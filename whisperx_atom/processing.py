from __future__ import annotations

import logging
import os
import copy
import hashlib
import subprocess
import threading
import time
import wave
from pathlib import Path
from typing import Any, Callable

from .contracts import ProcessingRequest, ProcessingResult, ProgressCallback
from .transcript_quality import (
    TranscriptQualityThresholds,
    build_transcript_quality_report,
    compare_transcript_quality,
    is_retryable_quality_failure,
    quality_gate,
)
from .audio_signal import language_quality, mute_wav_intervals
from diarization_quality import choose_best_diarization_candidate, diarization_profiles_for_processing_profile, score_diarization_result


LOGGER = logging.getLogger("whisperx.processing")


class ProcessingService:
    """Server-facing adapter around the proven legacy WhisperX pipeline."""

    IDLE_CACHE_SECONDS = max(60, int(os.getenv("WHISPERX_PIPELINE_IDLE_SECONDS", "900")))

    def __init__(self) -> None:
        self._pipeline: Any | None = None
        self._pipeline_fingerprint: tuple[Any, ...] | None = None
        self._last_used_monotonic = 0.0
        self._lock = threading.Lock()

    def _get_pipeline(self, config: Any) -> Any:
        fingerprint = (
            config.asr_model,
            config.asr_backend,
            config.device,
            config.compute_type,
        )
        with self._lock:
            if self._pipeline is None or self._pipeline_fingerprint != fingerprint:
                self._clear_pipeline_locked(clear_cuda=self._pipeline is not None)
                from app.transcription_pipeline import TranscriptionPipeline

                self._pipeline = TranscriptionPipeline(config)
                self._pipeline_fingerprint = fingerprint
            self._last_used_monotonic = time.monotonic()
            return self._pipeline

    def release_idle(self, force: bool = False) -> bool:
        with self._lock:
            if self._pipeline is None:
                return False
            if not force and time.monotonic() - self._last_used_monotonic < self.IDLE_CACHE_SECONDS:
                return False
            self._clear_pipeline_locked(clear_cuda=True)
            return True

    def close(self) -> None:
        self.release_idle(force=True)

    def _clear_pipeline_locked(self, clear_cuda: bool) -> None:
        pipeline, self._pipeline = self._pipeline, None
        self._pipeline_fingerprint = None
        self._last_used_monotonic = 0.0
        if pipeline is not None:
            try:
                pipeline.cache.clear()
            except Exception:
                LOGGER.debug("pipeline_cache_cleanup_failed", exc_info=True)
        if clear_cuda:
            try:
                import torch

                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                LOGGER.debug("cuda_cache_cleanup_failed", exc_info=True)

    def process(
        self,
        request: ProcessingRequest,
        progress: ProgressCallback | None = None,
        asr_ready: Callable[[dict[str, Any]], None] | None = None,
    ) -> ProcessingResult:
        if not request.media_path.is_file():
            raise FileNotFoundError(request.media_path)

        from app.transcription_pipeline import PipelineContext, PipelineConfig

        def report(stage: str, value: int) -> None:
            if progress:
                progress(stage, value)

        config = PipelineConfig.from_env()
        config.language = request.language or config.language
        config.min_speakers = max(1, request.min_speakers)
        config.max_speakers = max(config.min_speakers, request.max_speakers)
        asr_only = str(request.profile or "").strip().lower() in {"asr", "asr_draft", "transcribe_asr"}
        config.enable_alignment = not asr_only
        config.enable_diarization = not asr_only and os.getenv("DIARIZATION_MODE", "preferred").lower() != "disabled"
        thresholds = TranscriptQualityThresholds.from_env()
        pipeline = self._get_pipeline(config)
        # The resident object owns model caches, while request-scoped language
        # and speaker limits remain mutable per job.
        pipeline.config.language = config.language
        pipeline.config.min_speakers = config.min_speakers
        pipeline.config.max_speakers = config.max_speakers
        pipeline.config.enable_alignment = config.enable_alignment
        pipeline.config.enable_diarization = config.enable_diarization
        ctx = PipelineContext(job_id=request.job_id, audio_path=request.media_path)
        duration_seconds = _probe_duration_seconds(request.media_path)

        def emit_asr_ready(result: ProcessingResult) -> None:
            if asr_ready:
                asr_ready(result.to_dict())

        try:
            if str(request.profile or "").strip().lower() == "enrich":
                return self._process_enrichment(request, pipeline, ctx, config, duration_seconds, report)
            report("NORMALIZING", 10)
            source_audio_hash = _sha256_file(request.media_path)
            asr_source = request.media_path
            # Technical TTS markers are muted only in a derived ASR copy.  The
            # canonical recording stays byte-for-byte intact for playback and
            # export, while the spoken assistant response cannot contaminate
            # Russian ASR in a distant microphone.
            muted = mute_wav_intervals(request.media_path, request.technical_intervals)
            if muted is not None:
                asr_source = ctx.register_temp(muted) or request.media_path
            ctx.asr_audio_path, ctx.asr_preprocessing = pipeline.prepare_asr_input(asr_source, request.acoustic_profile)
            if ctx.asr_audio_path != request.media_path:
                ctx.register_temp(ctx.asr_audio_path)
            ctx.audio_signal_metrics = dict(ctx.asr_preprocessing.get("audio_signal_metrics") or {})
            # Transcript V1 is an ASR-only fast path. Do not run the second
            # diarization-oriented preprocessing pass when enrichment is
            # disabled; it adds latency and can fail a valid ASR job before the
            # first transcript is emitted.
            if config.enable_diarization:
                ctx.diar_audio_path = ctx.register_temp(pipeline._preprocess_audio(request.media_path, asr=False))
            else:
                ctx.diar_audio_path = None

            # A valid PCM file with no measurable signal is a normal terminal
            # outcome, not an ASR failure. Detect it before loading/running the
            # model so silent recordings cannot be reported as a generic
            # TRANSCRIPT_EMPTY (and do not spend GPU time on an empty decode).
            # _is_silent_pcm(ctx.asr_audio_path) remains the low-level fallback
            # inside _is_unusable_audio for legacy contract compatibility.
            if _is_unusable_audio(ctx.asr_audio_path, ctx.audio_signal_metrics):
                report("TRANSCRIBING", 30)
                no_speech = _build_no_speech_result(
                    request,
                    config,
                    duration_seconds,
                    ctx.asr_preprocessing,
                    source_audio_hash,
                    ctx.audio_signal_metrics,
                    request.acoustic_profile,
                )
                emit_asr_ready(no_speech)
                return no_speech

            report("TRANSCRIBING", 30)
            primary_result: dict[str, Any] = pipeline.run_asr_pass(ctx, config.vad_onset, config.chunk_size, config.asr_beam_size) or {}
            primary_report = build_transcript_quality_report(primary_result, duration_seconds, thresholds)
            primary_gate = quality_gate(primary_report, thresholds)
            fallback_report = None
            fallback_reason: str | None = None
            fallback_attempted = False
            selected_pass = "primary"
            result = primary_result

            if thresholds.fallback_enabled and is_retryable_quality_failure(primary_gate):
                fallback_attempted = True
                fallback_reason = ",".join(primary_report.reasons) or "quality_gate_failed"
                fallback_result = pipeline.run_asr_pass(
                    ctx, thresholds.fallback_vad_onset, thresholds.fallback_chunk_size, config.asr_beam_size
                ) or {}
                result, selected_pass, primary_report, fallback_report = compare_transcript_quality(
                    primary_result, fallback_result, duration_seconds, thresholds
                )

            selected_report = build_transcript_quality_report(result, duration_seconds, thresholds)
            selected_gate = quality_gate(selected_report, thresholds)
            if not (result.get("segments") or result.get("word_segments")) and _is_unusable_audio(ctx.asr_audio_path, ctx.audio_signal_metrics):
                no_speech = _build_no_speech_result(
                    request,
                    config,
                    duration_seconds,
                    ctx.asr_preprocessing,
                    source_audio_hash,
                    ctx.audio_signal_metrics,
                    request.acoustic_profile,
                )
                emit_asr_ready(no_speech)
                return no_speech
            if not selected_gate["valid"]:
                raise ValueError(selected_report.reasons[0] if selected_report.reasons else "TRANSCRIPT_EMPTY")
            asr_segments = copy.deepcopy(result.get("segments", []))
            for segment in asr_segments:
                if not segment.get("speaker"):
                    segment["speaker"] = "UNKNOWN"
            asr_text = " ".join(str(item.get("text", "")).strip() for item in asr_segments if item.get("text")).strip()
            asr_confidence_values = [float(item.get("confidence")) for item in asr_segments if isinstance(item.get("confidence"), (int, float))]
            asr_confidence = sum(asr_confidence_values) / len(asr_confidence_values) if asr_confidence_values else None
            language_report = language_quality(asr_text, config.language, asr_confidence)
            signal_warning = "AUDIO_SIGNAL_WEAK" if ctx.audio_signal_metrics.get("signal_state") == "WEAK" else None
            enhancement_attempted = False
            enhancement_reason = None
            # A valid-looking English hallucination on a weak Russian signal is
            # retried once with the explicit far-field profile.  Do not loop:
            # a second mismatch becomes a reviewable V1 warning.
            if language_report["mismatch"] and ctx.asr_preprocessing.get("preprocessing_profile") != "asr_far_field":
                enhancement_attempted = True
                enhancement_reason = "ASR_LANGUAGE_MISMATCH"
                enhanced_path = pipeline._preprocess_audio_profile(asr_source, "asr_far_field")
                ctx.register_temp(enhanced_path)
                original_path = ctx.asr_audio_path
                ctx.asr_audio_path = enhanced_path
                enhanced_result = pipeline.run_asr_pass(ctx, config.vad_onset, config.chunk_size, config.asr_beam_size) or {}
                enhanced_text = " ".join(str(item.get("text", "")).strip() for item in enhanced_result.get("segments", []) if item.get("text")).strip()
                enhanced_segments = enhanced_result.get("segments", []) or []
                enhanced_confidence_values = [float(item.get("confidence")) for item in enhanced_segments if isinstance(item.get("confidence"), (int, float))]
                enhanced_confidence = sum(enhanced_confidence_values) / len(enhanced_confidence_values) if enhanced_confidence_values else None
                enhanced_language = language_quality(enhanced_text, config.language, enhanced_confidence)
                enhanced_report = build_transcript_quality_report(enhanced_result, duration_seconds, thresholds)
                if not enhanced_language["mismatch"] or enhanced_report.quality_score > selected_report.quality_score:
                    result = enhanced_result
                    asr_segments = copy.deepcopy(result.get("segments", []))
                    for segment in asr_segments:
                        if not segment.get("speaker"):
                            segment["speaker"] = "UNKNOWN"
                    asr_text = enhanced_text
                    selected_report = enhanced_report
                    language_report = enhanced_language
                    selected_pass = "enhanced"
                    ctx.asr_preprocessing.update({"preprocessing_applied": True, "preprocessing_profile": "asr_far_field", "acoustic_profile": "LARGE_ROOM", "asr_enhancement_reason": enhancement_reason})
                else:
                    ctx.asr_audio_path = original_path
            draft_warnings = [code for code in ("ASR_LANGUAGE_MISMATCH" if language_report["mismatch"] else None, signal_warning, "AUDIO_SIGNAL_UNUSABLE" if ctx.audio_signal_metrics.get("signal_state") == "UNUSABLE" else None) if code]
            asr_draft = ProcessingResult(
                job_id=request.job_id,
                language=result.get("language") or config.language,
                text=asr_text,
                segments=asr_segments,
                word_segments=copy.deepcopy(result.get("word_segments", [])),
                metadata={
                    "model": config.asr_model,
                    "backend": config.asr_backend,
                    "device": config.device,
                    "compute_type": config.compute_type,
                    "processing_profile": request.profile,
                    "selected_asr_pass": selected_pass,
                    "asr_preprocessing": ctx.asr_preprocessing,
                    "asr_storage_key": request.source_storage_key,
                    "asr_sample_rate": 16000,
                    "asr_channels": 1,
                    "asr_duration_seconds": duration_seconds,
                    "asr_audio_hash": source_audio_hash,
                    "audio_signal_metrics": ctx.audio_signal_metrics,
                    "acoustic_profile": ctx.asr_preprocessing.get("acoustic_profile", request.acoustic_profile),
                    "asr_pass_count": 1 + int(enhancement_attempted),
                    "asr_selection_reason": "language_retry" if enhancement_attempted else "primary_quality",
                    "language_quality": language_report,
                },
                status="PARTIAL_READY",
                error_code="ASR_LANGUAGE_MISMATCH" if language_report["mismatch"] else ("AUDIO_SIGNAL_UNUSABLE" if ctx.audio_signal_metrics.get("signal_state") == "UNUSABLE" else None),
                warnings=draft_warnings,
                stage_outcomes={"ASR": "SUCCEEDED", "ALIGNMENT": "PENDING", "DIARIZATION": "PENDING"},
                quality={**selected_report.to_dict(), "asr_audio_hash": source_audio_hash, "audio_signal_metrics": ctx.audio_signal_metrics, "language_quality": language_report, "asr_pass_count": 1 + int(enhancement_attempted)},
            )
            emit_asr_ready(asr_draft)
            if asr_only:
                # V1 is the terminal result of TRANSCRIBE_ASR. Enrichment is
                # a separate job and must not be able to turn a persisted V1
                # into a failed ASR result after the user already has text.
                return asr_draft
            ctx.asr_result = result
            asr_recovery_result = copy.deepcopy(result)
            # ASR reasons drive fallback selection, but alignment can legitimately
            # repair span and word-timestamp warnings. Persist final quality
            # reasons below instead of leaking stale pre-alignment warnings.
            warnings: list[str] = []
            stage_outcomes: dict[str, str] = {"ASR": "SUCCEEDED"}

            report("ALIGNING", 55)
            if config.enable_alignment:
                try:
                    ctx.aligned_result = pipeline._align_result(ctx, result)
                    aligned = ctx.aligned_result or result
                    aligned_report = build_transcript_quality_report(aligned, duration_seconds, thresholds)
                    if quality_gate(aligned_report, thresholds)["valid"]:
                        result = aligned
                        stage_outcomes["ALIGNMENT"] = "SUCCEEDED"
                    else:
                        warnings.append("ALIGNMENT_FAILED")
                        stage_outcomes["ALIGNMENT"] = "FAILED"
                        report("ALIGNMENT_PARTIAL", 60)
                except Exception:
                    LOGGER.warning("alignment_failed job_id=%s", request.job_id, exc_info=True)
                    warnings.append("ALIGNMENT_FAILED")
                    stage_outcomes["ALIGNMENT"] = "FAILED"
                    report("ALIGNMENT_PARTIAL", 60)
            else:
                stage_outcomes["ALIGNMENT"] = "SKIPPED"

            report("DIARIZING", 75)
            diarization_quality: dict[str, Any] | None = None
            if config.enable_diarization:
                try:
                    candidates: list[dict[str, Any]] = []
                    primary_diar_result = pipeline._apply_diarization(ctx, copy.deepcopy(result), "diar")
                    primary_score = score_diarization_result(primary_diar_result.get("segments", []), ctx.diar_segments or [], "diar", config.min_speakers, config.max_speakers)
                    candidates.append({"profile": "diar", "result": primary_diar_result, "score": primary_score, "is_primary": True})
                    retry_score = float(os.getenv("DIARIZATION_QUALITY_RETRY_SCORE", "60"))
                    profiles = diarization_profiles_for_processing_profile(request.profile, "diar")
                    if primary_score.score < retry_score and len(profiles) > 1:
                        alternate_profile = profiles[1]
                        alternate_result = pipeline._apply_diarization(ctx, copy.deepcopy(result), alternate_profile)
                        alternate_score = score_diarization_result(alternate_result.get("segments", []), ctx.diar_segments or [], alternate_profile, config.min_speakers, config.max_speakers)
                        candidates.append({"profile": alternate_profile, "result": alternate_result, "score": alternate_score, "is_primary": False})
                    chosen = choose_best_diarization_candidate(candidates)
                    result = chosen["result"]
                    selected_score = chosen["score"]
                    diarization_quality = {
                        "selected_profile": chosen["profile"],
                        "selected": selected_score.to_dict(),
                        "candidates": [candidate["score"].to_dict() for candidate in candidates],
                        "retry_attempted": len(candidates) > 1,
                    }
                    if selected_score.score < retry_score:
                        warnings.append("DIARIZATION_LOW_QUALITY")
                        stage_outcomes["DIARIZATION"] = "PARTIAL"
                    else:
                        stage_outcomes["DIARIZATION"] = "SUCCEEDED"
                except Exception:
                    LOGGER.warning("diarization_failed job_id=%s", request.job_id, exc_info=True)
                    warnings.append("DIARIZATION_FAILED")
                    stage_outcomes["DIARIZATION"] = "FAILED"
                    report("DIARIZATION_PARTIAL", 80)
            else:
                warnings.append("DIARIZATION_DISABLED")
                stage_outcomes["DIARIZATION"] = "SKIPPED"

            for segment in result.get("segments", []):
                if not segment.get("speaker"):
                    segment["speaker"] = "UNKNOWN"

            report("QUALITY_CHECK", 90)
            result = pipeline._apply_glossary(result)
            final_report = build_transcript_quality_report(result, duration_seconds, thresholds)
            final_gate = quality_gate(final_report, thresholds)
            if not final_gate["valid"]:
                result = asr_recovery_result
                final_report = build_transcript_quality_report(result, duration_seconds, thresholds)
                final_gate = quality_gate(final_report, thresholds)
            if not final_gate["valid"]:
                raise ValueError(final_report.reasons[0] if final_report.reasons else "TRANSCRIPT_EMPTY")
            for reason in final_report.reasons:
                if reason not in warnings:
                    warnings.append(reason)

            segments = result.get("segments", [])
            text = " ".join(str(item.get("text", "")).strip() for item in segments if item.get("text"))
            quality = {
                **final_report.to_dict(),
                "primary_quality": primary_report.to_dict(),
                "fallback_quality": fallback_report.to_dict() if fallback_report else None,
                "fallback_reason": fallback_reason,
                "fallback_attempted": fallback_attempted,
                "primary_vad_onset": config.vad_onset,
                "primary_chunk_size": config.chunk_size,
                "fallback_vad_onset": thresholds.fallback_vad_onset if fallback_attempted else None,
                "fallback_chunk_size": thresholds.fallback_chunk_size if fallback_attempted else None,
                "fallback_effective": fallback_attempted,
                "selected_pass": selected_pass,
                "thresholds": thresholds.to_dict(),
                "stage_outcomes": stage_outcomes,
                "diarization": diarization_quality,
                "asr_preprocessing": ctx.asr_preprocessing,
            }
            metadata = {
                "model": config.asr_model,
                "backend": config.asr_backend,
                "device": config.device,
                "compute_type": config.compute_type,
                "speaker_count": len({item.get("speaker") for item in segments if item.get("speaker") and item.get("speaker") != "UNKNOWN"}),
                "word_count": len(result.get("word_segments", [])),
                "processing_profile": request.profile,
                "selected_asr_pass": selected_pass,
                "primary_vad_onset": config.vad_onset,
                "primary_chunk_size": config.chunk_size,
                "fallback_vad_onset": thresholds.fallback_vad_onset if fallback_attempted else None,
                "fallback_chunk_size": thresholds.fallback_chunk_size if fallback_attempted else None,
                "fallback_effective": fallback_attempted,
                **ctx.asr_preprocessing,
                "quality_thresholds": thresholds.to_dict(),
            }
            report("PERSISTING", 100)
            return ProcessingResult(
                job_id=request.job_id,
                language=result.get("language"),
                text=text.strip(),
                segments=segments,
                word_segments=result.get("word_segments", []),
                metadata=metadata,
                status="PARTIAL_READY" if warnings or not final_gate["ready"] else "READY",
                warnings=list(dict.fromkeys(warnings)),
                stage_outcomes=stage_outcomes,
                quality=quality,
            )
        except Exception as exc:
            if _is_cuda_oom(exc):
                # Release only on a real OOM. Successful jobs retain the
                # resident model cache for the next ASR request.
                with self._lock:
                    self._clear_pipeline_locked(clear_cuda=True)
            raise
        finally:
            with self._lock:
                # Idle eviction starts after the job actually finishes, not
                # when model acquisition began. Long ASR jobs must not be
                # immediately treated as idle on the next worker tick.
                self._last_used_monotonic = time.monotonic()
            pipeline._cleanup_ctx(ctx)

    def _process_enrichment(
        self,
        request: ProcessingRequest,
        pipeline: Any,
        ctx: Any,
        config: Any,
        duration_seconds: float | None,
        report: Callable[[str, int], None],
    ) -> ProcessingResult:
        """Run alignment/diarization over persisted ASR V1 without ASR."""
        source = copy.deepcopy(request.input_transcript or {})
        result: dict[str, Any] = {
            "language": source.get("language") or config.language,
            "segments": source.get("segments", []),
            "word_segments": source.get("word_segments", []),
        }
        expected_key = str((source.get("quality_metadata") or {}).get("asr_storage_key") or "").strip()
        actual_key = str(request.source_storage_key or "").strip()
        expected_hash = str((source.get("quality_metadata") or {}).get("asr_audio_hash") or "").strip().lower()
        # The worker-provided hash is an optimization/transport hint, not
        # proof of the bytes currently mounted at media_path. Re-hash the
        # canonical asset here so replacing a file behind the same storage key
        # cannot produce a V2 from different audio.
        actual_hash = _sha256_file(request.media_path).strip().lower()
        supplied_hash = str(request.source_audio_hash or "").strip().lower()
        expected_rate = (source.get("quality_metadata") or {}).get("asr_sample_rate")
        expected_channels = (source.get("quality_metadata") or {}).get("asr_channels")
        expected_duration = (source.get("quality_metadata") or {}).get("asr_duration_seconds")
        expected_preprocessing = (source.get("quality_metadata") or {}).get("asr_preprocessing")
        # The enrichment job must use the exact canonical asset that produced
        # V1. Treat a missing key as a mismatch too; silently accepting it
        # would allow alignment/diarization to run against a different media
        # object while leaving V1 apparently valid.
        if not expected_key or not actual_key or expected_key != actual_key:
            raise ValueError("ASR_INPUT_MISMATCH")
        if (
            len(expected_hash) != 64 or len(actual_hash) != 64 or expected_hash != actual_hash
            or (supplied_hash and supplied_hash != actual_hash)
            or not isinstance(expected_rate, (int, float)) or int(expected_rate) <= 0
            or not isinstance(expected_channels, (int, float)) or int(expected_channels) <= 0
            or not isinstance(expected_duration, (int, float)) or float(expected_duration) <= 0
            or not isinstance(expected_preprocessing, dict)
            or duration_seconds is None or abs(float(duration_seconds) - float(expected_duration)) > 0.1
        ):
            raise ValueError("ASR_INPUT_MISMATCH")
        # Canonical provenance compatibility marker: the legacy call was
        # prepare_asr_input(request.media_path); the profile is now explicit.
        ctx.asr_audio_path, actual_preprocessing = pipeline.prepare_asr_input(request.media_path, request.acoustic_profile)
        if ctx.asr_audio_path != request.media_path:
            ctx.register_temp(ctx.asr_audio_path)
        stable_keys = {"asr_input_path_kind", "preprocessing_mode", "preprocessing_applied", "preprocessing_profile", "acoustic_profile"}
        if any(actual_preprocessing.get(key) != expected_preprocessing.get(key) for key in stable_keys):
            raise ValueError("ASR_INPUT_MISMATCH")
        ctx.asr_preprocessing = actual_preprocessing
        ctx.asr_result = result
        if config.enable_diarization:
            diar_path = pipeline._preprocess_audio(request.media_path, asr=False)
            ctx.diar_audio_path = ctx.register_temp(diar_path)
        stage_outcomes: dict[str, str] = {"ASR": "REUSED_V1"}
        enrichment_metadata = source.get("quality_metadata") or {}
        warnings: list[str] = []
        if (enrichment_metadata.get("audio_signal_metrics") or {}).get("signal_state") == "WEAK":
            warnings.append("AUDIO_SIGNAL_WEAK")

        report("ALIGNING", 35)
        if config.enable_alignment:
            try:
                aligned = pipeline._align_result(ctx, result)
                result = aligned or result
                stage_outcomes["ALIGNMENT"] = "SUCCEEDED"
            except Exception:
                LOGGER.warning("enrichment_alignment_failed job_id=%s", request.job_id, exc_info=True)
                warnings.append("ALIGNMENT_FAILED")
                stage_outcomes["ALIGNMENT"] = "FAILED"
        else:
            stage_outcomes["ALIGNMENT"] = "SKIPPED"

        report("DIARIZING", 65)
        if config.enable_diarization:
            try:
                result = pipeline._apply_diarization(ctx, copy.deepcopy(result), "diar")
                stage_outcomes["DIARIZATION"] = "SUCCEEDED"
            except Exception:
                LOGGER.warning("enrichment_diarization_failed job_id=%s", request.job_id, exc_info=True)
                warnings.append("DIARIZATION_FAILED")
                stage_outcomes["DIARIZATION"] = "FAILED"
        else:
            warnings.append("DIARIZATION_DISABLED")
            stage_outcomes["DIARIZATION"] = "SKIPPED"

        for segment in result.get("segments", []):
            if not segment.get("speaker"):
                segment["speaker"] = "UNKNOWN"
        result = pipeline._apply_glossary(result)
        final_report = build_transcript_quality_report(result, duration_seconds, TranscriptQualityThresholds.from_env())
        # Preserve the canonical ASR provenance on V2 so diagnostics can prove
        # that enrichment reused the same preprocessing and storage asset.
        final_report_dict = final_report.to_dict()
        final_report_dict.update({
            key: enrichment_metadata[key]
            for key in ("asr_preprocessing", "asr_storage_key", "asr_sample_rate", "asr_channels", "asr_duration_seconds", "asr_audio_hash", "audio_signal_metrics", "acoustic_profile", "language_quality", "asr_pass_count")
            if key in enrichment_metadata
        })
        if not final_report.to_dict().get("segment_count") and not source.get("segments"):
            raise ValueError("TRANSCRIPT_INPUT_EMPTY")
        warnings.extend(reason for reason in final_report.reasons if reason not in warnings)
        text = " ".join(str(item.get("text", "")).strip() for item in result.get("segments", []) if item.get("text")).strip()
        return ProcessingResult(
            job_id=request.job_id,
            language=result.get("language") or config.language,
            text=text,
            segments=result.get("segments", []),
            word_segments=result.get("word_segments", []),
            metadata={
                "model": config.asr_model,
                "backend": config.asr_backend,
                "device": config.device,
                "compute_type": config.compute_type,
                "processing_profile": request.profile,
                "source_transcript_id": source.get("transcript_id"),
                "asr_preprocessing": enrichment_metadata.get("asr_preprocessing", {}),
                "asr_storage_key": enrichment_metadata.get("asr_storage_key"),
                "asr_sample_rate": enrichment_metadata.get("asr_sample_rate"),
                "asr_channels": enrichment_metadata.get("asr_channels"),
                "asr_duration_seconds": enrichment_metadata.get("asr_duration_seconds"),
                "asr_audio_hash": enrichment_metadata.get("asr_audio_hash"),
                "audio_signal_metrics": enrichment_metadata.get("audio_signal_metrics"),
                "acoustic_profile": enrichment_metadata.get("acoustic_profile", request.acoustic_profile),
                "language_quality": enrichment_metadata.get("language_quality"),
            },
            status="PARTIAL_READY" if warnings else "READY",
            warnings=list(dict.fromkeys(warnings)),
            stage_outcomes=stage_outcomes,
            quality=final_report_dict,
        )


def _is_cuda_oom(exc: BaseException) -> bool:
    text = f"{type(exc).__name__}: {exc}".lower()
    return "cuda" in text and ("out of memory" in text or "oom" in text)


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _probe_duration_seconds(path: Path) -> float | None:
    try:
        completed = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", str(path)],
            check=True,
            capture_output=True,
            text=True,
            timeout=15,
        )
        duration = float(completed.stdout.strip())
        return duration if duration > 0 else None
    except (OSError, ValueError, subprocess.SubprocessError):
        return None


def _is_silent_pcm(path: Path | None, threshold: float = 0.003) -> bool:
    """Return True only for a readable PCM WAV with no measurable signal."""
    if path is None or not path.is_file():
        return False
    try:
        # NumPy keeps the operation bounded by the WAV block size while
        # moving the sample loop into compiled code.  A two-hour recording is
        # therefore checked in a few-second windows instead of executing
        # hundreds of millions of Python exponentiations.
        import numpy as np

        with wave.open(str(path), "rb") as source:
            if source.getsampwidth() != 2 or source.getnchannels() < 1:
                return False
            total = 0
            sum_squares = 0.0
            while True:
                block = source.readframes(max(1, source.getframerate() * 5))
                if not block:
                    break
                values = np.frombuffer(block, dtype="<i2")
                if values.size == 0:
                    continue
                normalized = values.astype(np.float32) / 32768.0
                total += int(values.size)
                sum_squares += float(np.sum(normalized * normalized, dtype=np.float64))
            return total > 0 and (sum_squares / total) ** 0.5 < threshold
    except (OSError, EOFError, ValueError, ImportError):
        return False


def _is_unusable_audio(path: Path | None, metrics: dict[str, Any] | None) -> bool:
    """Treat only proven silence as no-speech; weak far-field audio still gets ASR."""
    state = str((metrics or {}).get("signal_state") or "").upper()
    if state:
        return state == "UNUSABLE"
    return _is_silent_pcm(path)


def _build_no_speech_result(
    request: ProcessingRequest,
    config: Any,
    duration_seconds: float | None,
    preprocessing: dict[str, Any] | None,
    audio_hash: str | None = None,
    audio_signal_metrics: dict[str, Any] | None = None,
    acoustic_profile: str = "AUTO",
) -> ProcessingResult:
    quality = {
        "quality_score": 0.0,
        "reasons": ["NO_SPEECH_DETECTED"],
        "segment_count": 0,
        "word_count": 0,
        "duration_seconds": duration_seconds,
        "audio_signal": "SILENT_PCM",
        "asr_preprocessing": preprocessing or {},
        "asr_storage_key": request.source_storage_key,
        "asr_sample_rate": 16000,
        "asr_channels": 1,
        "asr_duration_seconds": duration_seconds,
        "asr_audio_hash": audio_hash,
        "audio_signal_metrics": audio_signal_metrics or {},
        "acoustic_profile": acoustic_profile,
    }
    return ProcessingResult(
        job_id=request.job_id,
        language=config.language,
        text="",
        segments=[],
        word_segments=[],
        metadata={
            "model": config.asr_model,
            "backend": config.asr_backend,
            "device": config.device,
            "compute_type": config.compute_type,
            "processing_profile": request.profile,
            "audio_signal": "SILENT_PCM",
            "asr_storage_key": request.source_storage_key,
            "asr_sample_rate": 16000,
            "asr_channels": 1,
            "asr_duration_seconds": duration_seconds,
            "asr_audio_hash": audio_hash,
            "audio_signal_metrics": audio_signal_metrics or {},
            "acoustic_profile": acoustic_profile,
            **(preprocessing or {}),
        },
        status="PARTIAL_READY",
        error_code="NO_SPEECH_DETECTED",
        warnings=["NO_SPEECH_DETECTED"],
        stage_outcomes={"ASR": "SUCCEEDED", "ALIGNMENT": "SKIPPED", "DIARIZATION": "SKIPPED"},
        quality=quality,
    )


def validate_transcript_result(result: dict[str, Any], media_duration_seconds: float | None = None, tolerance_seconds: float = 2.0) -> dict[str, Any]:
    """Compatibility validator backed by the unified quality report."""
    thresholds = TranscriptQualityThresholds.from_env()
    thresholds = TranscriptQualityThresholds(**{**thresholds.to_dict(), "timestamp_tolerance_seconds": tolerance_seconds})
    report = build_transcript_quality_report(result, media_duration_seconds, thresholds)
    gate = quality_gate(report, thresholds)
    text = " ".join(str(item.get("text", "")).strip() for item in result.get("segments", []) or [] if item.get("text")).strip()
    error_code = report.reasons[0] if report.reasons else None
    if report.invalid_timestamp_count:
        error_code = "TRANSCRIPT_INVALID_TIMECODE"
    elif report.non_monotonic_segment_count:
        error_code = "TRANSCRIPT_INVALID_TIMECODE"
    return {
        "valid": gate["valid"],
        "error_code": error_code,
        "segment_count": report.segment_count,
        "text_length": len(text),
        "duration_seconds": (report.last_speech_ms or 0) / 1000.0,
        "media_duration_seconds": media_duration_seconds,
        "duration_tolerance_seconds": tolerance_seconds,
        "quality": report.to_dict(),
    }

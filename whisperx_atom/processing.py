from __future__ import annotations

import logging
import os
import copy
import hashlib
import subprocess
import wave
from pathlib import Path
from typing import Any, Callable

from .contracts import ProcessingRequest, ProcessingResult, ProgressCallback
from .asr_engine import AsrEngine, WhisperXAsrEngine
from .alignment_engine import AlignmentEngine, WhisperXAlignmentEngine
from .diarization_engine import DiarizationEngine, WhisperXDiarizationEngine
from .preprocessing_engine import PreprocessingEngine, WhisperXPreprocessingEngine
from .postprocessing_engine import PostprocessingEngine, WhisperXPostprocessingEngine
from .stage_result import StageResults
from .checkpoint_store import (
    NullPipelineCheckpointStore,
    PipelineCheckpointStore,
    checkpoint_fingerprint,
)
from .transcript_quality import (
    TranscriptQualityThresholds,
    build_transcript_quality_report,
    compare_transcript_quality,
    is_retryable_quality_failure,
    quality_gate,
)
from .audio_signal import language_quality, mute_wav_intervals
from .runtime import WhisperXRuntime
from .metrics import PipelineMetrics
from diarization_quality import choose_best_diarization_candidate, diarization_profiles_for_processing_profile, score_diarization_result


LOGGER = logging.getLogger("whisperx.processing")
ENRICHMENT_CHECKPOINT_REVISION = "2026-08-19-v1"


def _load_checkpoint_safely(
    store: PipelineCheckpointStore,
    job_id: str,
    stage: str,
    fingerprint: str,
    metrics: PipelineMetrics | None = None,
):
    try:
        checkpoint = store.load(job_id, stage, fingerprint)
        if metrics is not None:
            metrics.checkpoint(checkpoint is not None)
        return checkpoint
    except Exception:
        if metrics is not None:
            metrics.checkpoint(False)
        LOGGER.warning("checkpoint_load_failed job_id=%s stage=%s", job_id, stage, exc_info=True)
        return None


def _save_checkpoint_safely(
    store: PipelineCheckpointStore,
    job_id: str,
    stage: str,
    fingerprint: str,
    result: dict[str, Any],
):
    try:
        return store.save(job_id, stage, fingerprint, result)
    except Exception:
        # A checkpoint only avoids repeated GPU work. Failure to persist this
        # derived optimization must not downgrade a successfully completed
        # alignment/diarization stage.
        LOGGER.warning("checkpoint_save_failed job_id=%s stage=%s", job_id, stage, exc_info=True)
        return None


def _run_measured(
    metrics: PipelineMetrics,
    stage: str,
    callback: Callable[..., Any],
    *args: Any,
    **kwargs: Any,
) -> Any:
    """Run a stage while accumulating duration and CUDA-OOM diagnostics."""

    with metrics.measure(stage):
        return callback(*args, **kwargs)


def _collect_model_load_ms(metrics: PipelineMetrics, pipeline: Any) -> None:
    """Move lazy ASR/alignment/diarization load time into request metrics."""

    cache = getattr(pipeline, "cache", None)
    consume = getattr(cache, "consume_model_load_ms", None)
    if not callable(consume):
        return
    try:
        metrics.model_load_ms += float(consume() or 0.0)
    except Exception:
        # Metrics are advisory; a custom/fake pipeline must still process.
        LOGGER.debug("model_load_metric_failed", exc_info=True)


class ProcessingService:
    """Server-facing adapter around the proven legacy WhisperX pipeline."""

    IDLE_CACHE_SECONDS = max(60, int(os.getenv("WHISPERX_PIPELINE_IDLE_SECONDS", "900")))

    def __init__(
        self,
        asr_engine: AsrEngine | None = None,
        preprocessing_engine: PreprocessingEngine | None = None,
        alignment_engine: AlignmentEngine | None = None,
        diarization_engine: DiarizationEngine | None = None,
        postprocessing_engine: PostprocessingEngine | None = None,
        checkpoint_store: PipelineCheckpointStore | None = None,
        runtime: WhisperXRuntime | None = None,
    ) -> None:
        self._runtime = runtime or WhisperXRuntime(idle_cache_seconds=self.IDLE_CACHE_SECONDS)
        self._asr_engine = asr_engine or WhisperXAsrEngine()
        self._preprocessing_engine = preprocessing_engine or WhisperXPreprocessingEngine()
        self._alignment_engine = alignment_engine or WhisperXAlignmentEngine()
        self._diarization_engine = diarization_engine or WhisperXDiarizationEngine()
        self._postprocessing_engine = postprocessing_engine or WhisperXPostprocessingEngine()
        self._checkpoint_store = checkpoint_store or NullPipelineCheckpointStore()

    def _get_pipeline(self, config: Any) -> Any:
        return self._runtime.get_pipeline(config)

    def release_idle(self, force: bool = False) -> bool:
        return self._runtime.release_idle(force=force)

    def close(self) -> None:
        self._runtime.close()

    def _clear_pipeline_locked(self, clear_cuda: bool) -> None:
        # Kept as a compatibility seam for callers/tests. Ownership lives in
        # WhisperXRuntime so ProcessingService no longer imports the legacy
        # pipeline implementation or manages its lifetime.
        self._runtime.clear(clear_cuda=clear_cuda)

    def process(
        self,
        request: ProcessingRequest,
        progress: ProgressCallback | None = None,
        asr_ready: Callable[[dict[str, Any]], None] | None = None,
    ) -> ProcessingResult:
        if not request.media_path.is_file():
            raise FileNotFoundError(request.media_path)

        def report(stage: str, value: int) -> None:
            if progress:
                progress(stage, value)

        config = self._runtime.load_config()
        config.language = request.language or config.language
        config.min_speakers = max(1, request.min_speakers)
        config.max_speakers = max(config.min_speakers, request.max_speakers)
        asr_only = str(request.profile or "").strip().lower() in {"asr", "asr_draft", "transcribe_asr"}
        config.enable_alignment = not asr_only
        config.enable_diarization = not asr_only and os.getenv("DIARIZATION_MODE", "preferred").lower() != "disabled"
        thresholds = TranscriptQualityThresholds.from_env()
        metrics = PipelineMetrics(
            queue_wait_ms=float(request.queue_wait_ms or 0.0),
            media_prepare_ms=request.media_prepare_ms,
        )
        # Reset peak VRAM before model acquisition so the metric includes both
        # resident model load and request allocations. A reused model reports
        # model_load_ms=0 while still exposing its current peak footprint.
        pipeline = self._get_pipeline(config)
        # Runtime owns the resident implementation; stage engines receive only
        # the explicit facade and therefore do not depend on private legacy
        # method names.
        stage_pipeline = self._runtime.get_stage_adapter(pipeline)
        metrics.model_load_ms = self._runtime.last_model_load_ms
        # The resident object owns model caches, while request-scoped language
        # and speaker limits remain mutable per job.
        self._runtime.configure_request(pipeline, config)
        ctx = self._runtime.create_context(request.job_id, request.media_path)
        duration_seconds = _probe_duration_seconds(request.media_path)

        def emit_asr_ready(result: ProcessingResult) -> None:
            if asr_ready:
                asr_ready(result.to_dict())

        def attach_metrics(result: ProcessingResult) -> ProcessingResult:
            performance = metrics.to_dict(duration_seconds)
            result.metadata["pipeline_metrics"] = performance
            result.quality["pipeline_metrics"] = performance
            return result

        try:
            if str(request.profile or "").strip().lower() == "enrich":
                return self._process_enrichment(request, stage_pipeline, ctx, config, duration_seconds, report, metrics)
            report("NORMALIZING", 10)
            source_audio_hash = _run_measured(metrics, "normalize", _sha256_file, request.media_path)
            asr_source = request.media_path
            # Technical TTS markers are muted only in a derived ASR copy.  The
            # canonical recording stays byte-for-byte intact for playback and
            # export, while the spoken assistant response cannot contaminate
            # Russian ASR in a distant microphone.
            muted = _run_measured(metrics, "normalize", mute_wav_intervals, request.media_path, request.technical_intervals)
            if muted is not None:
                asr_source = ctx.register_temp(muted) or request.media_path
            ctx.asr_audio_path, ctx.asr_preprocessing = _run_measured(
                metrics,
                "normalize",
                self._preprocessing_engine.prepare_asr_input,
                stage_pipeline,
                asr_source,
                request.acoustic_profile,
            )
            if ctx.asr_audio_path != request.media_path:
                ctx.register_temp(ctx.asr_audio_path)
            ctx.audio_signal_metrics = dict(ctx.asr_preprocessing.get("audio_signal_metrics") or {})
            # Transcript V1 is an ASR-only fast path. Do not run the second
            # diarization-oriented preprocessing pass when enrichment is
            # disabled; it adds latency and can fail a valid ASR job before the
            # first transcript is emitted.
            if config.enable_diarization:
                ctx.diar_audio_path = ctx.register_temp(
                    _run_measured(metrics, "normalize", self._preprocessing_engine.prepare_diarization_input, stage_pipeline, request.media_path)
                )
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
                emit_asr_ready(attach_metrics(no_speech))
                return no_speech

            report("TRANSCRIBING", 30)
            primary_result = _run_measured(metrics, "asr", self._asr_engine.transcribe,
                stage_pipeline,
                ctx,
                vad_onset=config.vad_onset,
                chunk_size=config.chunk_size,
                beam_size=config.asr_beam_size,
            )
            _collect_model_load_ms(metrics, stage_pipeline)
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
                fallback_result = _run_measured(metrics, "asr", self._asr_engine.transcribe,
                    stage_pipeline,
                    ctx,
                    vad_onset=thresholds.fallback_vad_onset,
                    chunk_size=thresholds.fallback_chunk_size,
                    beam_size=config.asr_beam_size,
                )
                _collect_model_load_ms(metrics, stage_pipeline)
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
                emit_asr_ready(attach_metrics(no_speech))
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
                enhanced_path = _run_measured(metrics, "normalize", self._preprocessing_engine.prepare_profile, stage_pipeline, asr_source, "asr_far_field")
                ctx.register_temp(enhanced_path)
                original_path = ctx.asr_audio_path
                ctx.asr_audio_path = enhanced_path
                enhanced_result = _run_measured(metrics, "asr", self._asr_engine.transcribe,
                    stage_pipeline,
                    ctx,
                    vad_onset=config.vad_onset,
                    chunk_size=config.chunk_size,
                    beam_size=config.asr_beam_size,
                )
                _collect_model_load_ms(metrics, stage_pipeline)
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
                    "pipeline_metrics": metrics.to_dict(duration_seconds),
                },
                status="PARTIAL_READY",
                error_code="ASR_LANGUAGE_MISMATCH" if language_report["mismatch"] else ("AUDIO_SIGNAL_UNUSABLE" if ctx.audio_signal_metrics.get("signal_state") == "UNUSABLE" else None),
                warnings=draft_warnings,
                stage_outcomes=StageResults({"ASR": "SUCCEEDED", "ALIGNMENT": "PENDING", "DIARIZATION": "PENDING"}),
                quality={**selected_report.to_dict(), "asr_audio_hash": source_audio_hash, "audio_signal_metrics": ctx.audio_signal_metrics, "language_quality": language_report, "asr_pass_count": 1 + int(enhancement_attempted)},
            )
            emit_asr_ready(attach_metrics(asr_draft))
            if asr_only:
                # V1 is the terminal result of TRANSCRIBE_ASR. Enrichment is
                # a separate job and must not be able to turn a persisted V1
                # into a failed ASR result after the user already has text.
                return attach_metrics(asr_draft)
            ctx.asr_result = result
            asr_recovery_result = copy.deepcopy(result)
            # ASR reasons drive fallback selection, but alignment can legitimately
            # repair span and word-timestamp warnings. Persist final quality
            # reasons below instead of leaking stale pre-alignment warnings.
            warnings: list[str] = []
            stage_outcomes = StageResults({"ASR": "SUCCEEDED"})

            report("ALIGNING", 55)
            if config.enable_alignment:
                try:
                    ctx.aligned_result = _run_measured(metrics, "alignment", self._alignment_engine.align, stage_pipeline, ctx, result)
                    _collect_model_load_ms(metrics, stage_pipeline)
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
                    primary_diar_result = _run_measured(metrics, "diarization", self._diarization_engine.diarize,
                        stage_pipeline,
                        ctx,
                        copy.deepcopy(result),
                        "diar",
                    )
                    _collect_model_load_ms(metrics, stage_pipeline)
                    primary_score = score_diarization_result(primary_diar_result.get("segments", []), ctx.diar_segments or [], "diar", config.min_speakers, config.max_speakers)
                    candidates.append({"profile": "diar", "result": primary_diar_result, "score": primary_score, "is_primary": True})
                    retry_score = float(os.getenv("DIARIZATION_QUALITY_RETRY_SCORE", "60"))
                    profiles = diarization_profiles_for_processing_profile(request.profile, "diar")
                    if primary_score.score < retry_score and len(profiles) > 1:
                        alternate_profile = profiles[1]
                        alternate_result = _run_measured(metrics, "diarization", self._diarization_engine.diarize,
                            stage_pipeline,
                            ctx,
                            copy.deepcopy(result),
                            alternate_profile,
                        )
                        _collect_model_load_ms(metrics, stage_pipeline)
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
            result = _run_measured(metrics, "postprocess", self._postprocessing_engine.postprocess, stage_pipeline, ctx, result)
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
                "stage_outcomes": stage_outcomes.as_legacy_dict(),
                "diarization": diarization_quality,
                "asr_preprocessing": ctx.asr_preprocessing,
                "pipeline_metrics": metrics.to_dict(duration_seconds),
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
                "pipeline_metrics": metrics.to_dict(duration_seconds),
            }
            report("PERSISTING", 100)
            return attach_metrics(ProcessingResult(
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
            ))
        except Exception as exc:
            if _is_cuda_oom(exc):
                # Release only on a real OOM. Successful jobs retain the
                # resident model cache for the next ASR request.
                self._clear_pipeline_locked(clear_cuda=True)
            raise
        finally:
            self._runtime.cleanup_context(pipeline, ctx)

    def _process_enrichment(
        self,
        request: ProcessingRequest,
        pipeline: Any,
        ctx: Any,
        config: Any,
        duration_seconds: float | None,
        report: Callable[[str, int], None],
        metrics: PipelineMetrics | None = None,
    ) -> ProcessingResult:
        """Run alignment/diarization over persisted ASR V1 without ASR."""
        metrics = metrics or PipelineMetrics()
        source = copy.deepcopy(request.input_transcript or {})
        source_quality_metadata = source.get("quality_metadata") or {}
        # Enrichment is a separate job, but its V2 diagnostics should retain
        # the Media Worker preparation time measured for the canonical V1
        # asset.  Queue/model/stage timings remain specific to this job.
        source_pipeline_metrics = source_quality_metadata.get("pipeline_metrics") or {}
        if metrics.media_prepare_ms is None and source_pipeline_metrics.get("media_prepare_ms") is not None:
            try:
                metrics.media_prepare_ms = max(0.0, float(source_pipeline_metrics["media_prepare_ms"]))
                metrics.values["media_prepare_ms"] = metrics.media_prepare_ms
            except (TypeError, ValueError):
                pass
        result: dict[str, Any] = {
            "language": source.get("language") or config.language,
            "segments": source.get("segments", []),
            "word_segments": source.get("word_segments", []),
        }
        expected_key = str(source_quality_metadata.get("asr_storage_key") or "").strip()
        actual_key = str(request.source_storage_key or "").strip()
        expected_hash = str(source_quality_metadata.get("asr_audio_hash") or "").strip().lower()
        # The worker-provided hash is an optimization/transport hint, not
        # proof of the bytes currently mounted at media_path. Re-hash the
        # canonical asset here so replacing a file behind the same storage key
        # cannot produce a V2 from different audio.
        actual_hash = _sha256_file(request.media_path).strip().lower()
        supplied_hash = str(request.source_audio_hash or "").strip().lower()
        expected_rate = source_quality_metadata.get("asr_sample_rate")
        expected_channels = source_quality_metadata.get("asr_channels")
        expected_duration = source_quality_metadata.get("asr_duration_seconds")
        expected_preprocessing = source_quality_metadata.get("asr_preprocessing")
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
        ctx.asr_audio_path, actual_preprocessing = _run_measured(
            metrics,
            "normalize",
            self._preprocessing_engine.prepare_asr_input,
            pipeline,
            request.media_path,
            request.acoustic_profile,
        )
        if ctx.asr_audio_path != request.media_path:
            ctx.register_temp(ctx.asr_audio_path)
        stable_keys = {"asr_input_path_kind", "preprocessing_mode", "preprocessing_applied", "preprocessing_profile", "acoustic_profile"}
        if any(actual_preprocessing.get(key) != expected_preprocessing.get(key) for key in stable_keys):
            raise ValueError("ASR_INPUT_MISMATCH")
        ctx.asr_preprocessing = actual_preprocessing
        ctx.asr_result = result
        if config.enable_diarization:
            diar_path = _run_measured(metrics, "normalize", self._preprocessing_engine.prepare_diarization_input, pipeline, request.media_path)
            ctx.diar_audio_path = ctx.register_temp(diar_path)
        stage_outcomes = StageResults({"ASR": "REUSED_V1"})
        enrichment_metadata = source_quality_metadata
        warnings: list[str] = []
        if (enrichment_metadata.get("audio_signal_metrics") or {}).get("signal_state") == "WEAK":
            warnings.append("AUDIO_SIGNAL_WEAK")

        checkpoint_base = checkpoint_fingerprint({
            "schema": 1,
            "algorithm_revision": ENRICHMENT_CHECKPOINT_REVISION,
            "release": os.getenv("WHISPERX_RELEASE_VERSION") or os.getenv("APP_VERSION") or "dev",
            "source_transcript_id": source.get("transcript_id"),
            "source_audio_hash": actual_hash,
            "language": result.get("language") or config.language,
            "profile": request.profile,
            "acoustic_profile": request.acoustic_profile,
            "alignment_enabled": bool(config.enable_alignment),
            "diarization_enabled": bool(config.enable_diarization),
            "preprocessing": {key: actual_preprocessing.get(key) for key in sorted(stable_keys)},
        })

        report("ALIGNING", 35)
        alignment_artifact_hash = checkpoint_fingerprint({"result": result})
        if config.enable_alignment:
            alignment_checkpoint = _load_checkpoint_safely(
                self._checkpoint_store,
                request.job_id,
                "ALIGNMENT",
                checkpoint_base,
                metrics,
            )
            if alignment_checkpoint is not None:
                result = copy.deepcopy(alignment_checkpoint.result)
                ctx.aligned_result = result
                alignment_artifact_hash = alignment_checkpoint.artifact_sha256
                stage_outcomes.set(
                    "ALIGNMENT",
                    "SUCCEEDED",
                    metadata={
                        "artifact_key": alignment_checkpoint.artifact_key,
                        "artifact_hash": alignment_checkpoint.artifact_sha256,
                        "reason": "checkpoint_reused",
                    },
                )
            else:
                try:
                    aligned = _run_measured(metrics, "alignment", self._alignment_engine.align, pipeline, ctx, result)
                    _collect_model_load_ms(metrics, pipeline)
                    result = aligned or result
                    ctx.aligned_result = result
                    checkpoint = _save_checkpoint_safely(
                        self._checkpoint_store,
                        request.job_id,
                        "ALIGNMENT",
                        checkpoint_base,
                        result,
                    )
                    if checkpoint is not None:
                        alignment_artifact_hash = checkpoint.artifact_sha256
                    stage_outcomes.set(
                        "ALIGNMENT",
                        "SUCCEEDED",
                        metadata={
                            "artifact_key": checkpoint.artifact_key if checkpoint else "",
                            "artifact_hash": checkpoint.artifact_sha256 if checkpoint else alignment_artifact_hash,
                        },
                    )
                except Exception:
                    LOGGER.warning("enrichment_alignment_failed job_id=%s", request.job_id, exc_info=True)
                    warnings.append("ALIGNMENT_FAILED")
                    stage_outcomes.set("ALIGNMENT", "FAILED", warnings=("ALIGNMENT_FAILED",))
        else:
            stage_outcomes["ALIGNMENT"] = "SKIPPED"

        report("DIARIZING", 65)
        if config.enable_diarization:
            diarization_fingerprint = checkpoint_fingerprint({
                "base": checkpoint_base,
                "alignment_artifact_hash": alignment_artifact_hash,
                "profile": "diar",
            })
            diarization_checkpoint = _load_checkpoint_safely(
                self._checkpoint_store,
                request.job_id,
                "DIARIZATION",
                diarization_fingerprint,
                metrics,
            )
            if diarization_checkpoint is not None:
                result = copy.deepcopy(diarization_checkpoint.result)
                stage_outcomes.set(
                    "DIARIZATION",
                    "SUCCEEDED",
                    metadata={
                        "artifact_key": diarization_checkpoint.artifact_key,
                        "artifact_hash": diarization_checkpoint.artifact_sha256,
                        "reason": "checkpoint_reused",
                    },
                )
            else:
                try:
                    result = _run_measured(metrics, "diarization", self._diarization_engine.diarize,
                        pipeline,
                        ctx,
                        copy.deepcopy(result),
                        "diar",
                    )
                    _collect_model_load_ms(metrics, pipeline)
                    checkpoint = _save_checkpoint_safely(
                        self._checkpoint_store,
                        request.job_id,
                        "DIARIZATION",
                        diarization_fingerprint,
                        result,
                    )
                    stage_outcomes.set(
                        "DIARIZATION",
                        "SUCCEEDED",
                        metadata={
                            "artifact_key": checkpoint.artifact_key if checkpoint else "",
                            "artifact_hash": checkpoint.artifact_sha256 if checkpoint else checkpoint_fingerprint({"result": result}),
                        },
                    )
                except Exception:
                    LOGGER.warning("enrichment_diarization_failed job_id=%s", request.job_id, exc_info=True)
                    warnings.append("DIARIZATION_FAILED")
                    stage_outcomes.set("DIARIZATION", "FAILED", warnings=("DIARIZATION_FAILED",))
        else:
            warnings.append("DIARIZATION_DISABLED")
            stage_outcomes["DIARIZATION"] = "SKIPPED"

        for segment in result.get("segments", []):
            if not segment.get("speaker"):
                segment["speaker"] = "UNKNOWN"
        result = _run_measured(metrics, "postprocess", self._postprocessing_engine.postprocess, pipeline, ctx, result)
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
                "pipeline_metrics": metrics.to_dict(duration_seconds),
            },
            status="PARTIAL_READY" if warnings else "READY",
            warnings=list(dict.fromkeys(warnings)),
            stage_outcomes=stage_outcomes,
            quality={**final_report_dict, "pipeline_metrics": metrics.to_dict(duration_seconds)},
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
        stage_outcomes=StageResults({"ASR": "SUCCEEDED", "ALIGNMENT": "SKIPPED", "DIARIZATION": "SKIPPED"}),
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

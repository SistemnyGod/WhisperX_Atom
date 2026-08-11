from __future__ import annotations

import gc
import os
import copy
import subprocess
from pathlib import Path
from typing import Any

from .contracts import ProcessingRequest, ProcessingResult, ProgressCallback
from .transcript_quality import (
    TranscriptQualityThresholds,
    build_transcript_quality_report,
    compare_transcript_quality,
    is_retryable_quality_failure,
    quality_gate,
)
from diarization_quality import choose_best_diarization_candidate, diarization_profiles_for_processing_profile, score_diarization_result


class ProcessingService:
    """Server-facing adapter around the proven legacy WhisperX pipeline."""

    def process(self, request: ProcessingRequest, progress: ProgressCallback | None = None) -> ProcessingResult:
        if not request.media_path.is_file():
            raise FileNotFoundError(request.media_path)

        from app.transcription_pipeline import PipelineContext, PipelineConfig, TranscriptionPipeline

        def report(stage: str, value: int) -> None:
            if progress:
                progress(stage, value)

        config = PipelineConfig.from_env()
        config.language = request.language or config.language
        config.min_speakers = max(1, request.min_speakers)
        config.max_speakers = max(config.min_speakers, request.max_speakers)
        config.enable_alignment = True
        config.enable_diarization = os.getenv("DIARIZATION_MODE", "preferred").lower() != "disabled"
        thresholds = TranscriptQualityThresholds.from_env()
        pipeline = TranscriptionPipeline(config)
        ctx = PipelineContext(job_id=request.job_id, audio_path=request.media_path)
        duration_seconds = _probe_duration_seconds(request.media_path)

        try:
            report("NORMALIZING", 10)
            ctx.asr_audio_path = ctx.register_temp(pipeline._preprocess_audio(request.media_path, asr=True))
            ctx.diar_audio_path = ctx.register_temp(pipeline._preprocess_audio(request.media_path, asr=False))

            report("TRANSCRIBING", 30)
            primary_result: dict[str, Any] = pipeline._run_asr(ctx) or {}
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
                original_vad_onset = config.vad_onset
                original_chunk_size = config.chunk_size
                try:
                    config.vad_onset = thresholds.fallback_vad_onset
                    config.chunk_size = thresholds.fallback_chunk_size
                    fallback_result = pipeline._run_asr(ctx) or {}
                    result, selected_pass, primary_report, fallback_report = compare_transcript_quality(
                        primary_result, fallback_result, duration_seconds, thresholds
                    )
                finally:
                    config.vad_onset = original_vad_onset
                    config.chunk_size = original_chunk_size

            selected_report = build_transcript_quality_report(result, duration_seconds, thresholds)
            selected_gate = quality_gate(selected_report, thresholds)
            if not selected_gate["valid"]:
                raise ValueError(selected_report.reasons[0] if selected_report.reasons else "TRANSCRIPT_EMPTY")
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
                "selected_pass": selected_pass,
                "thresholds": thresholds.to_dict(),
                "stage_outcomes": stage_outcomes,
                "diarization": diarization_quality,
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
        finally:
            pipeline._cleanup_ctx(ctx)
            pipeline.cache.clear()
            gc.collect()
            try:
                import torch

                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                pass


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

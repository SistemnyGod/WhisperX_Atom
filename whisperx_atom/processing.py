from __future__ import annotations

import gc
import os
import subprocess
from pathlib import Path
from typing import Any

from .contracts import ProcessingRequest, ProcessingResult, ProgressCallback


class ProcessingService:
    """Server-facing adapter around the proven legacy WhisperX pipeline."""

    def process(
        self,
        request: ProcessingRequest,
        progress: ProgressCallback | None = None,
    ) -> ProcessingResult:
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

        pipeline = TranscriptionPipeline(config)
        ctx = PipelineContext(job_id=request.job_id, audio_path=request.media_path)
        try:
            report("NORMALIZING", 10)
            ctx.asr_audio_path = pipeline._preprocess_audio(request.media_path, asr=True)
            ctx.diar_audio_path = pipeline._preprocess_audio(request.media_path, asr=False)

            report("TRANSCRIBING", 30)
            ctx.asr_result = pipeline._run_asr(ctx)
            result: dict[str, Any] = ctx.asr_result or {}
            warnings: list[str] = []
            stage_outcomes: dict[str, str] = {"ASR": "SUCCEEDED"}

            if not result.get("segments") or not any(str(item.get("text", "")).strip() for item in result.get("segments", [])):
                raise ValueError("transcript_empty")

            report("ALIGNING", 55)
            if config.enable_alignment:
                try:
                    ctx.aligned_result = pipeline._align_result(ctx, result)
                    result = ctx.aligned_result or result
                    stage_outcomes["ALIGNMENT"] = "SUCCEEDED"
                except Exception as exc:
                    warnings.append("ALIGNMENT_FAILED")
                    stage_outcomes["ALIGNMENT"] = "FAILED"
                    report("ALIGNMENT_PARTIAL", 60)
            else:
                stage_outcomes["ALIGNMENT"] = "SKIPPED"

            report("DIARIZING", 75)
            if config.enable_diarization:
                try:
                    result = pipeline._apply_diarization(ctx, result)
                    stage_outcomes["DIARIZATION"] = "SUCCEEDED"
                except Exception as exc:
                    warnings.append("DIARIZATION_FAILED")
                    stage_outcomes["DIARIZATION"] = "FAILED"
                    report("DIARIZATION_PARTIAL", 80)
            else:
                warnings.append("DIARIZATION_DISABLED")
                stage_outcomes["DIARIZATION"] = "SKIPPED"

            for segment in result.get("segments", []):
                if not segment.get("speaker"):
                    segment["speaker"] = "Не определён"

            report("QUALITY_CHECK", 90)
            result = pipeline._apply_glossary(result)
            segments = result.get("segments", [])
            text = " ".join(str(item.get("text", "")).strip() for item in segments if item.get("text"))
            quality = validate_transcript_result(result, _probe_duration_seconds(request.media_path))
            if not quality["valid"]:
                raise ValueError(quality["error_code"])
            metadata = {
                "model": config.asr_model,
                "backend": config.asr_backend,
                "device": config.device,
                "compute_type": config.compute_type,
                "speaker_count": len({item.get("speaker") for item in segments if item.get("speaker")}),
                "word_count": len(result.get("word_segments", [])),
            }

            report("PERSISTING", 100)
            return ProcessingResult(
                job_id=request.job_id,
                language=result.get("language"),
                text=text.strip(),
                segments=segments,
                word_segments=result.get("word_segments", []),
                metadata=metadata,
                status="PARTIAL_READY" if warnings else "READY",
                warnings=warnings,
                stage_outcomes=stage_outcomes,
                quality=quality,
            )
        finally:
            # Keep server-side derivatives available for retry and cleanup only
            # temporary .asr/.diar files created by the legacy pipeline.
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
    """Validate the minimum invariant required before exposing a transcript."""
    segments = result.get("segments") or []
    text = " ".join(str(item.get("text", "")).strip() for item in segments if item.get("text")).strip()
    if not segments or not text:
        return {"valid": False, "error_code": "TRANSCRIPT_EMPTY", "segment_count": len(segments), "text_length": len(text)}
    previous_start = -1.0
    for item in segments:
        try:
            start = float(item.get("start", 0))
            end = float(item.get("end", 0))
        except (TypeError, ValueError):
            return {"valid": False, "error_code": "TRANSCRIPT_INVALID_TIMECODE", "segment_count": len(segments), "text_length": len(text)}
        if start < 0 or end < start or start < previous_start:
            return {"valid": False, "error_code": "TRANSCRIPT_INVALID_TIMECODE", "segment_count": len(segments), "text_length": len(text)}
        previous_start = start
    duration_seconds = max(float(item.get("end", 0)) for item in segments)
    if media_duration_seconds is not None and duration_seconds > media_duration_seconds + tolerance_seconds:
        return {
            "valid": False,
            "error_code": "TRANSCRIPT_OUTSIDE_MEDIA",
            "segment_count": len(segments),
            "text_length": len(text),
            "duration_seconds": duration_seconds,
            "media_duration_seconds": media_duration_seconds,
            "duration_tolerance_seconds": tolerance_seconds,
        }
    return {
        "valid": True,
        "error_code": None,
        "segment_count": len(segments),
        "text_length": len(text),
        "duration_seconds": duration_seconds,
        "media_duration_seconds": media_duration_seconds,
        "duration_tolerance_seconds": tolerance_seconds,
    }

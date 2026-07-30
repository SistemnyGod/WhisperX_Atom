from __future__ import annotations

from pathlib import Path
from typing import Any

from .contracts import ProcessingRequest, ProcessingResult, ProgressCallback


class ProcessingService:
    """Server-facing adapter around the proven legacy WhisperX pipeline.

    The import is deliberately lazy: API/media tests can run without CUDA or
    WhisperX installed, while the GPU worker gets the full existing pipeline.
    """

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
        config.enable_diarization = True

        pipeline = TranscriptionPipeline(config)
        ctx = PipelineContext(job_id=request.job_id, audio_path=request.media_path)

        report("NORMALIZING", 10)
        ctx.asr_audio_path = pipeline._preprocess_audio(request.media_path, asr=True)
        ctx.diar_audio_path = pipeline._preprocess_audio(request.media_path, asr=False)

        report("TRANSCRIBING", 30)
        ctx.asr_result = pipeline._run_asr(ctx)

        report("ALIGNING", 55)
        ctx.aligned_result = (
            pipeline._align_result(ctx, ctx.asr_result)
            if config.enable_alignment
            else ctx.asr_result
        )

        report("DIARIZING", 75)
        result: dict[str, Any] = ctx.aligned_result or ctx.asr_result or {}
        if config.enable_diarization:
            result = pipeline._apply_diarization(ctx, result)

        report("QUALITY_CHECK", 90)
        result = pipeline._apply_glossary(result)
        segments = result.get("segments", [])
        text = " ".join(str(item.get("text", "")).strip() for item in segments if item.get("text"))
        metadata = {
            "model": config.asr_model,
            "backend": config.asr_backend,
            "device": config.device,
            "compute_type": config.compute_type,
            "speaker_count": len({item.get("speaker") for item in segments if item.get("speaker")}),
            "word_count": len(result.get("word_segments", [])),
        }

        pipeline._cleanup_ctx(ctx)
        if request.media_path.name == "asr.wav":
            request.media_path.unlink(missing_ok=True)
        report("PERSISTING", 100)
        return ProcessingResult(
            job_id=request.job_id,
            language=result.get("language"),
            text=text.strip(),
            segments=segments,
            word_segments=result.get("word_segments", []),
            metadata=metadata,
        )


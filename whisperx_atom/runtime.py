"""Runtime boundary for the resident WhisperX implementation.

The public pipeline no longer needs to know where the current WhisperX
implementation lives.  This module is the only compatibility boundary to the
legacy ``app.transcription_pipeline`` package.  Keeping model lifetime and
request context creation here makes it possible to replace individual stages
without changing the worker/API contract.
"""

from __future__ import annotations

import logging
import os
import threading
import time
from typing import Any


LOGGER = logging.getLogger("whisperx.runtime")


class LegacyPipelineStageAdapter:
    """Explicit stage-facing facade over the legacy WhisperX pipeline.

    ``ProcessingService`` and the stage engines must not know about private
    ``TranscriptionPipeline`` method names.  This small object is the only
    place where those names are translated.  It is intentionally thin: model
    ownership, context lifetime and request configuration stay with
    :class:`WhisperXRuntime`, while each stage receives a stable, replaceable
    contract.  The adapter can be removed one method at a time when the
    corresponding native engine is implemented.
    """

    def __init__(self, pipeline: Any) -> None:
        self._pipeline = pipeline

    @property
    def cache(self) -> Any:
        """Expose the resident cache for metrics without leaking the pipeline."""

        return getattr(self._pipeline, "cache", None)

    def prepare_asr_input(self, input_path: Any, acoustic_profile: str = "AUTO") -> Any:
        return self._pipeline.prepare_asr_input(input_path, acoustic_profile)

    def prepare_diarization_input(self, input_path: Any) -> Any:
        native = getattr(self._pipeline, "prepare_diarization_input", None)
        if callable(native):
            return native(input_path)
        return self._pipeline._preprocess_audio(input_path, asr=False)

    def prepare_profile(self, input_path: Any, profile: str) -> Any:
        native = getattr(self._pipeline, "prepare_profile", None)
        if callable(native):
            return native(input_path, profile)
        return self._pipeline._preprocess_audio_profile(input_path, profile)

    def transcribe(self, context: Any, *, vad_onset: float, chunk_size: int, beam_size: int) -> Any:
        native = getattr(self._pipeline, "transcribe", None)
        if callable(native):
            return native(context, vad_onset=vad_onset, chunk_size=chunk_size, beam_size=beam_size)
        return self._pipeline.run_asr_pass(context, vad_onset, chunk_size, beam_size)

    def align(self, context: Any, result: dict[str, Any]) -> Any:
        native = getattr(self._pipeline, "align", None)
        if callable(native):
            return native(context, result)
        return self._pipeline._align_result(context, result)

    def diarize(self, context: Any, result: dict[str, Any], profile: str = "diar") -> Any:
        native = getattr(self._pipeline, "diarize", None)
        if callable(native):
            return native(context, result, profile)
        return self._pipeline._apply_diarization(context, result, profile)

    def postprocess(self, result: dict[str, Any]) -> Any:
        native = getattr(self._pipeline, "postprocess", None)
        if callable(native):
            return native(result)
        return self._pipeline._apply_glossary(result)


class WhisperXRuntime:
    """Own a resident pipeline and request-scoped legacy context objects."""

    def __init__(self, idle_cache_seconds: int | None = None) -> None:
        self.idle_cache_seconds = max(
            60,
            int(idle_cache_seconds if idle_cache_seconds is not None else os.getenv("WHISPERX_PIPELINE_IDLE_SECONDS", "900")),
        )
        self._pipeline: Any | None = None
        self._pipeline_fingerprint: tuple[Any, ...] | None = None
        self._last_used_monotonic = 0.0
        self._last_model_load_ms = 0.0
        self._lock = threading.Lock()

    @property
    def last_model_load_ms(self) -> float:
        return self._last_model_load_ms

    @property
    def resident(self) -> bool:
        with self._lock:
            return self._pipeline is not None

    def load_config(self) -> Any:
        # Deliberately keep the compatibility import in this module only.
        from app.transcription_pipeline import PipelineConfig

        return PipelineConfig.from_env()

    def create_context(self, job_id: str, audio_path: Any) -> Any:
        from app.transcription_pipeline import PipelineContext

        return PipelineContext(job_id=job_id, audio_path=audio_path)

    @staticmethod
    def _fingerprint(config: Any) -> tuple[Any, ...]:
        return (
            config.asr_model,
            config.asr_backend,
            config.device,
            config.compute_type,
        )

    def get_pipeline(self, config: Any) -> Any:
        fingerprint = self._fingerprint(config)
        with self._lock:
            if self._pipeline is None or self._pipeline_fingerprint != fingerprint:
                self._clear_locked(clear_cuda=self._pipeline is not None)
                started = time.perf_counter()
                # Compatibility construction remains isolated to this runtime
                # adapter.  New stage implementations can replace it later.
                from app.transcription_pipeline import TranscriptionPipeline

                self._pipeline = TranscriptionPipeline(config)
                self._last_model_load_ms = round((time.perf_counter() - started) * 1000.0, 3)
                self._pipeline_fingerprint = fingerprint
            else:
                self._last_model_load_ms = 0.0
            self._last_used_monotonic = time.monotonic()
            return self._pipeline

    @staticmethod
    def get_stage_adapter(pipeline: Any) -> LegacyPipelineStageAdapter:
        """Return the stable stage facade for a resident pipeline.

        Keeping this conversion in the runtime means orchestration can pass a
        stage contract around without importing or depending on legacy
        implementation details.  The identity check also keeps nested calls
        (for example direct enrichment tests) idempotent.
        """

        if isinstance(pipeline, LegacyPipelineStageAdapter):
            return pipeline
        return LegacyPipelineStageAdapter(pipeline)

    def configure_request(self, pipeline: Any, config: Any) -> None:
        """Apply request-scoped options while retaining resident model state."""

        pipeline.config.language = config.language
        pipeline.config.min_speakers = config.min_speakers
        pipeline.config.max_speakers = config.max_speakers
        pipeline.config.enable_alignment = config.enable_alignment
        pipeline.config.enable_diarization = config.enable_diarization

    def cleanup_context(self, pipeline: Any, context: Any) -> None:
        cleanup = getattr(pipeline, "_cleanup_ctx", None)
        if callable(cleanup):
            cleanup(context)

    def release_idle(self, force: bool = False) -> bool:
        with self._lock:
            if self._pipeline is None:
                return False
            if not force and time.monotonic() - self._last_used_monotonic < self.idle_cache_seconds:
                return False
            self._clear_locked(clear_cuda=True)
            return True

    def close(self) -> None:
        self.release_idle(force=True)

    def clear(self, clear_cuda: bool = True) -> None:
        with self._lock:
            self._clear_locked(clear_cuda=clear_cuda)

    def _clear_locked(self, clear_cuda: bool) -> None:
        pipeline, self._pipeline = self._pipeline, None
        self._pipeline_fingerprint = None
        self._last_used_monotonic = 0.0
        if pipeline is not None:
            try:
                cache = getattr(pipeline, "cache", None)
                if cache is not None:
                    cache.clear()
            except Exception:
                LOGGER.debug("pipeline_cache_cleanup_failed", exc_info=True)
        if clear_cuda:
            try:
                import torch

                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                LOGGER.debug("cuda_cache_cleanup_failed", exc_info=True)

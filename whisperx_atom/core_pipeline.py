"""Stable application boundary for all server-side transcription requests.

The implementation deliberately delegates to the existing, tested
``ProcessingService``.  Callers depend on this small facade instead of
importing the legacy ``app.transcription_pipeline`` module, which lets us
extract preprocessing, ASR, alignment, diarization and postprocessing one
stage at a time.
"""

from __future__ import annotations

from typing import Callable, Protocol

from .alignment_engine import AlignmentEngine
from .asr_engine import AsrEngine
from .contracts import ProcessingRequest, ProcessingResult, ProgressCallback
from .diarization_engine import DiarizationEngine
from .pipeline_contract import PipelineStageTracker
from .preprocessing_engine import PreprocessingEngine
from .postprocessing_engine import PostprocessingEngine
from .checkpoint_store import PipelineCheckpointStore
from .processing import ProcessingService


class CorePipeline(Protocol):
    """Application-level contract shared by API adapters and workers."""

    def process(
        self,
        request: ProcessingRequest,
        progress: ProgressCallback | None = None,
        asr_ready: Callable[[dict[str, object]], None] | None = None,
    ) -> ProcessingResult:
        ...

    def release_idle(self, force: bool = False) -> bool:
        ...

    def close(self) -> None:
        ...


class WhisperXCorePipeline:
    """Canonical WhisperX implementation behind the Core Pipeline contract."""

    def __init__(
        self,
        service: ProcessingService | None = None,
        *,
        asr_engine: AsrEngine | None = None,
        preprocessing_engine: PreprocessingEngine | None = None,
        alignment_engine: AlignmentEngine | None = None,
        diarization_engine: DiarizationEngine | None = None,
        postprocessing_engine: PostprocessingEngine | None = None,
        checkpoint_store: PipelineCheckpointStore | None = None,
    ) -> None:
        if service is not None and any(
            dependency is not None
            for dependency in (asr_engine, preprocessing_engine, alignment_engine, diarization_engine, postprocessing_engine, checkpoint_store)
        ):
            raise ValueError("CORE_PIPELINE_SERVICE_AND_DEPENDENCIES_ARE_MUTUALLY_EXCLUSIVE")
        self._service = service or ProcessingService(
            asr_engine=asr_engine,
            preprocessing_engine=preprocessing_engine,
            alignment_engine=alignment_engine,
            diarization_engine=diarization_engine,
            postprocessing_engine=postprocessing_engine,
            checkpoint_store=checkpoint_store,
        )

    def process(
        self,
        request: ProcessingRequest,
        progress: ProgressCallback | None = None,
        asr_ready: Callable[[dict[str, object]], None] | None = None,
    ) -> ProcessingResult:
        tracker = PipelineStageTracker(progress)
        return self._service.process(request, tracker.emit, asr_ready)

    def release_idle(self, force: bool = False) -> bool:
        return self._service.release_idle(force=force)

    def close(self) -> None:
        self._service.close()

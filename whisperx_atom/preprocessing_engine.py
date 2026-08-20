"""Preprocessing boundary for canonical ASR and diarization inputs.

The current WhisperX implementation still owns FFmpeg filters, acoustic
profiles and signal analysis.  This adapter keeps those decisions in the
proven runtime while making preprocessing an application dependency that can
be replaced or tested independently of the worker and API.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Protocol


class PreprocessingEngine(Protocol):
    """Request-scoped contract for derived audio preparation."""

    def prepare_asr_input(
        self,
        pipeline: Any,
        input_path: Path,
        acoustic_profile: str = "AUTO",
    ) -> tuple[Path, dict[str, Any]]:
        ...

    def prepare_diarization_input(self, pipeline: Any, input_path: Path) -> Path:
        ...

    def prepare_profile(self, pipeline: Any, input_path: Path, profile: str) -> Path:
        ...


class WhisperXPreprocessingEngine:
    """Compatibility adapter around the existing WhisperX preprocessing."""

    def prepare_asr_input(
        self,
        pipeline: Any,
        input_path: Path,
        acoustic_profile: str = "AUTO",
    ) -> tuple[Path, dict[str, Any]]:
        prepare = getattr(pipeline, "prepare_asr_input", None)
        if callable(prepare):
            return prepare(input_path, acoustic_profile)
        # Keep direct callers built against the old test seam working while
        # production receives the explicit runtime stage adapter.
        return pipeline.prepare_asr_input(input_path, acoustic_profile)

    def prepare_diarization_input(self, pipeline: Any, input_path: Path) -> Path:
        prepare = getattr(pipeline, "prepare_diarization_input", None)
        if callable(prepare):
            return prepare(input_path)
        return pipeline._preprocess_audio(input_path, asr=False)

    def prepare_profile(self, pipeline: Any, input_path: Path, profile: str) -> Path:
        prepare = getattr(pipeline, "prepare_profile", None)
        if callable(prepare):
            return prepare(input_path, profile)
        return pipeline._preprocess_audio_profile(input_path, profile)

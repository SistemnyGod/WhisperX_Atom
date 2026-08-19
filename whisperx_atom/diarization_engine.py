"""Diarization boundary for the Core Pipeline."""

from __future__ import annotations

from typing import Any, Protocol


class DiarizationEngine(Protocol):
    """Request-scoped contract for speaker segmentation."""

    def diarize(
        self,
        pipeline: Any,
        context: Any,
        result: dict[str, Any],
        profile: str = "diar",
    ) -> dict[str, Any]:
        ...


class WhisperXDiarizationEngine:
    """Compatibility adapter around the existing WhisperX/pyannote stage."""

    def diarize(
        self,
        pipeline: Any,
        context: Any,
        result: dict[str, Any],
        profile: str = "diar",
    ) -> dict[str, Any]:
        return pipeline._apply_diarization(context, result, profile) or result

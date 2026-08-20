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
        diarize = getattr(pipeline, "diarize", None)
        output = diarize(context, result, profile) if callable(diarize) else pipeline._apply_diarization(context, result, profile)
        output = output or result
        # The legacy adapter keeps pyannote's representative vectors on the
        # request context. Forward a JSON-safe copy for the enrichment
        # persistence boundary; no vector is ever added to Transcript V1.
        embeddings = getattr(context, "speaker_embeddings", None)
        if isinstance(embeddings, dict):
            safe_embeddings: dict[str, list[float]] = {}
            for label, vector in embeddings.items():
                try:
                    values = [float(item) for item in vector]
                except (TypeError, ValueError):
                    continue
                if values and all(value == value and abs(value) != float("inf") for value in values):
                    safe_embeddings[str(label)] = values
            if safe_embeddings:
                output["speaker_embeddings"] = safe_embeddings
        return output

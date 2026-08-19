"""Post-processing boundary for the canonical transcript pipeline.

The legacy WhisperX implementation still owns glossary and text cleanup.  The
adapter keeps that implementation intact while making the stage injectable for
tests and for a future post-processing implementation that does not import the
legacy pipeline module.
"""

from __future__ import annotations

from typing import Any, Protocol


class PostprocessingEngine(Protocol):
    """Application-level post-processing contract."""

    def postprocess(self, pipeline: Any, context: Any, result: dict[str, Any]) -> dict[str, Any]:
        """Return a transcript result with deterministic cleanup applied."""


class WhisperXPostprocessingEngine:
    """Compatibility adapter around the existing glossary implementation."""

    def postprocess(self, pipeline: Any, context: Any, result: dict[str, Any]) -> dict[str, Any]:
        processed = pipeline._apply_glossary(result)
        return processed if isinstance(processed, dict) else result

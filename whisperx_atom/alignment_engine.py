"""Alignment boundary for the Core Pipeline.

Alignment remains implemented by the proven WhisperX pipeline for now.  The
adapter makes the stage replaceable without moving model ownership into the
API or worker orchestration.
"""

from __future__ import annotations

from typing import Any, Protocol


class AlignmentEngine(Protocol):
    """Request-scoped contract for timestamp/word alignment."""

    def align(self, pipeline: Any, context: Any, result: dict[str, Any]) -> dict[str, Any]:
        ...


class WhisperXAlignmentEngine:
    """Compatibility adapter around the existing WhisperX aligner."""

    def align(self, pipeline: Any, context: Any, result: dict[str, Any]) -> dict[str, Any]:
        return pipeline._align_result(context, result) or result

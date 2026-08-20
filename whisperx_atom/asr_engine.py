"""ASR engine boundary used by the Core Pipeline.

The existing WhisperX pipeline owns model/cache details.  This adapter keeps
that implementation intact while making ASR an application dependency that can
be replaced by a fake in tests or another engine in a future release.
"""

from __future__ import annotations

from typing import Any, Protocol


class AsrEngine(Protocol):
    """Minimal request-scoped ASR contract; no storage or database access."""

    def transcribe(
        self,
        pipeline: Any,
        context: Any,
        *,
        vad_onset: float,
        chunk_size: int,
        beam_size: int,
    ) -> dict[str, Any]:
        ...


class WhisperXAsrEngine:
    """Compatibility adapter around the proven WhisperX implementation."""

    def transcribe(
        self,
        pipeline: Any,
        context: Any,
        *,
        vad_onset: float,
        chunk_size: int,
        beam_size: int,
    ) -> dict[str, Any]:
        transcribe = getattr(pipeline, "transcribe", None)
        if callable(transcribe):
            return transcribe(context, vad_onset=vad_onset, chunk_size=chunk_size, beam_size=beam_size) or {}
        return pipeline.run_asr_pass(context, vad_onset, chunk_size, beam_size) or {}

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from .stage_result import StageResults, ensure_stage_results


@dataclass(frozen=True)
class ProcessingRequest:
    job_id: str
    media_path: Path
    language: str | None = "ru"
    profile: str = "meeting"
    min_speakers: int = 1
    max_speakers: int = 12
    # Enrichment consumes the already persisted ASR V1 instead of running ASR
    # against the media a second time.
    input_transcript: dict[str, Any] | None = None
    # Storage identity of the canonical ASR asset. It is metadata only and is
    # never an absolute filesystem path or a secret.
    source_storage_key: str | None = None
    # SHA-256 of the canonical ASR asset persisted in V1. Enrichment must
    # reproduce and verify this value before reusing the transcript.
    source_audio_hash: str | None = None
    # Acoustic profile is metadata/selection only.  AUTO lets the worker use
    # measured signal metrics to select STANDARD or LARGE_ROOM.
    acoustic_profile: str = "AUTO"
    # TTS technical intervals are sample-derived server markers.  They are
    # applied only to the derived ASR input, never to the canonical recording.
    technical_intervals: tuple[tuple[int, int, str], ...] = ()


@dataclass
class ProcessingResult:
    job_id: str
    language: str | None
    text: str
    segments: list[dict[str, Any]]
    word_segments: list[dict[str, Any]]
    metadata: dict[str, Any]
    status: str = "READY"
    error_code: str | None = None
    warnings: list[str] = field(default_factory=list)
    stage_outcomes: StageResults | dict[str, str] = field(default_factory=StageResults)
    quality: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        self.stage_outcomes = ensure_stage_results(self.stage_outcomes)

    def to_dict(self) -> dict[str, Any]:
        return {
            "job_id": self.job_id,
            "language": self.language,
            "text": self.text,
            "segments": self.segments,
            "word_segments": self.word_segments,
            "metadata": self.metadata,
            "status": self.status,
            "error_code": self.error_code,
            "warnings": self.warnings,
            # Keep the established API/worker payload unchanged. Detailed
            # StageResult metadata remains an internal diagnostic boundary.
            "stage_outcomes": self.stage_outcomes.as_legacy_dict(),
            "quality": self.quality,
        }


ProgressCallback = Callable[[str, int], None]


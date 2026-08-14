from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable


@dataclass(frozen=True)
class ProcessingRequest:
    job_id: str
    media_path: Path
    language: str | None = "ru"
    profile: str = "meeting"
    min_speakers: int = 1
    max_speakers: int = 12


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
    stage_outcomes: dict[str, str] = field(default_factory=dict)
    quality: dict[str, Any] = field(default_factory=dict)

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
            "stage_outcomes": self.stage_outcomes,
            "quality": self.quality,
        }


ProgressCallback = Callable[[str, int], None]


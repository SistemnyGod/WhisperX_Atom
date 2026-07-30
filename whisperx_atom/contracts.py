from __future__ import annotations

from dataclasses import dataclass
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

    def to_dict(self) -> dict[str, Any]:
        return {
            "job_id": self.job_id,
            "language": self.language,
            "text": self.text,
            "segments": self.segments,
            "word_segments": self.word_segments,
            "metadata": self.metadata,
        }


ProgressCallback = Callable[[str, int], None]


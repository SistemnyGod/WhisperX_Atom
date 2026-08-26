"""Opaque identifiers used to keep Meeting as the centre of the pipeline.

IDs intentionally remain opaque: UUIDs are the normal value, but import and
offline clients may use another stable identifier.  We validate only what is
needed to prevent accidental path/query injection and keep the value
serializable for existing IPC/JSON contracts.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import ClassVar, TypeVar


_IdT = TypeVar("_IdT", bound="DomainId")


@dataclass(frozen=True)
class DomainId:
    value: str
    kind: ClassVar[str] = "id"

    @classmethod
    def parse(cls: type[_IdT], value: object) -> _IdT:
        text = str(value or "").strip()
        if not text or len(text) > 200:
            raise ValueError(f"{cls.kind.upper()}_REQUIRED")
        if any(char.isspace() or ord(char) < 32 for char in text):
            raise ValueError(f"{cls.kind.upper()}_INVALID")
        if "/" in text or "\\" in text:
            raise ValueError(f"{cls.kind.upper()}_INVALID")
        return cls(text)

    def __str__(self) -> str:
        return self.value


@dataclass(frozen=True)
class MeetingId(DomainId):
    kind: ClassVar[str] = "meeting_id"


@dataclass(frozen=True)
class RecordingId(DomainId):
    kind: ClassVar[str] = "recording_id"


@dataclass(frozen=True)
class TranscriptId(DomainId):
    kind: ClassVar[str] = "transcript_id"


@dataclass(frozen=True)
class JobId(DomainId):
    kind: ClassVar[str] = "job_id"

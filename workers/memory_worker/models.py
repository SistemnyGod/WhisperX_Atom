"""Small immutable contracts shared by the Meeting Memory worker."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class MemoryFact:
    fact_id: str | None
    owner_user_id: str | None
    meeting_id: str
    transcript_id: str
    transcript_version: int
    fact_type: str
    subject: str | None
    value: str
    start_ms: int
    end_ms: int
    confidence: float
    evidence_segment_ids: tuple[str, ...]
    derivation_type: str = "EXPLICIT"
    state: str = "ACTIVE"
    # Internal extraction context only. It is intentionally omitted from
    # ``to_dict`` and must not be written to memory acceptance artifacts.
    source_text: str = ""

    def to_dict(self) -> dict[str, Any]:
        return {
            "factId": self.fact_id,
            "ownerUserId": self.owner_user_id,
            "meetingId": self.meeting_id,
            "transcriptId": self.transcript_id,
            "transcriptVersion": self.transcript_version,
            "factType": self.fact_type,
            "subject": self.subject,
            "value": self.value,
            "startMs": self.start_ms,
            "endMs": self.end_ms,
            "confidence": self.confidence,
            "evidenceSegmentIds": list(self.evidence_segment_ids),
            "derivationType": self.derivation_type,
            "state": self.state,
        }


@dataclass(frozen=True)
class MemoryEntity:
    entity_type: str
    canonical_name: str
    normalized_name: str
    aliases: tuple[str, ...] = ()


@dataclass(frozen=True)
class MemoryRelation:
    source_fact_id: str
    target_fact_id: str
    relation_type: str
    confidence: float
    derivation_type: str = "DERIVED"


@dataclass(frozen=True)
class MemoryThread:
    title: str
    normalized_title: str
    state: str
    fact_ids: tuple[str, ...]
    first_seen_at: Any = None
    last_seen_at: Any = None

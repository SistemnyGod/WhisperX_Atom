"""Memory fact extraction backed by canonical transcript segments."""

from __future__ import annotations

import re
from typing import Any, Iterable, Mapping

from workers.summary_worker.fact_extraction import extract_transcript_facts

from .models import MemoryFact


def normalize_value(value: str) -> str:
    value = re.sub(r"\s+", " ", str(value or "").strip().lower().replace("ё", "е"))
    return value[:1000]


def extract_memory_facts(
    segments: Iterable[Mapping[str, Any] | tuple[Any, ...]],
    *,
    owner_user_id: str | None,
    meeting_id: str,
    transcript_id: str,
    transcript_version: int,
) -> list[MemoryFact]:
    """Extract explicit facts and retain every canonical evidence ID.

    The existing conservative extractor is the source of candidate facts. A
    memory row without an evidence segment is intentionally impossible here.
    """
    source = list(segments)
    source_texts = {
        str(item.get("id") or item.get("segmentId")): str(item.get("text") or "")
        for item in source
        if isinstance(item, Mapping)
    }
    derived = extract_transcript_facts(
        source,
        meeting_id=meeting_id,
        transcript_id=transcript_id,
        transcript_version=transcript_version,
    )
    return [
        MemoryFact(
            fact_id=None,
            owner_user_id=owner_user_id,
            meeting_id=fact.meeting_id,
            transcript_id=fact.transcript_id,
            transcript_version=fact.transcript_version,
            fact_type=fact.fact_type,
            subject=fact.subject,
            value=fact.value,
            start_ms=fact.start_ms,
            end_ms=fact.end_ms,
            confidence=fact.confidence,
            evidence_segment_ids=fact.evidence_segment_ids,
            derivation_type="EXPLICIT",
            state="ACTIVE",
            source_text=" ".join(source_texts.get(segment_id, "") for segment_id in fact.evidence_segment_ids).strip(),
        )
        for fact in derived
        if fact.evidence_segment_ids and fact.value.strip()
    ]

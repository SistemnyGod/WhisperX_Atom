"""Memory fact extraction backed by canonical transcript segments."""

from __future__ import annotations

import re
from typing import Any, Iterable, Mapping

from workers.summary_worker.fact_extraction import extract_transcript_facts

from .models import MemoryFact


FACT_MARKERS = (
    "решил", "решили", "решение", "поручил", "поручили", "задача",
    "ответствен", "срок", "до ", "причин", "статус", "риск",
    "вопрос", "назначен", "назначили", "выполнен", "завершен",
)


def normalize_value(value: str) -> str:
    value = re.sub(r"\s+", " ", str(value or "").strip().lower().replace("ё", "е"))
    return value[:1000]


def has_explicit_fact_candidates(segments: Iterable[Mapping[str, Any] | tuple[Any, ...]]) -> bool:
    """Return only a coarse, privacy-safe candidate signal.

    This does not create a fact. It distinguishes a legitimately empty
    meeting from a transcript that contains fact-shaped language but could
    not be projected and therefore requires review.
    """
    for item in segments:
        text = str(item.get("text") or "") if isinstance(item, Mapping) else str(item[-1] or "")
        normalized = re.sub(r"\s+", " ", text.strip().lower().replace("ё", "е"))
        if normalized and any(marker in normalized for marker in FACT_MARKERS):
            return True
    return False


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

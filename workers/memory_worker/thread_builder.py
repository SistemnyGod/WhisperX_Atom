"""Thread projection over normalized memory facts."""

from __future__ import annotations

from collections import defaultdict
from typing import Iterable

from .models import MemoryFact, MemoryThread
from .entity_resolver import normalize_entity_name


def build_threads(facts: Iterable[MemoryFact]) -> tuple[MemoryThread, ...]:
    grouped: dict[tuple[str | None, str], list[MemoryFact]] = defaultdict(list)
    for fact in facts:
        if fact.state != "ACTIVE" or not fact.evidence_segment_ids:
            continue
        subject = normalize_entity_name(fact.subject or fact.value[:120])
        grouped[(fact.owner_user_id, subject)].append(fact)
    result: list[MemoryThread] = []
    for (_owner, normalized), values in grouped.items():
        ordered = sorted(values, key=lambda fact: (fact.meeting_id, fact.start_ms, fact.fact_id or ""))
        roles = {fact.fact_type for fact in ordered}
        state = "RESOLVED" if "STATUS" in roles and any("готов" in fact.value.lower() or "заверш" in fact.value.lower() for fact in ordered) else "OPEN"
        result.append(MemoryThread(
            title=ordered[0].subject or ordered[0].value[:120],
            normalized_title=normalized,
            state=state,
            fact_ids=tuple(fact.fact_id for fact in ordered if fact.fact_id),
            # The database adapter supplies meeting.started_at when it
            # persists the projection. IDs are deliberately not presented as
            # dates or chronology.
            first_seen_at=None,
            last_seen_at=None,
        ))
    return tuple(result)

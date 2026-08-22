"""Thread projection over normalized memory facts."""

from __future__ import annotations

from collections import defaultdict
from typing import Iterable

from .models import MemoryFact, MemoryThread
from .entity_resolver import canonical_topic_name


def build_threads(facts: Iterable[MemoryFact], relations: Iterable[object] = ()) -> tuple[MemoryThread, ...]:
    grouped: dict[tuple[str | None, str], list[MemoryFact]] = defaultdict(list)
    for fact in facts:
        if fact.state != "ACTIVE" or not fact.evidence_segment_ids or not fact.subject:
            continue
        subject = canonical_topic_name(fact.subject)
        grouped[(fact.owner_user_id, subject)].append(fact)
    relation_values = tuple(relations)
    result: list[MemoryThread] = []
    for (_owner, normalized), values in grouped.items():
        ordered = sorted(
            values,
            key=lambda fact: (
                fact.meeting_started_at is None,
                fact.meeting_started_at,
                fact.start_ms,
                fact.fact_id or "",
            ),
        )
        ids = {fact.fact_id for fact in ordered}
        has_conflict = any(
            getattr(relation, "relation_type", None) == "CONTRADICTS"
            and getattr(relation, "source_fact_id", None) in ids
            and getattr(relation, "target_fact_id", None) in ids
            for relation in relation_values
        )
        has_close = any(
            getattr(relation, "relation_type", None) == "CLOSES"
            and getattr(relation, "source_fact_id", None) in ids
            and getattr(relation, "target_fact_id", None) in ids
            for relation in relation_values
        )
        latest = ordered[-1]
        latest_text = (getattr(latest, "source_text", "") or latest.value).lower()
        state = "CONFLICTED" if has_conflict else "RESOLVED" if has_close or (
            latest.fact_type in {"TASK", "STATUS"}
            and any(marker in latest_text for marker in ("готов", "сделан", "выполн", "заверш", "закрыт"))
        ) else "OPEN"
        result.append(MemoryThread(
            title=ordered[0].subject or ordered[0].value[:120],
            normalized_title=normalized,
            state=state,
            fact_ids=tuple(fact.fact_id for fact in ordered if fact.fact_id),
            first_seen_at=ordered[0].meeting_started_at,
            last_seen_at=ordered[-1].meeting_started_at,
        ))
    return tuple(result)

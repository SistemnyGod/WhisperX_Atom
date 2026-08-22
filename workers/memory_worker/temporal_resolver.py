"""Temporal ordering and current-state selection over evidence-backed facts."""

from __future__ import annotations

from collections import defaultdict
from typing import Iterable, Mapping

from .entity_resolver import canonical_topic_name
from .models import MemoryFact, MemoryRelation


def timeline(
    facts: Iterable[MemoryFact],
    meeting_started_at: Mapping[str, object] | None = None,
) -> tuple[MemoryFact, ...]:
    dates = meeting_started_at or {}
    return tuple(sorted(
        (fact for fact in facts if fact.state == "ACTIVE" and fact.evidence_segment_ids),
        key=lambda fact: (dates.get(fact.meeting_id, fact.meeting_id), fact.start_ms, fact.end_ms, fact.fact_id or ""),
    ))


def current_state(
    facts: Iterable[MemoryFact],
    relations: Iterable[MemoryRelation] = (),
    meeting_started_at: Mapping[str, object] | None = None,
) -> tuple[MemoryFact, ...]:
    """Select the latest fact unless it is invalidated/superseded.

    Returned facts still carry canonical segment IDs. Derived relations only
    influence selection and never replace source evidence.
    """
    values = [fact for fact in facts if fact.state == "ACTIVE" and fact.evidence_segment_ids]
    dates = meeting_started_at or {}

    def chronology(fact: MemoryFact) -> tuple[bool, object, int, int, str]:
        # Runtime callers pass meeting.started_at; the final ID tie-breaker is
        # only for truly identical timestamps and never establishes meeting
        # chronology on its own.
        value = dates.get(fact.meeting_id, fact.meeting_started_at)
        return (value is None, value, fact.start_ms, fact.end_ms, fact.fact_id or "")

    superseded = {relation.source_fact_id for relation in relations if relation.relation_type == "SUPERSEDES"}
    groups: dict[tuple[str, str], list[MemoryFact]] = defaultdict(list)
    for fact in values:
        subject_key = canonical_topic_name(fact.subject) if fact.subject else ""
        if not subject_key:
            subject_key = "value:" + " ".join(fact.value.lower().replace("ё", "е").split())
        groups[(fact.fact_type, subject_key)].append(fact)
    selected: list[MemoryFact] = []
    for group in groups.values():
        eligible = [fact for fact in group if fact.fact_id not in superseded]
        values_by_text = {" ".join(fact.value.lower().replace("ё", "е").split()) for fact in (eligible or group)}
        # Without an explicit SUPERSEDES relation, retain conflicting values
        # for the answer planner instead of silently treating the newest row
        # as the current truth.
        if len(values_by_text) > 1 and len(eligible or group) > 1:
            selected.extend(sorted(eligible or group, key=chronology))
        else:
            selected.append(max(eligible or group, key=chronology))
    return tuple(sorted(selected, key=lambda fact: (fact.fact_type, canonical_topic_name(fact.subject or ""), chronology(fact))))

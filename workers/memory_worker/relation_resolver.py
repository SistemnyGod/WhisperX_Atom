"""Explicit/derived cross-meeting relation detection."""

from __future__ import annotations

import re
from collections import defaultdict
from typing import Iterable, Mapping

from .models import MemoryFact, MemoryRelation
from .entity_resolver import canonical_topic_name


_SUPERSEDES = re.compile(r"\b(?:теперь|новый|новая|перенесли|изменили|передали|заменили)\b", re.IGNORECASE)
_CLOSES = re.compile(r"\b(?:готово|сделано|сделана|выполнено|выполнена|завершено|завершена|закрыли|закрыта|подготовил)\b", re.IGNORECASE)


def resolve_relations(
    facts: Iterable[MemoryFact],
    meeting_order: Mapping[str, int] | None = None,
) -> tuple[MemoryRelation, ...]:
    """Link only compatible facts and mark the link as derived.

    A relation is a retrieval hint. It is never sufficient by itself for an
    answer; callers must rehydrate both facts' evidence segments.
    """
    values = [fact for fact in facts if fact.state == "ACTIVE" and fact.fact_id and fact.subject]
    if meeting_order is not None:
        order = dict(meeting_order)
    else:
        # The DB adapter normally supplies meeting_order from meetings.started_at.
        # This fallback is deterministic for pure tests but is never exposed as
        # user-facing chronology.
        dated = sorted({
            (fact.meeting_started_at, fact.meeting_id)
            for fact in values
            if fact.meeting_started_at is not None
        }, key=lambda item: (item[0], item[1]))
        if dated:
            order = {meeting_id: index for index, (_started, meeting_id) in enumerate(dated)}
        else:
            order = {meeting_id: index for index, meeting_id in enumerate(sorted({fact.meeting_id for fact in values}))}
    grouped: dict[tuple[str, str], list[MemoryFact]] = defaultdict(list)
    for fact in values:
        grouped[(str(fact.owner_user_id or ""), canonical_topic_name(fact.subject or ""))].append(fact)

    def chronology(fact: MemoryFact) -> tuple[int, int, int, str]:
        return (order.get(fact.meeting_id, 0), fact.start_ms, fact.end_ms, fact.fact_id or "")

    result: list[MemoryRelation] = []
    for (_owner, _subject), topic_facts in grouped.items():
        ordered = sorted(topic_facts, key=chronology)
        for position, newer in enumerate(ordered):
            relation_text = getattr(newer, "source_text", "") or newer.value
            previous_same_type = next(
                (candidate for candidate in reversed(ordered[:position]) if candidate.fact_type == newer.fact_type),
                None,
            )
            if newer.fact_type in {"DEADLINE", "RESPONSIBLE"} and previous_same_type is not None:
                relation = "SUPERSEDES" if _SUPERSEDES.search(relation_text) else "CONTRADICTS"
                result.append(MemoryRelation(previous_same_type.fact_id, newer.fact_id, relation, 0.70, "DERIVED"))
                continue

            # A completion/status fact closes the most recent open task for
            # the same normalized topic, even when the extractor assigned a
            # different fact type to the closing utterance.
            if _CLOSES.search(relation_text) and newer.fact_type in {"TASK", "STATUS"}:
                previous_task = next(
                    (candidate for candidate in reversed(ordered[:position]) if candidate.fact_type in {"TASK", "STATUS"}),
                    None,
                )
                if previous_task is not None:
                    result.append(MemoryRelation(previous_task.fact_id, newer.fact_id, "CLOSES", 0.70, "DERIVED"))
                    continue

            if previous_same_type is not None:
                result.append(MemoryRelation(previous_same_type.fact_id, newer.fact_id, "CONTINUES", 0.70, "DERIVED"))
    unique = {(item.source_fact_id, item.target_fact_id, item.relation_type): item for item in result}
    return tuple(unique.values())

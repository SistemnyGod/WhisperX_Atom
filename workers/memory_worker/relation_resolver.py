"""Explicit/derived cross-meeting relation detection."""

from __future__ import annotations

import re
from typing import Iterable, Mapping

from .models import MemoryFact, MemoryRelation


_SUPERSEDES = re.compile(r"\b(?:теперь|новый|новая|перенесли|изменили|передали|заменили)\b", re.IGNORECASE)
_CLOSES = re.compile(r"\b(?:готово|сделано|выполнено|завершено|закрыли|подготовил)\b", re.IGNORECASE)


def resolve_relations(
    facts: Iterable[MemoryFact],
    meeting_order: Mapping[str, int] | None = None,
) -> tuple[MemoryRelation, ...]:
    """Link only compatible facts and mark the link as derived.

    A relation is a retrieval hint. It is never sufficient by itself for an
    answer; callers must rehydrate both facts' evidence segments.
    """
    values = [fact for fact in facts if fact.state == "ACTIVE" and fact.fact_id]
    order = dict(meeting_order or {meeting_id: index for index, meeting_id in enumerate(sorted({fact.meeting_id for fact in values}))})
    result: list[MemoryRelation] = []
    for newer in values:
        for older in values:
            if newer.fact_id == older.fact_id or newer.fact_type != older.fact_type:
                continue
            if newer.meeting_id == older.meeting_id:
                if newer.start_ms <= older.start_ms:
                    continue
            elif order.get(newer.meeting_id, 0) <= order.get(older.meeting_id, 0):
                continue
            same_subject = bool(newer.subject and older.subject and newer.subject.lower() == older.subject.lower())
            # Never link facts from unrelated topics merely because their
            # types match. Subject/entity linking must be explicit first;
            # otherwise a deadline from one meeting could supersede a
            # deadline for a completely different project.
            if not same_subject:
                continue
            relation_text = getattr(newer, "source_text", "") or newer.value
            if newer.fact_type in {"DEADLINE", "RESPONSIBLE"}:
                relation = "SUPERSEDES" if _SUPERSEDES.search(relation_text) else "CONTRADICTS"
            elif newer.fact_type == "TASK" and _CLOSES.search(relation_text):
                relation = "CLOSES"
            else:
                relation = "CONTINUES"
            result.append(MemoryRelation(older.fact_id, newer.fact_id, relation, 0.70, "DERIVED"))
    unique = {(item.source_fact_id, item.target_fact_id, item.relation_type): item for item in result}
    return tuple(unique.values())

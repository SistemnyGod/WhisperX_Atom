"""Thread projection over normalized memory facts."""

from __future__ import annotations

from collections import defaultdict
import re
from typing import Iterable

from .models import MemoryFact, MemoryThread
from .entity_resolver import canonical_topic_name
from .relation_resolver import is_explicit_closure, normalize_fact_value


_REOPENED_MARKER = re.compile(
    r"\b(?:снова|опять|повторно|возобнов\w*|заново)\b|\b(?:проблема|задача)\s+(?:снова|опять)\b",
    re.IGNORECASE,
)


def is_explicit_reopen(text: str) -> bool:
    """Recognize affirmative reopening language, not a historical mention."""
    return bool(_REOPENED_MARKER.search(text or ""))


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
        # Relations are emitted in canonical meeting chronology by the
        # resolver.  Use their directed chain when lightweight/unit callers
        # do not attach meeting_started_at to facts; UUID order is never a
        # chronology source.
        index_by_id = {fact.fact_id: index for index, fact in enumerate(ordered)}
        adjacency: dict[str, set[str]] = defaultdict(set)
        indegree = {fact.fact_id: 0 for fact in ordered if fact.fact_id}
        for relation in relation_values:
            source = getattr(relation, "source_fact_id", None)
            target = getattr(relation, "target_fact_id", None)
            if source in indegree and target in indegree and target not in adjacency[source]:
                adjacency[source].add(target)
                indegree[target] += 1
        ready = sorted((fact_id for fact_id, degree in indegree.items() if degree == 0), key=index_by_id.get)
        topological: list[str] = []
        while ready:
            current_id = ready.pop(0)
            topological.append(current_id)
            for target in sorted(adjacency.get(current_id, ()), key=index_by_id.get):
                indegree[target] -= 1
                if indegree[target] == 0:
                    ready.append(target)
                    ready.sort(key=index_by_id.get)
        if len(topological) == len(indegree):
            by_id = {fact.fact_id: fact for fact in ordered}
            ordered = [by_id[fact_id] for fact_id in topological]
        ids = {fact.fact_id for fact in ordered}
        superseded = {
            getattr(relation, "source_fact_id", None)
            for relation in relation_values
            if getattr(relation, "relation_type", None) in {"SUPERSEDES", "CONFIRMS"}
        }
        # A later explicit change supersedes every earlier candidate of the
        # same fact type, including an unresolved branch created by an older
        # CONTRADICTS edge.  The relation graph stays sequential; this set is
        # only the current-state projection.
        position_by_id = {fact.fact_id: index for index, fact in enumerate(ordered)}
        type_by_id = {fact.fact_id: fact.fact_type for fact in ordered}
        for relation in relation_values:
            if getattr(relation, "relation_type", None) != "SUPERSEDES":
                continue
            target_position = position_by_id.get(getattr(relation, "target_fact_id", None))
            target_type = type_by_id.get(getattr(relation, "target_fact_id", None))
            if target_position is None:
                continue
            superseded.update(
                fact.fact_id for fact in ordered[:target_position]
                if fact.fact_type == target_type and fact.fact_id
            )
        current = [fact for fact in ordered if fact.fact_id not in superseded]
        current_values = {
            normalize_fact_value(fact.fact_type, fact.value)
            for fact in current
            if fact.fact_type in {"DEADLINE", "RESPONSIBLE"}
        }
        # A historical CONTRADICTS edge is not itself a permanent conflict:
        # an explicit later SUPERSEDES edge removes the older candidate.
        has_conflict = len(current_values) > 1 or any(
            getattr(relation, "relation_type", None) == "CONTRADICTS"
            and getattr(relation, "source_fact_id", None) in {fact.fact_id for fact in current}
            and getattr(relation, "target_fact_id", None) in {fact.fact_id for fact in current}
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
        reopened = has_close and is_explicit_reopen(latest_text)
        state = "CONFLICTED" if has_conflict else "REOPENED" if reopened else "RESOLVED" if has_close or (
            latest.fact_type in {"TASK", "STATUS"}
            and is_explicit_closure(latest_text)
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

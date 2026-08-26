from __future__ import annotations

import re
from typing import Any, Mapping


CONFLICT_REASON = "CONFLICTING_STATEMENTS"

_DECISION_CUES = {
    "решить", "решили", "решение", "утвердить", "утвердили", "утверждаем",
    "согласовать", "согласовали", "планируем", "договорились", "предлагаю",
}
_OVERRIDE_CUES = {
    "отмен", "перенес", "перенос", "измен", "замен", "вместо", "отказ",
    "новый", "новое", "теперь", "переигр", "пересмотр",
}
_NEGATION_CUES = {"не", "нет", "нельзя", "отмен", "отказ", "исключить"}
_STOPWORDS = {
    "это", "как", "что", "для", "или", "при", "если", "надо", "будет", "был", "быть",
    "также", "после", "перед", "когда", "есть", "они", "она", "его", "ее", "их", "мы",
    "нужно", "можно", "надо", "будем", "сегодня", "завтра", "понедельник", "вторник",
    "среда", "четверг", "пятница", "суббота", "воскресенье",
}


def reconcile_decisions(
    facts: list[dict[str, Any]],
    segment_times: Mapping[str, tuple[int, int]] | None = None,
) -> list[dict[str, Any]]:
    """Mark contradictory decision candidates without silently selecting one.

    The reducer still receives every supported fact. A later statement may be
    tagged as the current candidate, but both statements remain reviewable.
    """

    decisions = [fact for fact in facts if fact.get("type") == "decision"]
    if len(decisions) < 2:
        return facts

    groups: list[list[dict[str, Any]]] = []
    for fact in decisions:
        subject = _decision_subject(fact)
        if not subject:
            continue
        matching = next((group for group in groups if _subjects_overlap(subject, group[0].get("_decision_subject", set()))), None)
        if matching is None:
            fact["_decision_subject"] = subject
            groups.append([fact])
        else:
            fact["_decision_subject"] = subject
            matching.append(fact)

    group_number = 0
    for group in groups:
        if len(group) < 2 or not _group_conflicts(group):
            continue
        group_number += 1
        ordered = sorted(group, key=lambda item: _fact_start(item, segment_times or {}))
        group_id = f"decision-conflict-{group_number}"
        for index, fact in enumerate(ordered):
            fact["conflict_group"] = group_id
            fact["needs_review"] = True
            reasons = list(fact.get("review_reasons", []))
            if CONFLICT_REASON not in reasons:
                reasons.append(CONFLICT_REASON)
            fact["review_reasons"] = reasons
            fact["resolution_status"] = "CURRENT_CANDIDATE" if index == len(ordered) - 1 else "SUPERSEDED_CANDIDATE"

    for fact in decisions:
        # Internal grouping metadata must not leak into the final Qwen contract.
        fact.pop("_decision_subject", None)
    return facts


def decision_conflict_facts(facts: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Return compact conflict metadata for persistence and quality reporting."""

    return [
        {
            "type": fact.get("type"),
            "text": fact.get("text"),
            "conflict_group": fact.get("conflict_group"),
            "resolution_status": fact.get("resolution_status"),
            "evidence_segment_ids": list(fact.get("evidence_segment_ids", [])),
            "review_reasons": list(fact.get("review_reasons", [])),
        }
        for fact in facts
        if fact.get("conflict_group")
    ]


def _group_conflicts(group: list[dict[str, Any]]) -> bool:
    signatures = {_decision_signature(fact) for fact in group}
    if len(signatures) < 2:
        return False
    return any(_has_override_or_negation(fact) for fact in group) or len(group) > 1


def _decision_subject(fact: Mapping[str, Any]) -> set[str]:
    explicit = _stems(str(fact.get("subject", "")))
    if explicit:
        return explicit
    text = str(fact.get("text", ""))
    terms = _stems(text)
    return {term for term in terms if term not in _DECISION_CUES and term not in _OVERRIDE_CUES}


def _decision_signature(fact: Mapping[str, Any]) -> tuple[str, ...]:
    terms = _stems(str(fact.get("text", "")))
    return tuple(sorted(term for term in terms if term not in _STOPWORDS))


def _has_override_or_negation(fact: Mapping[str, Any]) -> bool:
    text = str(fact.get("text", "")).casefold()
    return any(cue in text for cue in _OVERRIDE_CUES | _NEGATION_CUES)


def _subjects_overlap(left: set[str], right: set[str]) -> bool:
    if not left or not right:
        return False
    for left_term in left:
        for right_term in right:
            if left_term == right_term:
                return True
            if len(left_term) >= 4 and len(right_term) >= 4 and (left_term.startswith(right_term) or right_term.startswith(left_term)):
                return True
    return False


def _fact_start(fact: Mapping[str, Any], segment_times: Mapping[str, tuple[int, int]]) -> int:
    starts = [segment_times[str(value).removeprefix("SEG-")][0] for value in fact.get("evidence_segment_ids", []) if str(value).removeprefix("SEG-") in segment_times]
    return min(starts) if starts else 2**63 - 1


def _stems(value: str) -> set[str]:
    result: set[str] = set()
    for token in re.findall(r"[a-zа-яё0-9]{3,}", value.casefold()):
        if token in _STOPWORDS:
            continue
        stem = re.sub(r"(ами|ями|ого|ему|ому|ыми|ими|ать|ить|еть|ую|юю|ая|яя|ое|ее|ы|и|а|я|у|ю|е|о|й|ь)$", "", token)
        if len(stem) >= 3:
            result.add(stem)
    return result

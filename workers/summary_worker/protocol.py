from __future__ import annotations

import re
from typing import Any, Mapping

from .resolvers import resolve_deadline


_STOPWORDS = {
    "это", "как", "что", "для", "или", "при", "если", "надо", "будет", "был", "быть",
    "также", "после", "перед", "когда", "есть", "они", "она", "его", "ее", "их", "мы",
    "нужно", "можно", "надо", "будем", "сегодня", "завтра", "вопрос", "тема",
}

_STRUCTURAL_ARTIFACTS = ("{'", '"SEG-ID"', "'SEG-ID'", "evidence_segment_ids", "__class__")


def _contains_structural_artifact(value: Any) -> bool:
    return isinstance(value, str) and any(token in value for token in _STRUCTURAL_ARTIFACTS)


def validate_protocol_candidates(
    candidates: list[dict[str, Any]],
    valid_ids: set[str],
    segment_texts: Mapping[str, str],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Keep only evidence-backed protocol candidates before the reducer."""

    supported: list[dict[str, Any]] = []
    rejected: list[dict[str, Any]] = []
    for source in candidates:
        candidate = dict(source)
        evidence = _unique_ids(candidate.get("evidence_segment_ids", []), valid_ids)
        candidate["evidence_segment_ids"] = evidence
        reasons: list[str] = []
        if not evidence:
            reasons.append("NO_EVIDENCE")
        evidence_text = " ".join(segment_texts.get(item, "") for item in evidence)
        claim = " ".join(
            str(candidate.get(field) or "")
            for field in ("text", "topic_hint", "context", "decision")
        )
        if evidence and not _claim_supported(claim, evidence_text):
            reasons.append("UNSUPPORTED")
        deadline_text = _nullable_text(candidate.get("deadline_text"))
        if deadline_text and evidence and not _claim_supported(deadline_text, evidence_text):
            candidate["deadline_text"] = None
            candidate["deadline_iso"] = None
            reasons.append("UNSUPPORTED_DEADLINE")
        if reasons:
            # An unsupported deadline alone must not discard an otherwise
            # supported task; retain it with the deadline removed.
            if reasons == ["UNSUPPORTED_DEADLINE"]:
                candidate["needs_review"] = True
                candidate["review_reasons"] = reasons
                candidate["evidence_text"] = evidence_text[:3600]
                supported.append(candidate)
                continue
            candidate["needs_review"] = True
            candidate["review_reasons"] = _unique_strings(reasons)
            rejected.append(candidate)
            continue
        candidate["needs_review"] = False
        candidate["review_reasons"] = []
        candidate["evidence_text"] = evidence_text[:3600]
        supported.append(candidate)
    return supported, rejected


def group_protocol_candidates(candidates: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Conservatively merge repeated discussion/decision candidates."""

    groups: list[dict[str, Any]] = []
    for candidate in candidates:
        if candidate.get("type") not in {"discussion", "decision", "fact"}:
            continue
        decision = _nullable_text(candidate.get("decision"))
        if not decision:
            continue
        topic = _nullable_text(candidate.get("topic_hint")) or _topic_from_candidate(candidate)
        topic_tokens = _terms(topic)
        group = next(
            (
                item
                for item in groups
                if _topics_match(topic_tokens, item["topic_tokens"])
                and _conflicts_compatible(item.get("conflict_group"), candidate.get("conflict_group"))
            ),
            None,
        )
        if group is None:
            group = {
                "candidate_ids": [f"C{len(groups) + 1}"],
                "topic_hint": topic[:180],
                "topic_tokens": topic_tokens,
                "contexts": [],
                "decisions": [],
                "evidence_segment_ids": [],
                "review_reasons": [],
                "conflict_group": candidate.get("conflict_group"),
            }
            groups.append(group)
        group["candidate_ids"].append(f"C{len(groups) + 1}-{len(group['candidate_ids']) + 1}")
        for field, collection in (("context", "contexts"), ("text", "contexts"), ("decision", "decisions")):
            value = _nullable_text(candidate.get(field))
            if value and value not in group[collection]:
                group[collection].append(value)
        for segment_id in candidate.get("evidence_segment_ids", []):
            if segment_id not in group["evidence_segment_ids"]:
                group["evidence_segment_ids"].append(segment_id)
        for reason in candidate.get("review_reasons", []):
            if reason not in group["review_reasons"]:
                group["review_reasons"].append(reason)

    for group in groups:
        group.pop("topic_tokens", None)
        group["context"] = " ".join(group.pop("contexts", []))[:1200]
        group["decision"] = " ".join(group.pop("decisions", []))[:1200]
        group["evidence_segment_ids"] = group["evidence_segment_ids"][:12]
    return groups


def group_protocol_tasks(candidates: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Deduplicate tasks while retaining all supporting evidence."""

    groups: list[dict[str, Any]] = []
    for candidate in candidates:
        if candidate.get("type") != "task":
            continue
        task = _nullable_text(candidate.get("text"))
        if not task:
            continue
        terms = _terms(task)
        current = next((item for item in groups if _tasks_match(terms, item["task_terms"])), None)
        if current is None:
            current = {
                "task": task[:1000],
                "deadline_text": _nullable_text(candidate.get("deadline_text")),
                "evidence_segment_ids": [],
                "review_reasons": list(candidate.get("review_reasons", [])),
                "task_terms": terms,
            }
            groups.append(current)
        for segment_id in candidate.get("evidence_segment_ids", []):
            if segment_id not in current["evidence_segment_ids"]:
                current["evidence_segment_ids"].append(segment_id)
        if not current.get("deadline_text") and candidate.get("deadline_text"):
            current["deadline_text"] = candidate["deadline_text"]
        for reason in candidate.get("review_reasons", []):
            if reason not in current["review_reasons"]:
                current["review_reasons"].append(reason)
    for group in groups:
        group.pop("task_terms", None)
    return groups


def limit_protocol_items(items: list[dict[str, Any]], limit: int = 40) -> tuple[list[dict[str, Any]], bool]:
    """Apply the safety limit without making truncation depend on input order."""

    if len(items) <= limit:
        return items, False
    ranked = sorted(
        enumerate(items),
        key=lambda pair: (
            bool(pair[1].get("review_reasons")),
            -len(pair[1].get("evidence_segment_ids", [])),
            pair[0],
        ),
    )
    selected_indexes = sorted(index for index, _ in ranked[:limit])
    return [items[index] for index in selected_indexes], True


def protocol_candidates_prompt(groups: list[dict[str, Any]], tasks: list[dict[str, Any]]) -> str:
    """Serialize validated candidates for the final reducer, not the transcript."""

    lines = ["VALIDATED QUESTION/DECISION CANDIDATES:"]
    for index, group in enumerate(groups, start=1):
        lines.append(
            f"GROUP {index}:\n"
            f"topic_hint={group.get('topic_hint')}\n"
            f"context={group.get('context')}\n"
            f"decision={group.get('decision')}\n"
            f"conflict_group={group.get('conflict_group')}\n"
            f"review_reasons={','.join(group.get('review_reasons', []))}\n"
            f"evidence={','.join(group.get('evidence_segment_ids', []))}"
        )
    lines.append("\nVALIDATED TASK CANDIDATES:")
    for index, task in enumerate(tasks, start=1):
        lines.append(
            f"TASK {index}:\n"
            f"task={task.get('task')}\n"
            f"deadline_text={task.get('deadline_text')}\n"
            f"evidence={','.join(task.get('evidence_segment_ids', []))}"
        )
    return "\n\n".join(lines)


def normalize_protocol_result(payload: Mapping[str, Any]) -> dict[str, Any]:
    """Keep only the public protocol contract; unknown/legacy fields are dropped."""

    questions: list[dict[str, Any]] = []
    raw_questions = payload.get("questions_and_decisions", [])
    if isinstance(raw_questions, list):
        for item in raw_questions[:40]:
            if not isinstance(item, Mapping):
                continue
            topic = _nullable_text(item.get("topic"))
            context = _nullable_text(item.get("context"))
            decision = _nullable_text(item.get("decision"))
            if not topic or not decision:
                continue
            questions.append({
                "topic": topic[:180],
                "context": (context or "")[:1200],
                "decision": decision[:1200],
                "evidence_segment_ids": _raw_evidence(item.get("evidence_segment_ids"), 12),
            })

    tasks: list[dict[str, Any]] = []
    raw_tasks = payload.get("tasks", [])
    if isinstance(raw_tasks, list):
        for item in raw_tasks[:40]:
            if not isinstance(item, Mapping):
                continue
            task = _nullable_text(item.get("task"))
            if not task:
                continue
            tasks.append({
                "task": task[:1000],
                "deadline_text": _nullable_text(item.get("deadline_text", item.get("deadline"))),
                # The ISO value is always recomputed from deadline_text later.
                "deadline_iso": None,
                "evidence_segment_ids": _raw_evidence(item.get("evidence_segment_ids"), 12),
            })
    return {"questions_and_decisions": questions, "tasks": tasks}


def validate_protocol_result(
    payload: Mapping[str, Any],
    valid_ids: set[str],
    segment_texts: Mapping[str, str],
    segment_times: Mapping[str, tuple[int, int]],
    meeting_date: str | None,
    timezone: str | None = None,
) -> dict[str, Any]:
    """Validate final reducer output and compute source ranges in code."""

    normalized = normalize_protocol_result(payload)
    questions: list[dict[str, Any]] = []
    tasks: list[dict[str, Any]] = []
    rejected_items = 0
    review_items = 0

    for item in normalized["questions_and_decisions"]:
        if any(_contains_structural_artifact(item.get(field)) for field in ("topic", "context", "decision")):
            rejected_items += 1
            continue
        evidence = _unique_ids(item["evidence_segment_ids"], valid_ids)
        item["evidence_segment_ids"] = evidence
        reasons = [] if evidence and _claim_supported(
            f"{item['topic']} {item['context']} {item['decision']}",
            " ".join(segment_texts.get(value, "") for value in evidence),
        ) else ["NO_EVIDENCE" if not evidence else "UNSUPPORTED"]
        if reasons:
            rejected_items += 1
            continue
        item["source_start_ms"], item["source_end_ms"] = _source_range(evidence, segment_times)
        item["validation"] = {
            "evidence": True,
            "confidence": _support_confidence(
                f"{item['topic']} {item['context']} {item['decision']}",
                " ".join(segment_texts.get(value, "") for value in evidence),
            ),
            "needs_review": False,
            "review_reasons": [],
        }
        questions.append(item)

    for item in normalized["tasks"]:
        if any(_contains_structural_artifact(item.get(field)) for field in ("task", "deadline_text")):
            rejected_items += 1
            continue
        evidence = _unique_ids(item["evidence_segment_ids"], valid_ids)
        item["evidence_segment_ids"] = evidence
        evidence_text = " ".join(segment_texts.get(value, "") for value in evidence)
        reasons = [] if evidence and _claim_supported(item["task"], evidence_text) else ["NO_EVIDENCE" if not evidence else "UNSUPPORTED"]
        deadline_text = _nullable_text(item.get("deadline_text"))
        if deadline_text and evidence and not _claim_supported(deadline_text, evidence_text):
            item["deadline_text"] = None
            item["deadline_iso"] = None
            reasons.append("UNSUPPORTED_DEADLINE")
        elif deadline_text:
            resolved = resolve_deadline(deadline_text, meeting_date, timezone)
            item["deadline_iso"] = resolved.get("deadline_iso")
        if reasons and reasons != ["UNSUPPORTED_DEADLINE"]:
            rejected_items += 1
            continue
        if reasons:
            review_items += 1
        item["source_start_ms"], item["source_end_ms"] = _source_range(evidence, segment_times)
        item["validation"] = {
            "evidence": True,
            "deadline": bool(item.get("deadline_text")),
            "confidence": _support_confidence(item["task"], evidence_text),
            "needs_review": bool(reasons),
            "review_reasons": _unique_strings(reasons),
        }
        tasks.append(item)

    questions = _deduplicate_rows(questions, ("topic", "decision"), 40)
    tasks = _deduplicate_rows(tasks, ("task",), 40)
    total = len(questions) + len(tasks) + rejected_items
    score = 1.0 if total == 0 else max(0.0, min(1.0, 1.0 - (rejected_items + review_items) / total))
    reasons = []
    if rejected_items:
        reasons.append("UNSUPPORTED_ITEMS_REJECTED")
    if review_items:
        reasons.append("ITEMS_NEED_REVIEW")
    if not questions and not tasks:
        reasons.append("NO_CONFIRMED_DECISIONS")
    if rejected_items and not questions and not tasks:
        status = "FAILED"
    else:
        status = "NEEDS_REVIEW" if rejected_items or review_items else "READY"
    return {
        "questions_and_decisions": questions,
        "tasks": tasks,
        "quality": {
            "status": status,
            "score": round(score, 3),
            "question_count": len(questions),
            "task_count": len(tasks),
            "supported_count": len(questions) + len(tasks),
            "partial_count": review_items,
            "rejected_count": rejected_items,
            "review_items": review_items,
            "rejected_items": rejected_items,
            "duplicate_topics_removed": 0,
            "duplicate_tasks_removed": 0,
            "unsupported_deadlines_removed": sum(
                1 for item in tasks if "UNSUPPORTED_DEADLINE" in item.get("validation", {}).get("review_reasons", [])
            ),
            "reasons": reasons,
        },
    }


def _deduplicate_rows(rows: list[dict[str, Any]], fields: tuple[str, ...], limit: int) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    by_key: dict[str, dict[str, Any]] = {}
    for row in rows:
        key = "|".join(_normalise(row.get(field)) for field in fields)
        if not key:
            continue
        existing = by_key.get(key)
        if existing is None:
            by_key[key] = row
            result.append(row)
            continue
        evidence = existing.setdefault("evidence_segment_ids", [])
        for segment_id in row.get("evidence_segment_ids", []):
            if segment_id not in evidence:
                evidence.append(segment_id)
        existing["source_start_ms"], existing["source_end_ms"] = _merge_ranges(existing, row)
    return result[:limit]


def _merge_ranges(left: Mapping[str, Any], right: Mapping[str, Any]) -> tuple[int | None, int | None]:
    starts = [value for value in (left.get("source_start_ms"), right.get("source_start_ms")) if isinstance(value, int)]
    ends = [value for value in (left.get("source_end_ms"), right.get("source_end_ms")) if isinstance(value, int)]
    return (min(starts) if starts else None, max(ends) if ends else None)


def _source_range(evidence: list[str], segment_times: Mapping[str, tuple[int, int]]) -> tuple[int | None, int | None]:
    ranges = [segment_times[value] for value in evidence if value in segment_times]
    if not ranges:
        return None, None
    return min(item[0] for item in ranges), max(item[1] for item in ranges)


def _claim_supported(claim: str, evidence: str) -> bool:
    claim_terms = _terms(claim)
    evidence_terms = _terms(evidence)
    if not claim_terms or not evidence_terms:
        return False
    required = 1 if len(claim_terms) == 1 else max(2, (len(claim_terms) + 1) // 2)
    return len(claim_terms & evidence_terms) >= required


def _support_confidence(claim: str, evidence: str) -> float:
    claim_terms = _terms(claim)
    evidence_terms = _terms(evidence)
    if not claim_terms or not evidence_terms:
        return 0.0
    return round(len(claim_terms & evidence_terms) / len(claim_terms), 3)


def _tasks_match(left: set[str], right: set[str]) -> bool:
    """Match close task paraphrases without merging unrelated actions."""

    if not left or not right:
        return False
    overlap = len(left & right)
    jaccard = overlap / len(left | right)
    if jaccard >= 0.6:
        return True
    action_terms = {
        "убр", "провер", "восстанов", "подготов", "сопостав", "очист", "организ",
        "перемест", "заверш", "определ", "исключ", "осмотр", "обнов", "переда",
    }
    shared_actions = (left & right) & action_terms
    shared_objects = (left & right) - action_terms
    return bool(shared_actions and shared_objects)


def _topics_match(left: set[str], right: set[str]) -> bool:
    if not left or not right:
        return False
    overlap = len(left & right)
    if overlap >= 2:
        return overlap / min(len(left), len(right)) >= 0.6
    return len(left) == len(right) == 1 and left == right


def _conflicts_compatible(left: Any, right: Any) -> bool:
    return not left or not right or left == right


def _topic_from_candidate(candidate: Mapping[str, Any]) -> str:
    text = _nullable_text(candidate.get("text")) or _nullable_text(candidate.get("context")) or "Обсуждение"
    words = re.findall(r"[A-Za-zА-Яа-яЁё0-9]{3,}", text)
    return " ".join(words[:8])[:180]


def _terms(value: Any) -> set[str]:
    result = set()
    for token in re.findall(r"[A-Za-zА-Яа-яЁё0-9]{3,}", str(value or "").casefold()):
        if token in _STOPWORDS:
            continue
        result.add(_stem(token))
    return {item for item in result if len(item) >= 3}


def _stem(token: str) -> str:
    return re.sub(
        r"(ами|ями|ого|ему|ому|ыми|ими|ать|ить|еть|ого|ему|ому|ами|ями|ов|ев|ей|ах|ях|ом|ем|ам|ям|ою|ею|ую|юю|ая|яя|ое|ее|ы|и|а|я|у|ю|е|о|й|ь)$",
        "",
        token,
    )


def _normalise(value: Any) -> str:
    return " ".join(sorted(_terms(value)))


def _raw_evidence(value: Any, limit: int) -> list[str]:
    if not isinstance(value, list):
        return []
    return list(dict.fromkeys(str(item).removeprefix("SEG-") for item in value))[:limit]


def _unique_ids(value: Any, valid_ids: set[str]) -> list[str]:
    return [item for item in _raw_evidence(value, 12) if item in valid_ids]


def _unique_strings(values: list[str]) -> list[str]:
    return list(dict.fromkeys(str(value) for value in values if str(value)))


def _nullable_text(value: Any) -> str | None:
    text = str(value).strip() if value is not None else ""
    return None if text.casefold() in {"", "null", "none", "не указан", "не указано"} else text

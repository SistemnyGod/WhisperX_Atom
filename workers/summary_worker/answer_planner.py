"""Small, deterministic answer policy builder for Intelligence v2."""

from __future__ import annotations

from typing import Any, Iterable

from .query_understanding import AssistantQueryPlan


def build_answer_plan(
    plan: AssistantQueryPlan,
    conflicts: list[dict[str, Any]] | None = None,
    evidence_bundles: Iterable[Any] | None = None,
) -> dict[str, Any]:
    conflicts = conflicts or []
    policy = [
        "use_only_explicit_evidence",
        "do_not_use_assistant_history_as_evidence",
        "preserve_meeting_scope",
    ]
    if plan.intent in {"RESPONSIBLE", "DEADLINE", "TASK"}:
        policy.append("missing_requested_field_is_not_evidence")
    if plan.intent == "CAUSE":
        policy.extend(["causal_link_must_be_explicit", "do_not_infer_cause_from_adjacent_segments"])
    if conflicts:
        policy.append("report_conflicting_values_with_time_order")
    compact_bundles = []
    for bundle in evidence_bundles or ():
        try:
            compact_bundles.append({
                "meetingId": bundle.meeting_id,
                "topic": bundle.topic,
                "segmentIds": list(bundle.segment_ids),
                "candidateFacts": {
                    key: [
                        {"value": item.get("value"), "evidenceSegmentIds": list(item.get("evidenceSegmentIds") or [])}
                        for item in values
                    ]
                    for key, values in bundle.candidate_facts.items()
                },
            })
        except AttributeError:
            continue
    return {
        "answerType": "CONTRADICTION" if conflicts else plan.answer_type,
        "intent": plan.intent,
        "topic": plan.topic,
        "requiredFields": list(plan.requested_fields),
        "conflicts": conflicts,
        "evidenceBundles": compact_bundles,
        "policy": policy,
    }


_FIELD_MARKERS = {
    "responsible": ("ответствен", "отвеч", "исполн", "поруч", "назнач"),
    "deadline": ("срок", "до ", "дата", "числ", "законч", "заверш"),
    "decision": ("решили", "решение", "договорились", "утверд", "согласовал"),
    "task": ("поруч", "задач", "сделать", "подготовить", "проверить"),
    "cause": ("потому", "по причине", "из-за", "из за", "причин"),
    "status": ("статус", "останов", "продвига", "состояние"),
}


def assess_requested_fields(plan: AssistantQueryPlan, valid: dict[str, tuple[Any, ...]], evidence_ids: list[str]) -> tuple[list[str], list[str]]:
    """Return fields explicitly represented by cited evidence and missing fields.

    This is deliberately lexical and conservative. It is used to label a
    response PARTIAL; it never upgrades a weak claim or creates evidence.
    """
    cited_text = " ".join(str(valid[item][3]).lower() for item in evidence_ids if item in valid)
    supported: list[str] = []
    missing: list[str] = []
    for field in plan.requested_fields:
        markers = _FIELD_MARKERS.get(field, (field,))
        if any(marker in cited_text for marker in markers):
            supported.append(field)
        else:
            missing.append(field)
    return supported, missing


def missing_field_text(field: str) -> str:
    return {
        "responsible": "Подтверждённого ответственного в стенограмме нет.",
        "deadline": "Подтверждённого срока в стенограмме нет.",
        "decision": "Подтверждённого решения в стенограмме нет.",
        "task": "Подтверждённого поручения в стенограмме нет.",
        "cause": "Прямая причина в стенограмме не зафиксирована.",
        "status": "Подтверждённого статуса в стенограмме нет.",
    }.get(field, f"Подтверждённых данных по полю «{field}» в стенограмме нет.")

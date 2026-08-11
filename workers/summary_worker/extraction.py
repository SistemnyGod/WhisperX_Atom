from __future__ import annotations

import re
from typing import Any, Mapping


FACT_TYPES = ("topic", "decision", "action_item", "risk", "question", "fact")


def _fact_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "required": ["type", "text", "evidence_segment_ids"],
        "properties": {
            "type": {"type": "string", "enum": list(FACT_TYPES)},
            "text": {"type": "string", "maxLength": 1200},
            "speaker": {"type": ["string", "null"], "maxLength": 160},
            "responsible_text": {"type": ["string", "null"], "maxLength": 160},
            "deadline_text": {"type": ["string", "null"], "maxLength": 160},
            "evidence_segment_ids": {
                "type": "array",
                "maxItems": 8,
                "items": {"type": "string"},
            },
        },
        "additionalProperties": False,
    }


BLOCK_EXTRACTION_SCHEMA: dict[str, Any] = {
    "type": "object",
    "required": [
        "facts",
        "candidate_topics",
        "candidate_decisions",
        "candidate_action_items",
        "candidate_risks",
        "candidate_questions",
    ],
    "properties": {
        "facts": {"type": "array", "maxItems": 80, "items": _fact_schema()},
        "candidate_topics": {"type": "array", "maxItems": 20, "items": _fact_schema()},
        "candidate_decisions": {"type": "array", "maxItems": 30, "items": _fact_schema()},
        "candidate_action_items": {"type": "array", "maxItems": 40, "items": _fact_schema()},
        "candidate_risks": {"type": "array", "maxItems": 20, "items": _fact_schema()},
        "candidate_questions": {"type": "array", "maxItems": 20, "items": _fact_schema()},
    },
    "$defs": {
        "fact": {
            "type": "object",
            "required": ["type", "text", "evidence_segment_ids"],
            "properties": {
                "type": {"type": "string", "enum": list(FACT_TYPES)},
                "text": {"type": "string", "maxLength": 1200},
                "speaker": {"type": ["string", "null"], "maxLength": 160},
                "responsible_text": {"type": ["string", "null"], "maxLength": 160},
                "deadline_text": {"type": ["string", "null"], "maxLength": 160},
                "evidence_segment_ids": {
                    "type": "array",
                    "maxItems": 8,
                    "items": {"type": "string"},
                },
            },
            "additionalProperties": False,
        }
    },
    "additionalProperties": False,
}


PROTOCOL_FACT_TYPES = ("discussion", "decision", "task", "deadline", "fact")


def _protocol_candidate_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "required": ["type", "text", "evidence_segment_ids"],
        "properties": {
            "type": {"type": "string", "enum": list(PROTOCOL_FACT_TYPES)},
            "text": {"type": "string", "maxLength": 1200},
            "topic_hint": {"type": ["string", "null"], "maxLength": 180},
            "context": {"type": ["string", "null"], "maxLength": 1200},
            "decision": {"type": ["string", "null"], "maxLength": 1200},
            "deadline_text": {"type": ["string", "null"], "maxLength": 160},
            "evidence_segment_ids": {
                "type": "array",
                "maxItems": 12,
                "items": {"type": "string"},
            },
        },
        "additionalProperties": False,
    }


PROTOCOL_BLOCK_EXTRACTION_SCHEMA: dict[str, Any] = {
    "type": "object",
    "required": ["candidates"],
    "properties": {
        "candidates": {"type": "array", "maxItems": 100, "items": _protocol_candidate_schema()},
    },
    "additionalProperties": False,
}


def normalize_block_extraction(payload: Mapping[str, Any]) -> list[dict[str, Any]]:
    """Normalize native extraction output and tolerate legacy map fixtures."""

    facts: list[dict[str, Any]] = []
    raw_facts = payload.get("facts", [])
    if isinstance(raw_facts, list):
        for item in raw_facts:
            normalized = _fact(item, None)
            if normalized:
                facts.append(normalized)

    collections = (
        ("candidate_topics", "topic"),
        ("candidate_decisions", "decision"),
        ("candidate_action_items", "action_item"),
        ("candidate_risks", "risk"),
        ("candidate_questions", "question"),
        ("notable_facts", "fact"),
    )
    for collection, fact_type in collections:
        values = payload.get(collection, [])
        if not isinstance(values, list):
            continue
        for item in values:
            normalized = _fact(item, fact_type)
            if normalized:
                facts.append(normalized)

    # v1 output had no separate extraction fields. Keep old fixtures useful by
    # converting its final collections into candidates before the final reducer.
    if not facts:
        legacy_collections = (
            ("topics", "topic"),
            ("decisions", "decision"),
            ("action_items", "action_item"),
            ("risks", "risk"),
            ("open_questions", "question"),
        )
        for collection, fact_type in legacy_collections:
            values = payload.get(collection, [])
            if not isinstance(values, list):
                continue
            for item in values:
                normalized = _fact(item, fact_type)
                if normalized:
                    facts.append(normalized)
    return deduplicate_facts(facts)


def normalize_protocol_block_extraction(payload: Mapping[str, Any]) -> list[dict[str, Any]]:
    """Normalize protocol candidates without importing legacy responsible fields."""

    values = payload.get("candidates", [])
    if not isinstance(values, list):
        return []
    result: list[dict[str, Any]] = []
    for item in values:
        if not isinstance(item, Mapping):
            continue
        fact_type = str(item.get("type", "fact")).strip().casefold()
        if fact_type not in PROTOCOL_FACT_TYPES:
            continue
        text = str(item.get("text", "")).strip()
        topic_hint = _nullable_text(item.get("topic_hint"))
        context = _nullable_text(item.get("context"))
        decision = _nullable_text(item.get("decision"))
        if not text and not context and not decision:
            continue
        evidence = item.get("evidence_segment_ids", [])
        if not isinstance(evidence, list):
            evidence = []
        result.append({
            "type": fact_type,
            "text": text[:1200],
            "topic_hint": topic_hint[:180] if topic_hint else None,
            "context": context[:1200] if context else None,
            "decision": decision[:1200] if decision else None,
            "deadline_text": _nullable_text(item.get("deadline_text")),
            "evidence_segment_ids": [str(value) for value in evidence[:12]],
        })
    return result


def deduplicate_facts(facts: list[dict[str, Any]]) -> list[dict[str, Any]]:
    unique: list[dict[str, Any]] = []
    index_by_fingerprint: dict[str, int] = {}
    for fact in facts:
        fingerprint = entity_fingerprint(fact)
        if not fingerprint:
            continue
        index = index_by_fingerprint.get(fingerprint)
        if index is None:
            index_by_fingerprint[fingerprint] = len(unique)
            unique.append(fact)
            continue
        current = unique[index]
        evidence = list(current.get("evidence_segment_ids", []))
        for segment_id in fact.get("evidence_segment_ids", []):
            if segment_id not in evidence:
                evidence.append(segment_id)
        current["evidence_segment_ids"] = evidence[:8]
        for field in ("speaker", "responsible_text", "deadline_text"):
            if not current.get(field) and fact.get(field):
                current[field] = fact[field]
    return unique


def entity_fingerprint(fact: Mapping[str, Any]) -> str:
    fact_type = str(fact.get("type", "")).strip().casefold()
    text = _normalise_text(fact.get("text"))
    responsible = _normalise_text(fact.get("responsible_text"))
    if fact_type == "action_item":
        return "|".join((fact_type, text, responsible))
    return "|".join((fact_type, text))


def validate_extracted_facts(
    facts: list[dict[str, Any]],
    valid_ids: set[str],
    segment_texts: Mapping[str, str],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Return (supported, rejected) facts before the final LLM pass."""

    supported: list[dict[str, Any]] = []
    rejected: list[dict[str, Any]] = []
    for fact in facts:
        evidence = [str(value).removeprefix("SEG-") for value in fact.get("evidence_segment_ids", [])]
        fact["evidence_segment_ids"] = [value for value in evidence if value in valid_ids]
        reasons: list[str] = []
        if not fact["evidence_segment_ids"]:
            reasons.append("NO_EVIDENCE")
        elif not _claim_supported(fact, segment_texts):
            reasons.append("UNSUPPORTED")
        if reasons:
            fact["needs_review"] = True
            fact["review_reasons"] = reasons
            rejected.append(fact)
        else:
            fact["needs_review"] = False
            fact["review_reasons"] = []
            fact["evidence_text"] = " ".join(
                segment_texts.get(segment_id, "") for segment_id in fact["evidence_segment_ids"]
            )[:2400]
            supported.append(fact)
    return supported, rejected


def facts_prompt(facts: list[dict[str, Any]]) -> str:
    return "\n\n".join(
        "FACT {index}:\n{payload}".format(index=index, payload=_format_fact(fact))
        for index, fact in enumerate(facts, start=1)
    ) or "FACTS: none. Return empty collections and do not invent content."


def _format_fact(fact: Mapping[str, Any]) -> str:
    fields = [
        f"type={fact.get('type')}",
        f"text={fact.get('text')}",
        f"speaker={fact.get('speaker')}",
        f"responsible={fact.get('responsible_text')}",
        f"deadline={fact.get('deadline_text')}",
        f"deadline_iso={fact.get('deadline_iso')}",
        f"responsible_status={fact.get('responsible_resolution', {}).get('status') if isinstance(fact.get('responsible_resolution'), dict) else None}",
        f"deadline_status={fact.get('deadline_resolution', {}).get('status') if isinstance(fact.get('deadline_resolution'), dict) else None}",
        f"conflict_group={fact.get('conflict_group')}",
        f"resolution_status={fact.get('resolution_status')}",
        f"review_reasons={','.join(str(value) for value in fact.get('review_reasons', []))}",
        f"evidence={','.join(str(value) for value in fact.get('evidence_segment_ids', []))}",
        f"evidence_text={fact.get('evidence_text', '')}",
    ]
    return "\n".join(fields)


def _fact(item: Any, fallback_type: str | None) -> dict[str, Any] | None:
    if isinstance(item, str):
        text = item.strip()
        if not text:
            return None
        return {
            "type": fallback_type or "fact",
            "text": text[:1200],
            "speaker": None,
            "responsible_text": None,
            "deadline_text": None,
            "evidence_segment_ids": [],
        }
    if not isinstance(item, Mapping):
        return None
    fact_type = str(item.get("type", fallback_type or "fact")).strip().casefold()
    if fact_type not in FACT_TYPES:
        fact_type = fallback_type or "fact"
    text = str(
        item.get(
            "text",
            item.get("decision", item.get("summary", item.get("title", item.get("task", "")))),
        )
    ).strip()
    if not text:
        return None
    evidence = item.get("evidence_segment_ids", [])
    if not isinstance(evidence, list):
        evidence = []
    return {
        "type": fact_type,
        "text": text[:1200],
        "speaker": _nullable_text(item.get("speaker")),
        "responsible_text": _nullable_text(item.get("responsible_text", item.get("responsible"))),
        "deadline_text": _nullable_text(item.get("deadline_text", item.get("deadline"))),
        "evidence_segment_ids": [str(value) for value in evidence[:8]],
    }


def _claim_supported(fact: Mapping[str, Any], segment_texts: Mapping[str, str]) -> bool:
    evidence_text = " ".join(segment_texts.get(str(value).removeprefix("SEG-"), "") for value in fact.get("evidence_segment_ids", []))
    claim_terms = _content_terms(str(fact.get("text", "")))
    evidence_terms = _content_terms(evidence_text)
    if not claim_terms or not evidence_terms:
        return False
    required = 1 if len(claim_terms) <= 3 else 2
    return len(claim_terms & evidence_terms) >= required


def _content_terms(text: str) -> set[str]:
    stopwords = {"это", "как", "что", "для", "или", "при", "если", "надо", "будет", "был", "быть", "также", "после", "перед", "когда", "есть", "они", "она", "его", "ее", "их", "мы"}
    return {token for token in re.findall(r"[a-zа-яё0-9]{3,}", text.casefold()) if token not in stopwords}


def _normalise_text(value: Any) -> str:
    return re.sub(r"[^a-zа-яё0-9]+", " ", str(value or "").casefold()).strip()


def _nullable_text(value: Any) -> str | None:
    text = str(value).strip() if value is not None else ""
    return None if text.casefold() in {"", "null", "none", "не указан", "не указано"} else text

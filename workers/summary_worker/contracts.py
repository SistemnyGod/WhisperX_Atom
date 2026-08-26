from __future__ import annotations

from dataclasses import dataclass, field
import re
from typing import Any, Mapping


SUMMARY_SCHEMA_VERSION = "summary-v2"
SUMMARY_PROMPT_VERSION = "summary-v2"
PROTOCOL_RU_SCHEMA_VERSION = "meeting-protocol-ru-v1"
# Keep the persisted protocol schema compatible while making the repaired
# generation distinguishable from the pre-validation prompt.
PROTOCOL_RU_PROMPT_VERSION = "meeting-protocol-ru-v2"
MEETING_PROTOCOL_RU = "MEETING_PROTOCOL_RU"


class SummaryContentError(ValueError):
    """A deterministic content/schema failure which must never be persisted."""

    code = "SUMMARY_SCHEMA_INVALID"

    def __init__(self, reason: str) -> None:
        self.reason = reason
        super().__init__(f"{self.code}:{reason}")


def _evidence_schema(max_items: int = 8) -> dict[str, Any]:
    return {
        "type": "array",
        "maxItems": max_items,
        # llama.cpp's JSON-schema grammar does not support the PCRE ``\s``
        # escape. Evidence IDs are opaque, whitespace-free tokens, so the
        # equivalent portable character class keeps the production grammar
        # valid for both llama.cpp and regular JSON-schema validators.
        "items": {"type": "string", "pattern": "^(SEG-)?[^ ]+$"},
    }


def _validation_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "evidence": {"type": "boolean"},
            "responsible": {"type": "boolean"},
            "deadline": {"type": "boolean"},
            "confidence": {"type": "number", "minimum": 0, "maximum": 1},
            "needs_review": {"type": "boolean"},
            "responsible_status": {"type": "string"},
            "deadline_status": {"type": "string"},
            "review_reasons": {"type": "array", "items": {"type": "string"}},
        },
        "additionalProperties": False,
    }


def _protocol_validation_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "evidence": {"type": "boolean"},
            "deadline": {"type": "boolean"},
            "confidence": {"type": "number", "minimum": 0, "maximum": 1},
            "needs_review": {"type": "boolean"},
            "review_reasons": {"type": "array", "items": {"type": "string"}},
        },
        "additionalProperties": False,
    }


def _protocol_text_item_schema(max_items: int = 20) -> dict[str, Any]:
    """Schema for optional evidence-only protocol collections."""
    return {
        "type": "array",
        "maxItems": max_items,
        "items": {
            "type": "object",
            "required": ["text", "evidence_segment_ids"],
            "properties": {
                "text": {"type": "string"},
                "evidence_segment_ids": _evidence_schema(12),
                "validation": _protocol_validation_schema(),
            },
            "additionalProperties": False,
        },
    }


SUMMARY_SCHEMA_V2: dict[str, Any] = {
    "type": "object",
    "required": [
        "overview",
        "topics",
        "decisions",
        "action_items",
        "risks",
        "open_questions",
        "notable_facts",
    ],
    "properties": {
        "overview": {"type": "string"},
        "topics": {
            "type": "array",
            "maxItems": 20,
            "items": {
                "type": "object",
                "required": ["title", "summary", "evidence_segment_ids"],
                "properties": {
                    "title": {"type": "string"},
                    "summary": {"type": "string"},
                    "evidence_segment_ids": _evidence_schema(),
                    "validation": _validation_schema(),
                },
                "additionalProperties": False,
            },
        },
        "decisions": {
            "type": "array",
            "maxItems": 30,
            "items": {
                "type": "object",
                "required": ["subject", "decision", "evidence_segment_ids"],
                "properties": {
                    "subject": {"type": "string"},
                    "decision": {"type": "string"},
                    "evidence_segment_ids": _evidence_schema(),
                    "validation": _validation_schema(),
                },
                "additionalProperties": False,
            },
        },
        "action_items": {
            "type": "array",
            "maxItems": 40,
            "items": {
                "type": "object",
                "required": [
                    "task",
                    "responsible",
                    "deadline_text",
                    "deadline_iso",
                    "evidence_segment_ids",
                ],
                "properties": {
                    "task": {"type": "string"},
                    "responsible": {"type": ["string", "null"]},
                    "deadline_text": {"type": ["string", "null"]},
                    "deadline_iso": {"type": ["string", "null"]},
                    "evidence_segment_ids": _evidence_schema(),
                    "validation": _validation_schema(),
                },
                "additionalProperties": False,
            },
        },
        "risks": {
            "type": "array",
            "maxItems": 20,
            "items": {
                "type": "object",
                "required": ["text", "severity", "evidence_segment_ids"],
                "properties": {
                    "text": {"type": "string"},
                    "severity": {"type": "string"},
                    "evidence_segment_ids": _evidence_schema(),
                    "validation": _validation_schema(),
                },
                "additionalProperties": False,
            },
        },
        "open_questions": {
            "type": "array",
            "maxItems": 20,
            "items": {
                "type": "object",
                "required": ["text", "evidence_segment_ids"],
                "properties": {
                    "text": {"type": "string"},
                    "evidence_segment_ids": _evidence_schema(),
                    "validation": _validation_schema(),
                },
                "additionalProperties": False,
            },
        },
        "notable_facts": {
            "type": "array",
            "maxItems": 40,
            "items": {
                "type": "object",
                "required": ["text", "evidence_segment_ids"],
                "properties": {
                    "text": {"type": "string"},
                    "evidence_segment_ids": _evidence_schema(),
                    "validation": _validation_schema(),
                },
                "additionalProperties": False,
            },
        },
    },
    "additionalProperties": False,
}


MEETING_PROTOCOL_RU_SCHEMA: dict[str, Any] = {
    "type": "object",
    "required": ["questions_and_decisions", "tasks"],
    "properties": {
        "questions_and_decisions": {
            "type": "array",
            "maxItems": 40,
            "items": {
                "type": "object",
                "required": ["topic", "context", "decision", "evidence_segment_ids"],
                "properties": {
                    "topic": {"type": "string"},
                    "context": {"type": "string"},
                    "decision": {"type": "string"},
                    "evidence_segment_ids": _evidence_schema(12),
                    "validation": _protocol_validation_schema(),
                },
                "additionalProperties": False,
            },
        },
        "tasks": {
            "type": "array",
            "maxItems": 40,
            "items": {
                "type": "object",
                "required": ["task", "deadline_text", "deadline_iso", "evidence_segment_ids"],
                "properties": {
                    "task": {"type": "string"},
                    "deadline_text": {"type": ["string", "null"]},
                    "deadline_iso": {"type": ["string", "null"]},
                    "evidence_segment_ids": _evidence_schema(12),
                    "validation": _protocol_validation_schema(),
                },
                "additionalProperties": False,
            },
        },
        # Additive fields used by deterministic fallback. Existing Qwen
        # responses remain valid because these collections are optional.
        "open_questions": _protocol_text_item_schema(20),
        "notable_facts": _protocol_text_item_schema(40),
        "quality": {
            "type": "object",
            "required": ["status", "score", "review_items", "rejected_items"],
            "properties": {
                "status": {"type": "string", "enum": ["READY", "NEEDS_REVIEW", "FAILED"]},
                "score": {"type": "number", "minimum": 0, "maximum": 1},
                "review_items": {"type": "integer", "minimum": 0},
                "rejected_items": {"type": "integer", "minimum": 0},
                "truncated": {"type": "boolean"},
            },
            "additionalProperties": False,
        },
    },
    "additionalProperties": False,
}


@dataclass(frozen=True)
class MeetingContext:
    """Evidence-backed context supplied to the summary prompt.

    Values are intentionally strings. The worker can build this object from
    either a database row or an outbox payload without changing the summary
    contract, and relative dates remain visible to the validator.
    """

    title: str | None = None
    date: str | None = None
    start: str | None = None
    meeting_type: str | None = None
    series: str | None = None
    department: str | None = None
    participants: tuple[str, ...] = ()
    timezone: str | None = None

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any] | None) -> "MeetingContext":
        if not value:
            return cls()
        participants = value.get("participants", ())
        if not participants:
            participants = ()
        elif isinstance(participants, str):
            participants = (participants,) if participants.strip() else ()
        else:
            participants = tuple(str(item).strip() for item in participants if str(item).strip())
        return cls(
            title=_optional_text(value.get("title")),
            date=_optional_text(value.get("date")),
            start=_optional_text(value.get("start")),
            meeting_type=_optional_text(value.get("meeting_type", value.get("type"))),
            series=_optional_text(value.get("series")),
            department=_optional_text(value.get("department")),
            participants=participants,
            timezone=_optional_text(value.get("timezone")),
        )

    def prompt_text(self) -> str:
        values = {
            "Название": self.title,
            "Дата": self.date,
            "Начало": self.start,
            "Тип": self.meeting_type,
            "Серия": self.series,
            "Подразделение": self.department,
            "Участники": ", ".join(self.participants) if self.participants else None,
            "Часовой пояс": self.timezone,
        }
        lines = [f"{key}: {value}" for key, value in values.items() if value]
        return "\n".join(lines) if lines else "Контекст встречи не передан."


@dataclass(frozen=True)
class SummaryProfile:
    name: str
    description: str
    priorities: tuple[str, ...]

    def prompt_text(self) -> str:
        return f"Профиль: {self.name}. {self.description} Приоритеты: {', '.join(self.priorities)}."


SUMMARY_PROFILES: dict[str, SummaryProfile] = {
    "OPERATIVE": SummaryProfile(
        "OPERATIVE",
        "Оперативное совещание с фокусом на исполнимость.",
        ("decisions", "action_items", "risks", "open_questions"),
    ),
    "GENERAL_MEETING": SummaryProfile(
        "GENERAL_MEETING",
        "Общее деловое совещание.",
        ("overview", "topics", "decisions", "action_items"),
    ),
    "TECHNICAL": SummaryProfile(
        "TECHNICAL",
        "Техническое обсуждение с фокусом на ограничения и факты.",
        ("notable_facts", "risks", "decisions", "open_questions"),
    ),
    "TRAINING": SummaryProfile(
        "TRAINING",
        "Обучение или инструктаж.",
        ("topics", "notable_facts", "open_questions", "action_items"),
    ),
    "INTERVIEW": SummaryProfile(
        "INTERVIEW",
        "Интервью с разделением утверждений и нерешённых вопросов.",
        ("topics", "notable_facts", "open_questions"),
    ),
    "INCIDENT": SummaryProfile(
        "INCIDENT",
        "Разбор инцидента с фокусом на факты, риски и следующие действия.",
        ("notable_facts", "risks", "decisions", "action_items"),
    ),
    MEETING_PROTOCOL_RU: SummaryProfile(
        MEETING_PROTOCOL_RU,
        "Русский структурированный протокол производственного или оперативного совещания. "
        "Формируй только подтверждённые вопросы с решениями и отдельные исполнимые задачи со сроками. "
        "Не формируй ответственных и не придумывай решения, задачи или сроки.",
        ("questions", "decisions", "tasks", "deadlines", "evidence", "deduplication"),
    ),
}


@dataclass(frozen=True)
class ValidationResult:
    evidence: bool
    responsible: bool = True
    deadline: bool = True
    confidence: float | None = None
    needs_review: bool = False
    reasons: tuple[str, ...] = field(default_factory=tuple)

    def to_dict(self) -> dict[str, Any]:
        result: dict[str, Any] = {
            "evidence": self.evidence,
            "responsible": self.responsible,
            "deadline": self.deadline,
            "needs_review": self.needs_review,
        }
        if self.confidence is not None:
            result["confidence"] = max(0.0, min(1.0, self.confidence))
        if self.reasons:
            result["reasons"] = list(self.reasons)
        return result


@dataclass(frozen=True)
class SummaryResult:
    overview: str
    topics: tuple[dict[str, Any], ...]
    decisions: tuple[dict[str, Any], ...]
    action_items: tuple[dict[str, Any], ...]
    risks: tuple[dict[str, Any], ...]
    open_questions: tuple[dict[str, Any], ...]
    notable_facts: tuple[dict[str, Any], ...]
    source_hash: str | None = None
    block_count: int | None = None

    def to_dict(self) -> dict[str, Any]:
        result = {
            "schema_version": SUMMARY_SCHEMA_VERSION,
            "overview": self.overview,
            "summary": self.overview,  # Compatibility for existing clients.
            "topics": list(self.topics),
            "decisions": list(self.decisions),
            "action_items": list(self.action_items),
            "risks": list(self.risks),
            "open_questions": list(self.open_questions),
            "notable_facts": list(self.notable_facts),
        }
        if self.source_hash:
            result["source_hash"] = self.source_hash
        if self.block_count is not None:
            result["block_count"] = self.block_count
        return result


@dataclass(frozen=True)
class MeetingProtocolRuResult:
    questions_and_decisions: tuple[dict[str, Any], ...]
    tasks: tuple[dict[str, Any], ...]
    source_hash: str | None = None
    block_count: int | None = None
    quality: Mapping[str, Any] = field(default_factory=dict)
    prompt_version: str = PROTOCOL_RU_PROMPT_VERSION
    open_questions: tuple[dict[str, Any], ...] = ()
    notable_facts: tuple[dict[str, Any], ...] = ()

    def to_dict(self) -> dict[str, Any]:
        quality = {
            key: self.quality[key]
            for key in (
                "status", "score", "question_count", "task_count", "supported_count",
                "partial_count", "rejected_count", "review_items", "rejected_items",
                "duplicate_topics_removed", "duplicate_tasks_removed",
                "unsupported_deadlines_removed", "reasons",
                "truncated",
            )
            if key in self.quality
        }
        return {
            "schema_version": PROTOCOL_RU_SCHEMA_VERSION,
            "prompt_version": self.prompt_version,
            "profile": MEETING_PROTOCOL_RU,
            "questions_and_decisions": [dict(item) for item in self.questions_and_decisions],
            "tasks": [dict(item) for item in self.tasks],
            "open_questions": [dict(item) for item in self.open_questions],
            "notable_facts": [dict(item) for item in self.notable_facts],
            "source_hash": self.source_hash,
            "block_count": self.block_count,
            "quality": quality,
            "quality_score": quality.get("score"),
        }


def profile_for(value: str | None) -> SummaryProfile:
    return SUMMARY_PROFILES.get(str(value or "GENERAL_MEETING").upper(), SUMMARY_PROFILES["GENERAL_MEETING"])


def normalize_summary_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    """Convert v1 or partially formed Qwen output into bounded Summary v2."""

    # Qwen occasionally wraps the known protocol collections below an
    # ``overview``/``summary`` object.  Repair that *known* shape without ever
    # stringifying arbitrary mappings (the old behaviour produced Python dict
    # reprs in user-visible summaries).
    source = _repair_nested_summary_payload(payload)

    def items(name: str, limit: int) -> list[dict[str, Any]]:
        value = source.get(name, [])
        return [item for item in value if isinstance(item, Mapping)][:limit] if isinstance(value, list) else []

    topics: list[dict[str, Any]] = []
    raw_topics = source.get("topics", [])
    if isinstance(raw_topics, list):
        for item in raw_topics[:20]:
            if isinstance(item, Mapping):
                title = _text_value(item.get("title", item.get("topic", item.get("name", item.get("summary", "")))))
                summary = _text_value(item.get("summary", item.get("context", title)))
                topics.append({
                    "title": title,
                    "summary": summary,
                    "evidence_segment_ids": _evidence_ids(item)[:8],
                })
            elif isinstance(item, str) and item.strip():
                text = item.strip()
                topics.append({"title": text[:220], "summary": text[:1200], "evidence_segment_ids": []})

    decisions = []
    for item in items("decisions", 30):
        decision = _text_value(item.get("decision", item.get("text", item.get("summary", ""))))
        if decision:
            decisions.append({
                "subject": _text_value(item.get("subject", item.get("topic", ""))),
                "decision": decision,
                "evidence_segment_ids": _evidence_ids(item)[:8],
            })

    action_items = []
    for item in items("action_items", 40):
        task = _text_value(item.get("task", item.get("text", item.get("summary", ""))))
        if task:
            deadline_text = item.get("deadline_text", item.get("deadline"))
            action_items.append({
                "task": task,
                "responsible": _nullable_text(item.get("responsible", item.get("owner"))),
                "deadline_text": _nullable_text(deadline_text),
                "deadline_iso": _nullable_text(item.get("deadline_iso")) if isinstance(item.get("deadline_iso"), str) else None,
                "evidence_segment_ids": _evidence_ids(item)[:8],
            })

    def text_collection(name: str, limit: int, include_severity: bool = False) -> list[dict[str, Any]]:
        result = []
        for item in items(name, limit):
            text = _text_value(item.get("text", item.get("summary", item.get("fact", item.get("topic", "")))))
            if text:
                value = {"text": text, "evidence_segment_ids": _evidence_ids(item)[:8]}
                if include_severity:
                    value["severity"] = _text_value(item.get("severity")) or "unknown"
                result.append(value)
        return result

    overview = _text_value(source.get("overview", source.get("summary", "")))
    topics = _deduplicate_items(topics, ("title", "summary"), 20)
    decisions = _deduplicate_items(decisions, ("subject", "decision"), 30)
    action_items = _deduplicate_items(action_items, ("task",), 40)
    risks = _deduplicate_items(text_collection("risks", 20, include_severity=True), ("text",), 20)
    open_questions = _deduplicate_items(text_collection("open_questions", 20), ("text",), 20)
    notable_facts = _deduplicate_items(text_collection("notable_facts", 40), ("text",), 40)
    result = SummaryResult(
        overview=overview[:4000],
        topics=tuple(topics),
        decisions=tuple(decisions),
        action_items=tuple(action_items),
        risks=tuple(risks),
        open_questions=tuple(open_questions),
        notable_facts=tuple(notable_facts),
    ).to_dict()
    return result


def _text_value(value: Any) -> str:
    """Accept user text only when the model returned a scalar string."""

    return value.strip() if isinstance(value, str) else ""


def _evidence_ids(item: Mapping[str, Any]) -> list[str]:
    raw = item.get("evidence_segment_ids")
    if raw is None:
        raw = item.get("evidence", item.get("SEG-ID", item.get("segment_ids", [])))
    if isinstance(raw, str):
        raw = [raw]
    if not isinstance(raw, list):
        return []
    return [value.strip() for value in raw if isinstance(value, str) and value.strip()]


def _repair_nested_summary_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    source = dict(payload)
    nested: list[Mapping[str, Any]] = []
    for key in ("overview", "summary", "result", "protocol"):
        value = source.get(key)
        if isinstance(value, Mapping):
            nested.append(value)
            scalar = value.get("text", value.get("overview", value.get("summary", "")))
            source[key] = _text_value(scalar)
    aliases = {
        "topics": ("topics", "themes"),
        "decisions": ("decisions", "resolutions"),
        "action_items": ("action_items", "tasks", " поручения".strip()),
        "risks": ("risks", "risk"),
        "open_questions": ("open_questions", "questions"),
        "notable_facts": ("notable_facts", "facts"),
    }
    for canonical, names in aliases.items():
        current = source.get(canonical)
        if isinstance(current, list) and current:
            continue
        for candidate in nested:
            for name in names:
                value = candidate.get(name)
                if isinstance(value, list):
                    source[canonical] = value
                    break
            if isinstance(source.get(canonical), list):
                break
    return source


_STRUCTURAL_ARTIFACT_PATTERNS = (
    "{'", "\"SEG-ID\"", "'SEG-ID'", "evidence_segment_ids", "__class__",
)


def validate_summary_content(payload: Mapping[str, Any], valid_ids: set[str] | None = None) -> None:
    """Reject malformed/user-visible structural output before persistence."""

    overview = payload.get("overview")
    if not isinstance(overview, str) or not overview.strip():
        raise SummaryContentError("overview_empty_or_not_text")
    collections = ("topics", "decisions", "action_items", "risks", "open_questions", "notable_facts")
    for collection in collections:
        value = payload.get(collection)
        if not isinstance(value, list):
            raise SummaryContentError(f"{collection}_not_array")
        for item in value:
            if not isinstance(item, Mapping):
                raise SummaryContentError(f"{collection}_item_not_object")
            for field in ("title", "summary", "subject", "decision", "task", "text", "responsible", "deadline_text", "deadline_iso"):
                field_value = item.get(field)
                if field_value is not None and not isinstance(field_value, str):
                    raise SummaryContentError(f"{collection}.{field}_not_text")
                if isinstance(field_value, str) and any(token in field_value for token in _STRUCTURAL_ARTIFACT_PATTERNS):
                    raise SummaryContentError(f"{collection}.{field}_contains_structural_artifact")
            evidence = item.get("evidence_segment_ids", [])
            if not isinstance(evidence, list) or not all(isinstance(value, str) for value in evidence):
                raise SummaryContentError(f"{collection}.evidence_not_array")
            if valid_ids is not None:
                normalized = {value.removeprefix("SEG-") for value in valid_ids}
                if any(value.removeprefix("SEG-") not in normalized for value in evidence):
                    raise SummaryContentError(f"{collection}.evidence_unknown")
    if any(token in overview for token in _STRUCTURAL_ARTIFACT_PATTERNS):
        raise SummaryContentError("overview_contains_structural_artifact")


def _deduplicate_items(items: list[dict[str, Any]], key_fields: tuple[str, ...], limit: int) -> list[dict[str, Any]]:
    unique: list[dict[str, Any]] = []
    indexes: dict[str, int] = {}
    for item in items:
        key = " ".join(_normalise_key(item.get(field)) for field in key_fields).strip()
        if not key:
            continue
        existing_index = indexes.get(key)
        if existing_index is None:
            indexes[key] = len(unique)
            unique.append(item)
            continue
        existing = unique[existing_index]
        evidence = list(existing.get("evidence_segment_ids", []))
        for segment_id in item.get("evidence_segment_ids", []):
            if segment_id not in evidence:
                evidence.append(segment_id)
        existing["evidence_segment_ids"] = evidence[:8]
        if not existing.get("responsible") and item.get("responsible"):
            existing["responsible"] = item["responsible"]
        if not existing.get("deadline_iso") and item.get("deadline_iso"):
            existing["deadline_iso"] = item["deadline_iso"]
        if not existing.get("deadline_text") and item.get("deadline_text"):
            existing["deadline_text"] = item["deadline_text"]
    return unique[:limit]


def _normalise_key(value: Any) -> str:
    return re.sub(r"\s+", " ", str(value or "").casefold()).strip()


def _optional_text(value: Any) -> str | None:
    text = str(value).strip() if value is not None else ""
    return text or None


def _nullable_text(value: Any) -> str | None:
    text = _optional_text(value)
    if text and text.lower() in {"null", "none", "не указано", "не указан", "не указано"}:
        return None
    return text

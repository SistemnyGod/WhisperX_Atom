"""Deterministic Assistant query understanding for Mifodiy Intelligence v2.

This module deliberately does not resolve meeting scope and does not generate
answers.  ``AssistantModeResolver`` remains the owner of GENERAL/CURRENT/
LIVE/MEMORY routing.  The plan produced here only describes what the user is
asking for so retrieval and the answer policy can be specialised safely.
"""

from __future__ import annotations

from dataclasses import dataclass
import re
from typing import Any, Protocol


class SemanticProvider(Protocol):
    name: str

    def embed(self, text: str) -> tuple[float, ...]:
        ...


_PROTOTYPES: dict[str, tuple[str, ...]] = {
    "RESPONSIBLE": ("кто отвечает", "кто ответственный", "кому поручили", "кто исполнитель"),
    "DEADLINE": ("какой срок", "к какому числу", "когда должны сделать", "когда закончить"),
    "DECISION": ("что решили", "к чему пришли", "что согласовали", "что утвердили"),
    "CAUSE": ("почему перенесли", "по какой причине", "почему решили", "из за чего"),
    "TASK": ("что поручили", "какие задачи", "что нужно сделать", "какие поручения"),
    "STATUS": ("на чем остановились", "текущий статус", "как продвигается работа"),
    "TIMELINE": ("что обсуждали сначала", "что было потом", "как развивались события"),
    "COMPARISON": ("чем отличаются варианты", "сравни варианты", "в чем разница"),
    "SUMMARY": ("кратко подведи итог", "краткий итог", "резюме встречи"),
    "FACT_LOOKUP": ("что говорили по теме", "что известно по вопросу", "что обсуждали"),
}

_INTENT_KEYWORDS: dict[str, tuple[str, ...]] = {
    "RESPONSIBLE": ("кто отвечает", "ответственн", "исполнител", "кому поруч", "кто назнач"),
    "DEADLINE": ("срок", "к какому числу", "до какого", "когда законч", "когда сдел"),
    "DECISION": ("что решили", "решили", "договорились", "согласовали", "утвердили", "к чему пришли"),
    "CAUSE": ("почему", "по какой причине", "из-за чего", "из за чего", "причин"),
    "TASK": ("поручили", "поручение", "задач", "что нужно сделать", "сделать кому"),
    "STATUS": ("на чем останов", "на чём останов", "текущий статус", "как продвига", "состояние работ"),
    "TIMELINE": ("сначала", "потом", "по порядку", "хронолог", "последовательно"),
    "COMPARISON": ("чем отлич", "сравни", "разниц", "вариант а", "вариант б"),
    "SUMMARY": ("кратко", "итог", "резюме", "подведи"),
}

_FOLLOW_UP_RE = re.compile(r"^(?:а|и|ну|тогда)\s+(кто|срок|когда|почему|как|что|где|подробнее|ещ[её])\b", re.IGNORECASE)
_DATE_RE = re.compile(r"\b(?:\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?|\d{1,2}\s+(?:январ|феврал|март|апрел|мая|июн|июл|август|сентябр|октябр|ноябр|декабр))", re.IGNORECASE)
_PERSON_RE = re.compile(r"\b(?:для|у|с|кому|кто)\s+([А-ЯЁ][а-яё-]{2,}(?:\s+[А-ЯЁ][а-яё-]{2,})?)")
_PROTOTYPE_VECTOR_CACHE: dict[int, dict[str, tuple[tuple[float, ...], ...]]] = {}


def _normalize(value: str) -> str:
    return " ".join(re.sub(r"[^\wА-Яа-яЁё-]+", " ", value or "").lower().replace("ё", "е").split())


def _cosine(left: tuple[float, ...], right: tuple[float, ...]) -> float:
    size = min(len(left), len(right))
    if size == 0:
        return 0.0
    value = sum(left[index] * right[index] for index in range(size))
    return max(-1.0, min(1.0, value))


def _topic(query: str, follow_up: bool, previous: "AssistantQueryPlan | None") -> str | None:
    if follow_up and previous and previous.topic:
        return previous.topic
    value = _normalize(query)
    value = re.sub(r"^(?:а|и|ну|тогда)\s+", "", value)
    value = re.sub(r"^(?:кто|что|какой|какая|какое|какие|когда|почему|зачем|как|где|сколько)\s+", "", value)
    # Remove conversational/question scaffolding wherever it occurs, not only
    # at the beginning.  Multi-field questions such as "что решили и кто
    # отвечает за ремонт" should yield the useful topic "за ремонт", rather
    # than leaving "и кто" in the retrieval key.
    value = re.sub(
        r"\b(?:а|и|ну|тогда|кто|что|какой|какая|какое|какие|когда|почему|зачем|как|где|сколько|"
        r"отвечает|ответственный|срок|решили|решение|поручили|причина|кратко|расскажи|подробнее)\b",
        " ",
        value,
    )
    value = " ".join(value.split())
    return value[:160] if len(value) >= 3 else None


@dataclass(frozen=True)
class AssistantQueryPlan:
    intent: str
    topic: str | None
    person: str | None
    date_range: str | None
    requested_fields: tuple[str, ...]
    requires_explanation: bool
    requires_comparison: bool
    follow_up: bool
    confidence: float
    answer_type: str
    retrieval_terms: tuple[str, ...]

    @property
    def neighbour_window(self) -> int:
        return {"CAUSE": 4, "TIMELINE": 3, "SUMMARY": 2, "COMPARISON": 2}.get(self.intent, 1)

    def retrieval_query(self, original: str) -> str:
        pieces = [original.strip()]
        if self.topic and self.topic.lower() not in _normalize(original):
            pieces.append(self.topic)
        pieces.extend(self.retrieval_terms)
        return " ".join(piece for piece in pieces if piece).strip()

    def to_dict(self) -> dict[str, Any]:
        return {
            "intent": self.intent,
            "topic": self.topic,
            "person": self.person,
            "dateRange": self.date_range,
            "requestedFields": list(self.requested_fields),
            "requiresExplanation": self.requires_explanation,
            "requiresComparison": self.requires_comparison,
            "followUp": self.follow_up,
            "confidence": round(self.confidence, 3),
            "answerType": self.answer_type,
            "neighbourWindow": self.neighbour_window,
            "retrievalTerms": list(self.retrieval_terms),
        }

    @staticmethod
    def from_mapping(value: dict[str, Any] | None) -> "AssistantQueryPlan | None":
        if not value:
            return None
        try:
            intent = str(value.get("intent") or "FACT_LOOKUP")
            requested_values = value.get("requestedFields") or value.get("requested_fields")
            if requested_values is None:
                requested_values = {
                    "RESPONSIBLE": ("responsible",), "DEADLINE": ("deadline",), "DECISION": ("decision",),
                    "CAUSE": ("cause",), "TASK": ("task",), "STATUS": ("status",), "TIMELINE": ("timeline",),
                    "COMPARISON": ("comparison",), "SUMMARY": ("summary",),
                }.get(intent, ("facts",))
            return AssistantQueryPlan(
                intent=intent,
                topic=str(value.get("topic")) if value.get("topic") else None,
                person=str(value.get("person")) if value.get("person") else None,
                date_range=str(value.get("dateRange") or value.get("date_range")) if value.get("dateRange") or value.get("date_range") else None,
                requested_fields=tuple(str(item) for item in requested_values),
                requires_explanation=bool(value.get("requiresExplanation", value.get("requires_explanation", False))),
                requires_comparison=bool(value.get("requiresComparison", value.get("requires_comparison", False))),
                follow_up=bool(value.get("followUp", value.get("follow_up", False))),
                confidence=float(value.get("confidence", 0.0)),
                answer_type=str(value.get("answerType") or value.get("answer_type") or "DIRECT_FACT"),
                retrieval_terms=tuple(str(item) for item in (value.get("retrievalTerms") or value.get("retrieval_terms") or ())),
            )
        except (TypeError, ValueError):
            return None


def _semantic_intent(query: str, provider: SemanticProvider | None) -> tuple[str | None, float]:
    if provider is None or not str(getattr(provider, "name", "")).startswith(("onnx-cpu:", "sentence-transformers:")):
        return None, 0.0
    try:
        query_vector = provider.embed(query)
        cache_key = id(provider)
        prototype_vectors = _PROTOTYPE_VECTOR_CACHE.setdefault(
            cache_key,
            {intent: tuple(provider.embed(item) for item in prototypes) for intent, prototypes in _PROTOTYPES.items()},
        )
        best_intent, best_score = None, -1.0
        for intent, prototypes in prototype_vectors.items():
            score = max((_cosine(query_vector, item) for item in prototypes), default=0.0)
            if score > best_score:
                best_intent, best_score = intent, score
        return (best_intent, best_score) if best_score >= 0.72 else (None, best_score)
    except Exception:
        return None, 0.0


def understand_query(query: str, previous: AssistantQueryPlan | None = None, semantic_provider: SemanticProvider | None = None) -> AssistantQueryPlan:
    normalized = _normalize(query)
    follow_up = bool(_FOLLOW_UP_RE.search(normalized)) or (len(normalized.split()) <= 3 and normalized.startswith(("а ", "и ")))
    intent = "FACT_LOOKUP"
    confidence = 0.58
    matched_hits: list[tuple[int, str]] = []
    for candidate, keywords in _INTENT_KEYWORDS.items():
        positions = [normalized.find(keyword) for keyword in keywords if normalized.find(keyword) >= 0]
        if positions:
            matched_hits.append((min(positions), candidate))
    matched_intents = [candidate for _position, candidate in sorted(matched_hits, key=lambda item: (item[0], item[1]))]
    if matched_intents:
        intent, confidence = matched_intents[0], 0.94
    if follow_up and intent == "FACT_LOOKUP":
        intent = "FOLLOW_UP"
        confidence = 0.88
    semantic_intent, semantic_score = _semantic_intent(normalized, semantic_provider)
    if confidence < 0.8 and semantic_intent:
        intent, confidence = semantic_intent, min(0.86, max(0.72, semantic_score))
    if follow_up and intent == "FOLLOW_UP" and previous:
        # A generic "а подробнее?" keeps the prior intent as well as the
        # topic.  It remains marked as a follow-up so the caller can explain
        # why the scope was inherited; it does not turn assistant text into
        # evidence.
        intent = previous.intent or "FOLLOW_UP"
        confidence = min(0.84, max(confidence, previous.confidence * 0.9))
        requested = previous.requested_fields
    else:
        requested = {
            "RESPONSIBLE": ("responsible",), "DEADLINE": ("deadline",), "DECISION": ("decision",),
            "CAUSE": ("cause",), "TASK": ("task",), "STATUS": ("status",), "TIMELINE": ("timeline",),
            "COMPARISON": ("comparison",), "SUMMARY": ("summary",), "FOLLOW_UP": (),
        }.get(intent, ("facts",))
        if len(matched_intents) > 1:
            requested = tuple(dict.fromkeys(
                field
                for matched in matched_intents
                for field in {
                    "RESPONSIBLE": ("responsible",), "DEADLINE": ("deadline",), "DECISION": ("decision",),
                    "CAUSE": ("cause",), "TASK": ("task",), "STATUS": ("status",), "TIMELINE": ("timeline",),
                    "COMPARISON": ("comparison",), "SUMMARY": ("summary",),
                }.get(matched, ("facts",))
            ))
    requires_explanation = intent == "CAUSE" or any(word in normalized for word in ("почему", "объясни", "подробно"))
    requires_comparison = intent == "COMPARISON"
    answer_type = "COMPARISON" if requires_comparison else "SUMMARY" if intent == "SUMMARY" else "MULTI_FACT" if len(requested) > 1 else "DIRECT_FACT"
    if intent == "FOLLOW_UP":
        answer_type = "PARTIAL"
    terms = _PROTOTYPES.get(intent, ("факт",))
    person_match = _PERSON_RE.search(query or "")
    person = person_match.group(1).strip() if person_match else (previous.person if follow_up and previous else None)
    return AssistantQueryPlan(
        intent=intent,
        topic=_topic(query, follow_up, previous),
        person=person,
        date_range=_DATE_RE.search(query or "").group(0) if _DATE_RE.search(query or "") else (previous.date_range if follow_up and previous else None),
        requested_fields=tuple(requested),
        requires_explanation=requires_explanation,
        requires_comparison=requires_comparison,
        follow_up=follow_up,
        confidence=confidence,
        answer_type=answer_type,
        retrieval_terms=tuple(terms),
    )

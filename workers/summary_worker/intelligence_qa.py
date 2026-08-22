"""Deterministic, synthetic reasoning-corpus support for Mifodiy QA.

The corpus contains no customer meetings or transcripts.  It is generated
from Russian templates and is suitable for checking query understanding,
answer policy and safety invariants before an authenticated production run.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
import hashlib
from typing import Any, Iterable

from .query_understanding import AssistantQueryPlan, understand_query


# The detailed category table in the product plan sums to 400 rather than
# 300.  Keep the explicit per-category targets so a release report cannot
# silently undercount the requested safety coverage.
CASE_COUNTS: dict[str, int] = {
    "single_fact": 40,
    "decision": 40,
    "responsible": 30,
    "deadline": 30,
    "cause": 40,
    "multi_evidence": 40,
    "contradiction": 30,
    "partial_answer": 30,
    "follow_up": 40,
    "comparison": 30,
    "deliberately_unanswerable": 50,
}


@dataclass(frozen=True)
class ReasoningCase:
    case_id: str
    category: str
    question: str
    expected_intent: str
    expected_answer_type: str
    expected_outcome: str
    must_not_infer: tuple[str, ...] = ()

    @property
    def question_sha256(self) -> str:
        return hashlib.sha256(self.question.encode("utf-8")).hexdigest()

    def to_dict(self) -> dict[str, Any]:
        # Questions are intentionally omitted from acceptance artifacts by
        # the runner; this mapping is for local corpus generation only.
        return {
            "caseId": self.case_id,
            "category": self.category,
            "question": self.question,
            "expectedIntent": self.expected_intent,
            "answerType": self.expected_answer_type,
            "expectedOutcome": self.expected_outcome,
            "mustNotInfer": list(self.must_not_infer),
        }


_TEMPLATES: dict[str, tuple[str, str, str, str]] = {
    "single_fact": ("Что говорили по насосу {n}?", "FACT_LOOKUP", "DIRECT_FACT", "ANSWER"),
    "decision": ("Что решили по ремонту {n}?", "DECISION", "DIRECT_FACT", "ANSWER"),
    "responsible": ("Кто отвечает за ремонт {n}?", "RESPONSIBLE", "DIRECT_FACT", "ANSWER"),
    "deadline": ("Какой срок ремонта {n}?", "DEADLINE", "DIRECT_FACT", "ANSWER"),
    "cause": ("Почему перенесли ремонт {n}?", "CAUSE", "DIRECT_FACT", "ANSWER"),
    "multi_evidence": ("Что решили и кто отвечает за объект {n}?", "DECISION", "MULTI_FACT", "ANSWER"),
    "contradiction": ("Какой окончательный срок объекта {n}?", "DEADLINE", "CONTRADICTION", "ANSWER"),
    "partial_answer": ("Кто отвечает и какой срок объекта {n}?", "RESPONSIBLE", "PARTIAL", "ANSWER"),
    "follow_up": ("А кто отвечает?", "RESPONSIBLE", "DIRECT_FACT", "ANSWER"),
    "comparison": ("Чем отличаются вариант А и вариант Б для объекта {n}?", "COMPARISON", "COMPARISON", "ANSWER"),
    "deliberately_unanswerable": ("Какой подтверждённый срок объекта {n}, если его не называли?", "DEADLINE", "NO_EVIDENCE", "NO_EVIDENCE"),
}


def generate_reasoning_cases(counts: dict[str, int] | None = None) -> list[ReasoningCase]:
    counts = dict(counts or CASE_COUNTS)
    cases: list[ReasoningCase] = []
    for category, count in counts.items():
        if category not in _TEMPLATES:
            raise ValueError(f"unknown_reasoning_category:{category}")
        template, intent, answer_type, outcome = _TEMPLATES[category]
        for index in range(1, max(0, int(count)) + 1):
            object_name = f"{category.replace('_', ' ')} {index}"
            question = template.format(n=object_name)
            must_not_infer = ("person", "date", "number", "cause") if category in {"cause", "contradiction", "deliberately_unanswerable"} else ()
            cases.append(ReasoningCase(f"{category}-{index:03d}", category, question, intent, answer_type, outcome, must_not_infer))
    return cases


def validate_reasoning_corpus(cases: Iterable[ReasoningCase]) -> dict[str, Any]:
    values = list(cases)
    ids = [item.case_id for item in values]
    categories = Counter(item.category for item in values)
    invalid = [item.case_id for item in values if item.category not in CASE_COUNTS or not item.question.strip()]
    duplicate_ids = sorted({item for item in ids if ids.count(item) > 1})
    missing = {key: value - categories.get(key, 0) for key, value in CASE_COUNTS.items() if categories.get(key, 0) != value}
    return {
        "caseCount": len(values),
        "categoryCounts": dict(categories),
        "duplicateIds": duplicate_ids,
        "invalidCases": invalid,
        "countMismatches": missing,
        "valid": not duplicate_ids and not invalid and not missing,
    }


def evaluate_intent_cases(cases: Iterable[ReasoningCase]) -> dict[str, Any]:
    rows: list[dict[str, Any]] = []
    for case in cases:
        plan: AssistantQueryPlan = understand_query(case.question)
        rows.append({
            "caseId": case.case_id,
            "questionSha256": case.question_sha256,
            "expectedIntent": case.expected_intent,
            "actualIntent": plan.intent,
            "confidence": plan.confidence,
            "passed": plan.intent == case.expected_intent,
        })
    passed = sum(1 for item in rows if item["passed"])
    return {"caseCount": len(rows), "passed": passed, "failed": len(rows) - passed, "accuracy": passed / len(rows) if rows else 0.0, "results": rows}


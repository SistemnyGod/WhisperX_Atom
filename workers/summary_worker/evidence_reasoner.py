"""Evidence-only helpers used before Qwen synthesis.

The detector is intentionally conservative.  It reports a possible conflict
to the prompt, but never invents a winner and never replaces transcript
evidence with a derived fact.
"""

from __future__ import annotations

import re
from typing import Any

from .query_understanding import AssistantQueryPlan


_DATE_OR_NUMBER = re.compile(r"\b(?:\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?|\d{1,2}\s+(?:январ\w*|феврал\w*|март\w*|апрел\w*|ма[яй]\w*|июн\w*|июл\w*|август\w*|сентябр\w*|октябр\w*|ноябр\w*|декабр\w*)|\d+(?:[.,]\d+)?\s*(?:руб|рублей|кг|кВт|м|дн|дней|час))\b", re.IGNORECASE)
_NAME = re.compile(r"\b[А-ЯЁ][а-яё]{2,}(?:\s+[А-ЯЁ][а-яё]{2,})?\b")
_GENERIC = {"Ответ", "Ответственный", "Срок", "Решение", "Причина", "Итог", "Тогда", "Это"}


def detect_conflicts(valid: dict[str, tuple[str, int, int, str, str, str, int]], plan: AssistantQueryPlan) -> list[dict[str, Any]]:
    if plan.intent not in {"DEADLINE", "RESPONSIBLE", "COMPARISON", "STATUS"}:
        return []
    values: dict[str, list[str]] = {}
    for segment_id, (_meeting, start_ms, _end_ms, text, *_rest) in valid.items():
        lowered = text.lower()
        if plan.intent == "DEADLINE" and not any(token in lowered for token in ("срок", "до ", "законч", "заверш", "дата", "числ")):
            continue
        if plan.intent == "RESPONSIBLE" and not any(token in lowered for token in ("ответствен", "отвеч", "исполн", "поруч", "назнач")):
            continue
        matches = _DATE_OR_NUMBER.findall(text) if plan.intent in {"DEADLINE", "COMPARISON"} else [name for name in _NAME.findall(text) if name not in _GENERIC]
        for value in matches:
            normalized = " ".join(value.lower().replace("ё", "е").split())
            values.setdefault(normalized, []).append(segment_id)
    if len(values) <= 1:
        return []
    return [
        {"value": value, "evidenceSegmentIds": ids[:8]}
        for value, ids in sorted(values.items(), key=lambda item: item[0])
    ][:8]

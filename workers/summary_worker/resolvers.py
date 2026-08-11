from __future__ import annotations

import re
from datetime import date, timedelta
from typing import Any, Mapping

from .contracts import MeetingContext


_MONTHS = {
    "января": 1, "февраля": 2, "марта": 3, "апреля": 4,
    "мая": 5, "июня": 6, "июля": 7, "августа": 8,
    "сентября": 9, "октября": 10, "ноября": 11, "декабря": 12,
}
_WEEKDAYS = {
    "понедельник": 0, "понедельника": 0,
    "вторник": 1, "вторника": 1,
    "среда": 2, "среды": 2,
    "четверг": 3, "четверга": 3,
    "пятница": 4, "пятницы": 4,
    "суббота": 5, "субботы": 5,
    "воскресенье": 6, "воскресенья": 6,
}


def resolve_responsible(
    responsible_text: str | None,
    participants: tuple[str, ...] = (),
) -> dict[str, Any]:
    """Resolve only an explicitly named participant; never infer from speaker."""

    query = _tokens(responsible_text)
    if not query:
        return {"status": "UNRESOLVED", "resolved": None, "reason": "NO_EXPLICIT_PERSON"}
    matches = [name for name in participants if _name_matches(query, _tokens(name))]
    if len(matches) == 1:
        return {"status": "RESOLVED", "resolved": matches[0], "reason": None}
    if len(matches) > 1:
        return {"status": "AMBIGUOUS", "resolved": None, "reason": "AMBIGUOUS_PERSON"}
    return {"status": "UNRESOLVED", "resolved": None, "reason": "PERSON_NOT_IN_CONTEXT"}


def resolve_deadline(
    deadline_text: str | None,
    meeting_date: str | None,
    timezone: str | None = None,
) -> dict[str, Any]:
    """Resolve conservative Russian date expressions without inventing a date."""

    text = str(deadline_text or "").strip()
    if not text:
        return {"status": "UNRESOLVED", "deadline_iso": None, "reason": "NO_EXPLICIT_DEADLINE"}
    base = _parse_date(meeting_date)
    if base is None:
        return {"status": "UNRESOLVED", "deadline_iso": None, "reason": "MEETING_DATE_REQUIRED"}
    lowered = text.casefold()
    if "сегодня" in lowered:
        return _resolved(base)
    if "завтра" in lowered:
        return _resolved(base + timedelta(days=1))
    if "послезавтра" in lowered:
        return _resolved(base + timedelta(days=2))

    try:
        if re.fullmatch(r"\d{4}-\d{2}-\d{2}", lowered):
            return _resolved(date.fromisoformat(lowered))
    except ValueError:
        return _unresolved("INVALID_DATE")

    weekday = next((value for word, value in _WEEKDAYS.items() if word in lowered), None)
    if weekday is not None:
        days = (weekday - base.weekday()) % 7
        return _resolved(base + timedelta(days=days or 7))

    match = re.search(r"(?<!\d)(\d{1,2})[.\-/](\d{1,2})(?:[.\-/](\d{4}))?", lowered)
    if match:
        day, month = int(match.group(1)), int(match.group(2))
        year = int(match.group(3) or base.year)
        try:
            value = date(year, month, day)
        except ValueError:
            return _unresolved("INVALID_DATE")
        if not match.group(3) and value < base:
            value = date(base.year + 1, month, day)
        return _resolved(value)

    match = re.search(r"(?<!\d)(\d{1,2})\s+([а-яё]+)(?:\s+(\d{4}))?", lowered)
    if match and match.group(2) in _MONTHS:
        year = int(match.group(3) or base.year)
        try:
            value = date(year, _MONTHS[match.group(2)], int(match.group(1)))
        except ValueError:
            return _unresolved("INVALID_DATE")
        if not match.group(3) and value < base:
            value = date(base.year + 1, value.month, value.day)
        return _resolved(value)

    return _unresolved("AMBIGUOUS_DEADLINE")


def resolve_extracted_facts(
    facts: list[dict[str, Any]],
    context: MeetingContext,
) -> list[dict[str, Any]]:
    for fact in facts:
        if fact.get("type") != "action_item":
            continue
        responsible = resolve_responsible(fact.get("responsible_text"), context.participants)
        deadline = resolve_deadline(fact.get("deadline_text"), context.date, context.timezone)
        fact["responsible_resolution"] = responsible
        fact["deadline_resolution"] = deadline
        if responsible["status"] != "RESOLVED":
            fact["responsible_text"] = None
        elif responsible["resolved"]:
            fact["responsible_text"] = responsible["resolved"]
        if deadline["deadline_iso"]:
            fact["deadline_iso"] = deadline["deadline_iso"]
    return facts


def resolve_summary_action_items(
    payload: dict[str, Any],
    context: MeetingContext,
) -> dict[str, Any]:
    """Apply the same conservative rules to final Summary v2 action items."""

    for item in payload.get("action_items", []):
        if not isinstance(item, dict):
            continue
        responsible = resolve_responsible(item.get("responsible"), context.participants)
        deadline = resolve_deadline(item.get("deadline_text") or item.get("deadline_iso"), context.date, context.timezone)
        item["responsible"] = responsible["resolved"]
        if deadline["deadline_iso"]:
            item["deadline_iso"] = deadline["deadline_iso"]
        validation = item.get("validation") if isinstance(item.get("validation"), dict) else {}
        validation["responsible_status"] = responsible["status"]
        validation["deadline_status"] = deadline["status"]
        reasons = list(validation.get("review_reasons", []))
        for reason in (responsible.get("reason"), deadline.get("reason")):
            if reason and reason not in {"NO_EXPLICIT_PERSON", "NO_EXPLICIT_DEADLINE"} and reason not in reasons:
                reasons.append(reason)
        if reasons:
            validation["review_reasons"] = reasons
            item["needs_review"] = True
        item["validation"] = validation
    return payload


def _resolved(value: date) -> dict[str, Any]:
    return {"status": "RESOLVED", "deadline_iso": value.isoformat(), "reason": None}


def _unresolved(reason: str) -> dict[str, Any]:
    return {"status": "UNRESOLVED", "deadline_iso": None, "reason": reason}


def _parse_date(value: str | None) -> date | None:
    text = str(value or "").strip()
    if not text:
        return None
    try:
        return date.fromisoformat(text[:10])
    except ValueError:
        pass
    match = re.fullmatch(r"(\d{1,2})[.]([0-9]{1,2})[.]([0-9]{4})", text)
    if match:
        try:
            return date(int(match.group(3)), int(match.group(2)), int(match.group(1)))
        except ValueError:
            return None
    return None


def _tokens(value: str | None) -> set[str]:
    return set(re.findall(r"[a-zа-яё0-9]{2,}", str(value or "").casefold()))


def _name_matches(query: set[str], candidate: set[str]) -> bool:
    if not query or not candidate:
        return False
    return query.issubset(candidate) or candidate.issubset(query)

"""Dependency-light, deterministic evidence grounding gates."""

from __future__ import annotations

import re
from typing import Any


_GROUNDING_STOPWORDS = {
    "это", "этот", "эта", "эти", "что", "как", "кто", "где", "когда", "были", "было",
    "будет", "есть", "для", "при", "или", "и", "в", "во", "на", "по", "из", "с", "со",
    "у", "к", "о", "об", "за", "да", "так", "мы", "они", "он", "она", "их",
    "его", "её", "может", "можно", "нужно", "решили", "говорили", "сказал", "сказали",
}

_POLARITY_PATTERNS = (
    ("NEGATED_COMPLETION", re.compile(r"\bне\s+(?:готов\w*|сдела\w*|выполн\w*|заверш\w*|закры\w*|подготов\w*)\b", re.IGNORECASE)),
    ("NEGATED_CHANGE", re.compile(r"\bне\s+(?:перенос\w*|измен\w*|замен\w*|переда\w*)\b", re.IGNORECASE)),
    ("NEGATION", re.compile(r"\b(?:не|нет|никогда|невозможно|отсутств\w*)\b", re.IGNORECASE)),
    ("COMPLETION", re.compile(r"\b(?:готов\w*|сдела\w*|выполн\w*|заверш\w*|закры\w*|подготов\w*)\b", re.IGNORECASE)),
    ("CHANGE", re.compile(r"\b(?:перенос\w*|измен\w*|замен\w*|переда\w*)\b", re.IGNORECASE)),
)

_PROTECTED_NUMBER_RE = re.compile(
    r"(?<![\w])(?:\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?|\d{1,2}\s+(?:январ\w*|феврал\w*|март\w*|апрел\w*|ма[яй]\w*|июн\w*|июл\w*|август\w*|сентябр\w*|октябр\w*|ноябр\w*|декабр\w*)|\d+(?:[.,]\d+)?)(?![\w])",
    re.IGNORECASE,
)

_RU_INFLECTION_SUFFIXES = ("иями", "ами", "ями", "ого", "ему", "ому", "ов", "ев", "ам", "ям", "ах", "ях", "ы", "и", "а", "я", "у", "ю", "е", "о")
_NON_NAME_CAPITALIZED = {
    "Ответ", "Ответственный", "Срок", "Решение", "Решили", "Поручение", "Задача",
    "Итог", "Итоги", "Вопрос", "Причина", "Причины", "Дата", "Нужно", "Нужен",
    "Нужна", "Можно", "Следует", "Поэтому", "Также", "Тогда", "Это", "Этот", "Эта",
}

_PREDICATE_MARKERS = {
    "responsible": ("ответствен", "отвечает", "назначен", "исполнитель"),
    "deadline": ("срок", "до ", "дата", "заверш"),
    "decision": ("решил", "решен", "договорил", "утверд", "согласовал"),
    "task": ("поруч", "задач", "подготов", "провер", "сдела"),
    "cause": ("потому что", "из-за", "из за", "по причине", "поэтому"),
    "status": ("статус", "продвига", "останов", "готов", "заверш"),
}


def claims_are_structurally_grounded(result: dict[str, Any], valid: dict[str, tuple[str, int, int, str, str, str, int]], assistant_mode: str) -> bool:
    claims = result.get("claims")
    if assistant_mode == "GENERAL_CHAT":
        return not claims or all(not isinstance(item, dict) or not (item.get("evidenceIds") or []) for item in claims)
    if not isinstance(claims, list) or not claims:
        return False
    for claim in claims:
        if not isinstance(claim, dict) or not str(claim.get("text", "")).strip():
            return False
        ids = claim.get("evidenceIds")
        if not isinstance(ids, list) or not ids or any(str(item).removeprefix("SEG-") not in valid for item in ids):
            return False
        structured_fields = [claim.get(name) for name in ("subject", "predicate", "value", "polarity")]
        if any(field is not None for field in structured_fields):
            if any(not isinstance(field, str) or not field.strip() for field in structured_fields):
                return False
            if str(claim.get("polarity")).upper() not in {"POSITIVE", "NEGATIVE", "UNKNOWN"}:
                return False
    return True


def grounding_tokens(value: str) -> set[str]:
    result: set[str] = set()
    for token in re.findall(r"[\wА-Яа-яЁё-]{2,}", (value or "").lower()):
        token = token.replace("ё", "е").strip("-")
        if token and token not in _GROUNDING_STOPWORDS:
            for suffix in _RU_INFLECTION_SUFFIXES:
                if len(token) - len(suffix) >= 4 and token.endswith(suffix):
                    token = token[: -len(suffix)]
                    break
            result.add(token)
    return result


def polarity_markers(value: str) -> frozenset[str]:
    normalized = (value or "").lower().replace("ё", "е")
    return frozenset(name for name, pattern in _POLARITY_PATTERNS if pattern.search(normalized))


def polarity_compatible(claim: str, evidence: str) -> bool:
    claim_markers = polarity_markers(claim)
    evidence_markers = polarity_markers(evidence)
    for negative, positive in (("NEGATED_COMPLETION", "COMPLETION"), ("NEGATED_CHANGE", "CHANGE")):
        if (negative in claim_markers) != (negative in evidence_markers):
            return False
        if (positive in claim_markers) != (positive in evidence_markers):
            return False
    return ("NEGATION" in claim_markers) == ("NEGATION" in evidence_markers)


def protected_number_tokens(value: str) -> set[str]:
    return {" ".join(match.group(0).lower().replace(",", ".").split()) for match in _PROTECTED_NUMBER_RE.finditer(value or "")}


def named_tokens(value: str) -> set[str]:
    return {
        token.lower()
        for token in re.findall(r"\b[А-ЯЁ][а-яё]{2,}\b", value or "")
        if token not in _NON_NAME_CAPITALIZED
    }


def claims_are_semantically_grounded(result: dict[str, Any], valid: dict[str, tuple[str, int, int, str, str, str, int]], assistant_mode: str) -> bool:
    """Validate evidence IDs, polarity, prose overlap and exact protected values."""
    if assistant_mode == "GENERAL_CHAT":
        return True
    if not claims_are_structurally_grounded(result, valid, assistant_mode):
        return False
    claims = result.get("claims") or []
    for claim in claims:
        evidence_text = " ".join(
            valid[str(item).removeprefix("SEG-")][3]
            for item in claim.get("evidenceIds", [])
            if str(item).removeprefix("SEG-") in valid
        ).lower()
        claim_text = str(claim.get("text", "")).lower()
        if not polarity_compatible(claim_text, evidence_text):
            return False
        claim_tokens = {token for token in grounding_tokens(claim_text) if not token.startswith("seg-")}
        if claim_tokens and not (claim_tokens & grounding_tokens(evidence_text)):
            return False
        claim_names = named_tokens(str(claim.get("text", "")))
        if any(not re.search(rf"(?<!\w){re.escape(name)}(?!\w)", evidence_text) for name in claim_names):
            return False
        if not protected_number_tokens(claim_text).issubset(protected_number_tokens(evidence_text)):
            return False
        if any(claim.get(name) is not None for name in ("subject", "predicate", "value", "polarity")):
            subject = str(claim.get("subject", "")).lower()
            predicate = str(claim.get("predicate", "")).lower()
            value = str(claim.get("value", "")).lower()
            polarity = str(claim.get("polarity", "UNKNOWN")).upper()
            if subject and not (grounding_tokens(subject) & grounding_tokens(evidence_text)):
                return False
            markers = _PREDICATE_MARKERS.get(predicate)
            if markers and not any(marker in evidence_text for marker in markers):
                return False
            if value and not all(token in evidence_text for token in grounding_tokens(value)):
                return False
            if polarity == "NEGATIVE" and "NEGATION" not in polarity_markers(evidence_text):
                return False
            if polarity == "POSITIVE" and "NEGATION" in polarity_markers(evidence_text):
                return False
            if predicate == "cause" and not any(marker in evidence_text for marker in _PREDICATE_MARKERS["cause"]):
                return False

    cited = " ".join(
        valid[str(item).removeprefix("SEG-")][3]
        for claim in claims for item in claim.get("evidenceIds", [])
        if str(item).removeprefix("SEG-") in valid
    ).lower()
    for value in (str(result.get("answer", "")), str(result.get("voice_answer", ""))):
        protected = set(re.findall(r"\b[\w-]*\d[\w-]*\b", value.lower()))
        protected.update(named_tokens(value))
        if not protected_number_tokens(value).issubset(protected_number_tokens(cited)):
            return False
        if any(not re.search(rf"(?<!\w){re.escape(token.lower())}(?!\w)", cited) for token in protected):
            return False
    return True

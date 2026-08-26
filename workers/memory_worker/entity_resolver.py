"""Deterministic, owner-scoped entity normalization."""

from __future__ import annotations

import re
from typing import Iterable

from .models import MemoryEntity, MemoryFact


_SPACE_RE = re.compile(r"\s+")
_PUNCT_RE = re.compile(r"[^0-9a-zа-яё№# -]+", re.IGNORECASE)
_EQUIPMENT_RE = re.compile(r"\b(?:печ\w*|насос\w*|привод\w*|станок\w*|агрегат\w*|линия\w*|двигател\w*|оборудован\w*)\b", re.IGNORECASE)
_PROJECT_RE = re.compile(r"\b(?:проект\w*|программ\w*|модернизац\w*|внедрен\w*)\b", re.IGNORECASE)
_LOCATION_RE = re.compile(r"\b(?:цех\w*|участ\w*|площад\w*|склад\w*|объект\w*|производств\w*)\b", re.IGNORECASE)

# Bump this only when the canonical key algorithm changes.  The value is
# stored in release metadata/backfill diagnostics so a future key migration
# cannot silently mix projections produced by different normalizers.
SUBJECT_NORMALIZER_VERSION = 1


def normalize_entity_name(value: str) -> str:
    value = _PUNCT_RE.sub(" ", str(value or "").lower().replace("ё", "е"))
    value = _SPACE_RE.sub(" ", value).strip(" -")
    return value[:240]


def canonical_topic_name(value: str) -> str:
    """Return a conservative comparison key, retaining the human title."""
    normalized = normalize_entity_name(value)
    replacements = {
        "печи": "печь",
        "печью": "печь",
        "ремонта": "ремонт",
        "насоса": "насос",
        "проекта": "проект",
        "привода": "привод",
    }
    tokens = [replacements.get(token, token) for token in normalized.split()]
    result = " ".join(tokens)
    result = re.sub(r"\bвтор(?:ой|ая|ую|ой)\s+печ\w*\b", "печь №2", result)
    result = re.sub(r"\b2\s*-?\s*я\s+печ\w*\b", "печь №2", result)
    result = re.sub(r"\bпеч\w*\s*№?\s*2\b", "печь №2", result)
    result = re.sub(r"\bпеч\w*\s+2\s*-?\s*я\b", "печь №2", result)
    return result.strip()


def _topic_variants(name: str) -> tuple[str, ...]:
    """Add conservative Russian case variants for retrieval aliases."""
    normalized = normalize_entity_name(name)
    variants = {normalized}
    variants.add(canonical_topic_name(normalized))
    if normalized.startswith("печь "):
        suffix = normalized.removeprefix("печь ").strip(" №")
        if suffix:
            variants.update({f"печь №{suffix}", f"{suffix} печь"})
    if normalized.startswith("вторая печь"):
        variants.update({"печь №2", "2 печь", "2-я печь"})
    return tuple(sorted(item for item in variants if item))


def _aliases(name: str) -> tuple[str, ...]:
    return _topic_variants(name)


def _entity_type(name: str, *, is_responsible: bool = False) -> str:
    if is_responsible:
        return "PERSON"
    if _EQUIPMENT_RE.search(name):
        return "EQUIPMENT"
    if _PROJECT_RE.search(name):
        return "PROJECT"
    if _LOCATION_RE.search(name):
        return "LOCATION"
    return "TOPIC"


def resolve_entities(facts: Iterable[MemoryFact]) -> tuple[MemoryEntity, ...]:
    """Return candidates without merging across owners.

    Only explicit responsible values and explicit fact subjects become entities;
    arbitrary prose is never treated as a topic.
    """
    by_key: dict[tuple[str, str], MemoryEntity] = {}
    for fact in facts:
        candidates: list[tuple[str, str]] = []
        if fact.fact_type == "RESPONSIBLE":
            candidates.append((fact.value.strip(), "PERSON"))
        if fact.subject:
            # Every explicit subject gets a stable TOPIC entity.  A second,
            # typed entity is also retained when the same phrase explicitly
            # names equipment/project/location; this lets retrieval scope by
            # topic while preserving the more specific classification.
            subject = fact.subject.strip()
            candidates.append((subject, "TOPIC"))
            specialized = _entity_type(subject, is_responsible=False)
            if specialized != "TOPIC":
                candidates.append((subject, specialized))
        for name, entity_type in candidates:
            normalized = canonical_topic_name(name) if entity_type == "TOPIC" else normalize_entity_name(name)
            if not normalized:
                continue
            key = (entity_type, normalized)
            by_key.setdefault(key, MemoryEntity(entity_type, name, normalized, _aliases(name)))
    return tuple(by_key.values())

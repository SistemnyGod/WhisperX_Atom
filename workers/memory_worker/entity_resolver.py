"""Deterministic, owner-scoped entity normalization."""

from __future__ import annotations

import re
from typing import Iterable

from .models import MemoryEntity, MemoryFact


_SPACE_RE = re.compile(r"\s+")
_PUNCT_RE = re.compile(r"[^0-9a-zа-яё№# -]+", re.IGNORECASE)


def normalize_entity_name(value: str) -> str:
    value = _PUNCT_RE.sub(" ", str(value or "").lower().replace("ё", "е"))
    value = _SPACE_RE.sub(" ", value).strip(" -")
    return value[:240]


def _aliases(name: str) -> tuple[str, ...]:
    normalized = normalize_entity_name(name)
    aliases = {normalized}
    if normalized.startswith("печь "):
        suffix = normalized.removeprefix("печь ").strip(" №")
        if suffix:
            aliases.update({f"печь №{suffix}", f"{suffix} печь"})
    if normalized.startswith("вторая печь"):
        aliases.update({"печь №2", "2 печь", "2-я печь"})
    return tuple(sorted(alias for alias in aliases if alias))


def resolve_entities(facts: Iterable[MemoryFact]) -> tuple[MemoryEntity, ...]:
    """Return candidates without merging across owners.

    Facts currently expose explicit responsible values. Subject/entity linking
    for richer topics can be added later from an explicit extraction field;
    this function never treats arbitrary prose as a canonical entity.
    """
    by_key: dict[tuple[str, str], MemoryEntity] = {}
    for fact in facts:
        if fact.fact_type != "RESPONSIBLE":
            continue
        name = fact.value.strip()
        normalized = normalize_entity_name(name)
        if not normalized:
            continue
        key = ("PERSON", normalized)
        by_key.setdefault(key, MemoryEntity("PERSON", name, normalized, _aliases(name)))
    return tuple(by_key.values())

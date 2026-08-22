"""Memory query planning and canonical evidence rehydration helpers."""

from __future__ import annotations

from dataclasses import dataclass
import re
from typing import Iterable

from .models import MemoryFact


@dataclass(frozen=True)
class MemoryQueryPlan:
    intent: str
    fact_types: tuple[str, ...]
    temporal_mode: str
    include_superseded: bool
    topic: str | None

    def to_dict(self) -> dict[str, object]:
        return {
            "intent": self.intent,
            "factTypes": list(self.fact_types),
            "temporalMode": self.temporal_mode,
            "includeSuperseded": self.include_superseded,
            "topic": self.topic,
        }


def build_memory_query_plan(intent: str, query: str, topic: str | None = None) -> MemoryQueryPlan:
    normalized = " ".join(re.sub(r"[^\wА-Яа-яЁё-]+", " ", query or "").lower().split())
    if intent == "TIMELINE" or any(word in normalized for word in ("как менялся", "история", "сначала", "потом")):
        fact_types = ("DEADLINE", "RESPONSIBLE", "DECISION", "STATUS", "TASK")
        temporal_mode, include_superseded = "HISTORY", True
    elif any(word in normalized for word in ("открыт", "невыполн", "остается", "осталось")):
        fact_types, temporal_mode, include_superseded = ("TASK", "STATUS",), "OPEN_ITEMS", False
    elif any(word in normalized for word in ("сейчас", "текущ", "кто отвечает")):
        fact_types, temporal_mode, include_superseded = ("RESPONSIBLE", "DEADLINE", "STATUS",), "CURRENT_STATE", False
    elif intent == "CHANGE_SUMMARY" or "изменил" in normalized:
        fact_types, temporal_mode, include_superseded = ("DEADLINE", "RESPONSIBLE", "STATUS", "TASK"), "CHANGES", True
    else:
        fact_types = {
            "DECISION": ("DECISION",),
            "RESPONSIBLE": ("RESPONSIBLE",),
            "DEADLINE": ("DEADLINE",),
            "TASK": ("TASK",),
            "CAUSE": ("CAUSE",),
            "STATUS": ("STATUS",),
            "FACT_LOOKUP": ("DECISION", "TASK", "RESPONSIBLE", "DEADLINE", "CAUSE", "STATUS"),
        }.get(intent, ("DECISION", "TASK", "RESPONSIBLE", "DEADLINE", "CAUSE", "STATUS"))
        temporal_mode, include_superseded = "FACT", False
    return MemoryQueryPlan(intent, tuple(dict.fromkeys(fact_types)), temporal_mode, include_superseded, topic)


def rehydrate_evidence(facts: Iterable[MemoryFact], allowed_segment_ids: set[str] | None = None) -> tuple[str, ...]:
    """Return only source segment IDs, never memory prose or assistant text."""
    allowed = allowed_segment_ids
    values: list[str] = []
    for fact in facts:
        if fact.state != "ACTIVE":
            continue
        for segment_id in fact.evidence_segment_ids:
            if allowed is None or segment_id in allowed:
                values.append(segment_id)
    return tuple(dict.fromkeys(values))

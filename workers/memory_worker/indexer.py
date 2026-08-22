"""Deterministic Meeting Memory v2 index builder.

This is intentionally independent of NATS and PostgreSQL. The runtime worker
can call it inside a transaction, while tests can exercise the same safety
rules without customer data or a model.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable, Mapping

from .entity_resolver import resolve_entities
from .models import MemoryEntity, MemoryFact, MemoryRelation, MemoryThread
from .relation_resolver import resolve_relations
from .thread_builder import build_threads


@dataclass(frozen=True)
class MemoryIndex:
    facts: tuple[MemoryFact, ...]
    entities: tuple[MemoryEntity, ...]
    relations: tuple[MemoryRelation, ...]
    threads: tuple[MemoryThread, ...]

    @property
    def evidence_safe(self) -> bool:
        return all(fact.evidence_segment_ids and fact.state == "ACTIVE" for fact in self.facts)

    def diagnostics(self) -> dict[str, int | bool]:
        return {
            "factCount": len(self.facts),
            "entityCount": len(self.entities),
            "relationCount": len(self.relations),
            "threadCount": len(self.threads),
            "evidenceSafe": self.evidence_safe,
        }


def build_memory_index(
    facts: Iterable[MemoryFact],
    *,
    meeting_order: Mapping[str, int] | None = None,
) -> MemoryIndex:
    values = tuple(
        fact for fact in facts
        if fact.state == "ACTIVE" and fact.evidence_segment_ids and fact.derivation_type in {"EXPLICIT", "DERIVED"}
    )
    entities = resolve_entities(values)
    relations = resolve_relations(values, meeting_order)
    threads = build_threads(values, relations)
    return MemoryIndex(values, entities, relations, threads)

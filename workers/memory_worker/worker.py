"""Memory indexing stages and retry-safe job contract.

The durable NATS/PostgreSQL adapter can invoke ``MemoryIndexWorker.index``;
the indexer itself never gates transcript V1/V2 or Summary completion.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable, Mapping

from .indexer import MemoryIndex, build_memory_index
from .models import MemoryFact


MEMORY_STAGES = (
    "QUEUED",
    "EXTRACTING_FACTS",
    "RESOLVING_ENTITIES",
    "LINKING_FACTS",
    "REBUILDING_THREADS",
    "READY",
    "NEEDS_REVIEW",
    "FAILED",
)


@dataclass(frozen=True)
class MemoryIndexResult:
    status: str
    stage: str
    index: MemoryIndex | None
    error_code: str | None = None


class MemoryIndexWorker:
    """Build an owner-scoped memory projection with fail-closed evidence."""

    def index(self, facts: Iterable[MemoryFact], meeting_order: Mapping[str, int] | None = None) -> MemoryIndexResult:
        try:
            values = tuple(facts)
            if any(fact.state == "ACTIVE" and not fact.evidence_segment_ids for fact in values):
                safe_values = tuple(fact for fact in values if fact.state != "ACTIVE" or fact.evidence_segment_ids)
                return MemoryIndexResult(
                    "NEEDS_REVIEW",
                    "NEEDS_REVIEW",
                    build_memory_index(safe_values, meeting_order=meeting_order),
                    "MEMORY_FACT_WITHOUT_EVIDENCE",
                )
            index = build_memory_index(values, meeting_order=meeting_order)
            if not index.evidence_safe:
                return MemoryIndexResult("NEEDS_REVIEW", "NEEDS_REVIEW", index, "MEMORY_FACT_WITHOUT_EVIDENCE")
            return MemoryIndexResult("READY", "READY", index)
        except Exception as exc:
            return MemoryIndexResult("FAILED", "FAILED", None, type(exc).__name__.upper())

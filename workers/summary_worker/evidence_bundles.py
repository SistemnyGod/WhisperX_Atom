"""Evidence bundles for the Assistant answer planner.

Bundles are a presentation/indexing layer over retrieved transcript segments.
They are never a source of truth: every candidate fact keeps the segment IDs
that support it and the final grounding validator still checks the original
segments.  Keeping this object separate from the database retrieval makes it
safe to evolve the prompt contract without changing scope/RBAC code.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Mapping

from .query_understanding import AssistantQueryPlan


@dataclass(frozen=True)
class EvidenceBundle:
    topic: str | None
    meeting_id: str | None
    evidence: tuple[dict[str, Any], ...]
    candidate_facts: dict[str, tuple[dict[str, Any], ...]]

    @property
    def segment_ids(self) -> tuple[str, ...]:
        return tuple(str(item["segmentId"]) for item in self.evidence)

    def to_dict(self) -> dict[str, Any]:
        return {
            "topic": self.topic,
            "meetingId": self.meeting_id,
            "evidence": [dict(item) for item in self.evidence],
            # Candidate facts deliberately carry evidence IDs.  Consumers
            # must validate those IDs against the original snapshot.
            "candidateFacts": {
                key: [dict(item) for item in values]
                for key, values in self.candidate_facts.items()
            },
        }


def _fact_markers(plan: AssistantQueryPlan) -> tuple[tuple[str, tuple[str, ...]], ...]:
    markers = {
        "responsible": ("ответствен", "отвеч", "исполн", "поруч", "назнач"),
        "deadline": ("срок", "до ", "дата", "числ", "законч", "заверш"),
        "decision": ("решили", "решение", "договорились", "утверд", "согласовал"),
        "task": ("поруч", "задач", "подготовить", "проверить", "сделать"),
        "cause": ("потому", "по причине", "из-за", "из за", "причин", "поэтому"),
        "status": ("статус", "останов", "продвига", "состояние"),
    }
    fields = plan.requested_fields or ("facts",)
    return tuple((field, markers.get(field, (field,))) for field in fields)


def build_evidence_bundles(
    valid: Mapping[str, tuple[Any, ...]],
    plan: AssistantQueryPlan,
    *,
    max_bundles: int = 5,
    max_evidence_per_bundle: int = 12,
) -> tuple[EvidenceBundle, ...]:
    """Group already scope-filtered evidence by meeting and topic.

    ``valid`` is the exact retrieval snapshot used by the worker.  This
    function does not query the database, expand scope, or select a winning
    conflicting value.  It only prepares compact, source-aware prompt data.
    """
    grouped: dict[tuple[str | None, str | None], list[dict[str, Any]]] = {}
    for segment_id, value in valid.items():
        if len(value) < 7:
            continue
        meeting_id, start_ms, end_ms, text, kind, transcript_id, version = value[:7]
        item = {
            "segmentId": str(segment_id),
            "meetingId": str(meeting_id) if meeting_id is not None else None,
            "startMs": int(start_ms),
            "endMs": int(end_ms),
            "text": str(text).strip(),
            "transcriptKind": str(kind),
            "transcriptId": str(transcript_id) if transcript_id else None,
            "transcriptVersion": int(version),
        }
        key = (item["meetingId"], plan.topic)
        grouped.setdefault(key, []).append(item)

    bundles: list[EvidenceBundle] = []
    for (meeting_id, topic), items in list(grouped.items())[: max(1, int(max_bundles))]:
        evidence = tuple(items[: max(1, int(max_evidence_per_bundle))])
        candidate_facts: dict[str, tuple[dict[str, Any], ...]] = {}
        for field, markers in _fact_markers(plan):
            candidates = []
            for item in evidence:
                lowered = item["text"].lower()
                if any(marker in lowered for marker in markers):
                    candidates.append({
                        "value": item["text"],
                        "evidenceSegmentIds": [item["segmentId"]],
                        "startMs": item["startMs"],
                        "endMs": item["endMs"],
                    })
            if candidates:
                candidate_facts[field] = tuple(candidates[:8])
        bundles.append(EvidenceBundle(topic=topic, meeting_id=meeting_id, evidence=evidence, candidate_facts=candidate_facts))
    return tuple(bundles)


def bundles_prompt(bundles: tuple[EvidenceBundle, ...] | list[EvidenceBundle]) -> str:
    """Serialize bundles compactly for a model prompt without hiding IDs."""
    lines: list[str] = []
    for index, bundle in enumerate(bundles, start=1):
        lines.append(f"Bundle {index}: meeting={bundle.meeting_id} topic={bundle.topic or 'unknown'}")
        for item in bundle.evidence:
            lines.append(f"[SEG-{item['segmentId']} {item['startMs']//1000}s] {item['text']}")
        if bundle.candidate_facts:
            lines.append(f"candidateFacts={bundle.to_dict()['candidateFacts']}")
    return "\n".join(lines)


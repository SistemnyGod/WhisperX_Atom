"""Evidence-only summary fallback used while the optional LLM is unavailable.

This module deliberately does not infer topics, people, dates or decisions.
It only exposes transcript text that is already tied to canonical segment IDs.
The normal Qwen summary can safely replace this projection on a later retry.
"""

from __future__ import annotations

from typing import Any, Iterable

from .contracts import SUMMARY_SCHEMA_VERSION
from .fact_extraction import DerivedFact, extract_transcript_facts
from .summarizer import TranscriptSegment, transcript_source_hash


DETERMINISTIC_SUMMARY_MODEL = "deterministic-v2"
LEGACY_DETERMINISTIC_SUMMARY_MODEL = "deterministic-v1"


def _clean(text: str, limit: int = 280) -> str:
    value = " ".join(str(text or "").split())
    return value[:limit].rstrip() + ("…" if len(value) > limit else "")


def _item(text: str, segment_id: str) -> dict[str, Any]:
    return {
        "text": _clean(text),
        "evidence_segment_ids": [segment_id],
        "needs_review": True,
        "validation": {
            "evidence": True,
            "needs_review": True,
            "review_reasons": ["LLM_ENHANCEMENT_PENDING"],
        },
    }


def _fact_item(text: str, evidence_segment_ids: Iterable[str]) -> dict[str, Any]:
    ids = [str(value) for value in evidence_segment_ids if str(value)]
    item = _item(text, ids[0] if ids else "")
    item["evidence_segment_ids"] = ids
    return item


def build_deterministic_summary(
    segments: Iterable[TranscriptSegment],
    profile: str | None = None,
    facts: Iterable[DerivedFact] | None = None,
) -> dict[str, Any]:
    """Build a conservative, schema-compatible summary from canonical text.

    Marker labels are treated only as user annotations. They select which
    already-confirmed segment text is shown as a decision or action item; they
    never create a responsible person, deadline, topic, or inferred fact.
    """

    material = [segment for segment in segments if str(segment.text).strip()]
    if not material:
        raise ValueError("transcript_has_no_segments")

    segment_by_id = {str(segment.id): segment for segment in material}
    # Facts are a derived index only. Rehydrate every evidence id against the
    # canonical segment set before it can enter the fallback projection.  The
    # value/subject from a caller-provided index is never trusted directly:
    # matching canonical extraction supplies the value again from segment text.
    canonical_facts = extract_transcript_facts(
        [{"id": segment.id, "startMs": segment.start_ms, "endMs": segment.end_ms, "text": segment.text, "speakerId": segment.speaker}
         for segment in material],
        meeting_id="",
        transcript_id="",
        transcript_version=0,
        minimum_confidence=0.70,
    )
    provided_facts = list(facts or ())
    derived_facts = canonical_facts if not provided_facts else provided_facts
    valid_facts: list[DerivedFact] = []
    for fact in derived_facts:
        evidence = tuple(str(item) for item in fact.evidence_segment_ids if str(item) in segment_by_id)
        if not evidence or fact.state != "ACTIVE":
            continue
        canonical = next((candidate for candidate in canonical_facts
                          if candidate.fact_type == fact.fact_type
                          and tuple(str(item) for item in candidate.evidence_segment_ids) == evidence), None)
        if canonical is None:
            continue
        valid_facts.append(canonical)

    decisions: list[dict[str, Any]] = []
    action_items: list[dict[str, Any]] = []
    open_questions: list[dict[str, Any]] = []
    notable_facts: list[dict[str, Any]] = []
    for segment in material:
        markers = {str(marker).upper() for marker in segment.markers}
        if any(marker.startswith("DECISION") for marker in markers):
            decisions.append({
                "subject": "",
                "decision": _clean(segment.text, 1200),
                "evidence_segment_ids": [segment.id],
                "validation": {
                    "evidence": True,
                    "needs_review": True,
                    "review_reasons": ["LLM_ENHANCEMENT_PENDING"],
                },
            })
        elif any(marker.startswith("ACTION_ITEM") for marker in markers):
            action = {
                "task": _clean(segment.text, 1200),
                "responsible": None,
                "deadline_text": None,
                "deadline_iso": None,
                "evidence_segment_ids": [segment.id],
                "validation": {
                    "evidence": True,
                    "responsible": False,
                    "deadline": False,
                    "needs_review": True,
                    "review_reasons": ["LLM_ENHANCEMENT_PENDING"],
                },
            }
            action_items.append(action)
        elif str(segment.text).rstrip().endswith(("?", "？")):
            open_questions.append(_item(segment.text, segment.id))
        else:
            notable_facts.append(_item(segment.text, segment.id))

    # Add only explicit fact types. Values, names and dates remain exactly as
    # present in the rehydrated canonical evidence; missing fields stay null.
    fact_decisions = [fact for fact in valid_facts if fact.fact_type == "DECISION"]
    fact_tasks = [fact for fact in valid_facts if fact.fact_type == "TASK"]
    fact_statuses = [fact for fact in valid_facts if fact.fact_type == "STATUS"]
    fact_responsibles = {fact.subject or "": fact.value for fact in valid_facts if fact.fact_type == "RESPONSIBLE"}
    fact_deadlines = {fact.subject or "": fact.value for fact in valid_facts if fact.fact_type == "DEADLINE"}
    for fact in fact_decisions:
        decisions.append({
            "subject": fact.subject or "",
            "decision": _clean(segment_by_id[fact.evidence_segment_ids[0]].text, 1200),
            "evidence_segment_ids": list(fact.evidence_segment_ids),
            "validation": {"evidence": True, "needs_review": True, "review_reasons": ["LLM_ENHANCEMENT_PENDING"]},
        })
    for fact in fact_tasks:
        evidence_id = str(fact.evidence_segment_ids[0])
        subject = fact.subject or ""
        action_items.append({
            "task": _clean(segment_by_id[evidence_id].text, 1200),
            "responsible": fact_responsibles.get(subject),
            "deadline_text": fact_deadlines.get(subject),
            "deadline_iso": None,
            "evidence_segment_ids": list(fact.evidence_segment_ids),
            "validation": {"evidence": True, "responsible": subject in fact_responsibles, "deadline": subject in fact_deadlines, "needs_review": True, "review_reasons": ["LLM_ENHANCEMENT_PENDING"]},
        })
    # STATUS is a reportable fact, not an action item.  Keep it in the
    # evidence-only facts collection so a deterministic draft never presents
    # a state observation as a task for somebody to execute.
    existing_notable_evidence = {
        tuple(str(value) for value in item.get("evidence_segment_ids", ()))
        for item in notable_facts
    }
    for fact in fact_statuses:
        if not fact.evidence_segment_ids or str(fact.evidence_segment_ids[0]) not in segment_by_id:
            continue
        evidence = tuple(str(value) for value in fact.evidence_segment_ids)
        # The segment loop above already exposes unmarked prose as a
        # notable fact. Replace that view with the fact-backed representation
        # once, rather than showing the same canonical segment twice.
        if evidence in existing_notable_evidence:
            notable_facts = [
                item for item in notable_facts
                if tuple(str(value) for value in item.get("evidence_segment_ids", ())) != evidence
            ]
        notable_facts.append(_fact_item(segment_by_id[evidence[0]].text, evidence))
        existing_notable_evidence.add(evidence)

    # Keep the visible draft short and deterministic. Every sentence is copied
    # from a canonical segment; no connective claim is generated.
    overview = " ".join(_clean(segment.text, 420) for segment in material[:3])
    review_count = len(decisions) + len(action_items) + len(open_questions) + len(notable_facts)
    if str(profile or "").upper() == "MEETING_PROTOCOL_RU":
        questions_and_decisions = [
            {
                "topic": item["subject"],
                "context": item["decision"],
                "decision": item["decision"],
                "evidence_segment_ids": item["evidence_segment_ids"],
                "validation": {
                    "evidence": True,
                    "deadline": False,
                    "needs_review": True,
                    "review_reasons": ["LLM_ENHANCEMENT_PENDING"],
                },
            }
            for item in decisions[:30]
        ]
        tasks = [
            {
                "task": item["task"],
                "deadline_text": None,
                "deadline_iso": None,
                "evidence_segment_ids": item["evidence_segment_ids"],
                "validation": {
                    "evidence": True,
                    "deadline": False,
                    "needs_review": True,
                    "review_reasons": ["LLM_ENHANCEMENT_PENDING"],
                },
            }
            for item in action_items[:40]
        ]
        return {
            "questions_and_decisions": questions_and_decisions,
            "tasks": tasks,
            "open_questions": open_questions[:20],
            "notable_facts": notable_facts[:40],
            "quality": {
                "status": "NEEDS_REVIEW",
                "score": 0.0,
                "review_items": review_count,
                "rejected_items": 0,
                "reasons": ["LLM_ENHANCEMENT_PENDING", "DETERMINISTIC_DRAFT"],
            },
            "validation": {
                "evidence_checked": True,
                "checked_items": review_count,
                "needs_review_items": review_count,
                "review_items": review_count,
                "unsupported_claims": 0,
                "review_reasons": ["LLM_ENHANCEMENT_PENDING", "DETERMINISTIC_DRAFT"],
            },
            "quality_score": 0.0,
            "source_hash": transcript_source_hash(material),
            "block_count": 1,
            "schema_version": "meeting-protocol-ru-v1",
            "prompt_version": DETERMINISTIC_SUMMARY_MODEL,
            "profile": "MEETING_PROTOCOL_RU",
            "contentValidity": "NEEDS_REVIEW",
            "generationState": "READY_WITH_WARNINGS",
            "errorCode": "LLM_ENHANCEMENT_PENDING",
            "fallbackReason": "LLM_UNAVAILABLE",
        }
    return {
        "overview": overview[:4000],
        "summary": overview[:4000],
        "topics": [],
        "decisions": decisions[:30],
        "action_items": action_items[:40],
        "risks": [],
        "open_questions": open_questions[:20],
        "notable_facts": notable_facts[:40],
        "validation": {
            "evidence_checked": True,
            "checked_items": review_count,
            "needs_review_items": review_count,
            "review_items": review_count,
            "unsupported_claims": 0,
            "review_reasons": ["LLM_ENHANCEMENT_PENDING", "DETERMINISTIC_DRAFT"],
        },
        "quality_score": 0.0,
        "source_hash": transcript_source_hash(material),
        "block_count": 1,
        "schema_version": SUMMARY_SCHEMA_VERSION,
        "prompt_version": DETERMINISTIC_SUMMARY_MODEL,
        "contentValidity": "NEEDS_REVIEW",
        "generationState": "READY_WITH_WARNINGS",
        "errorCode": "LLM_ENHANCEMENT_PENDING",
        "fallbackReason": "LLM_UNAVAILABLE",
    }

"""Conservative derived transcript-fact extraction.

The output is an index over a canonical transcript, not a replacement for the
transcript.  Every fact carries transcript/version and evidence segment IDs;
callers must revalidate those IDs before using a fact in an answer.
"""

from __future__ import annotations

from dataclasses import dataclass
import re
from typing import Any, Iterable, Mapping


_DATE = re.compile(r"\b(?:\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?|\d{1,2}\s+(?:январ\w*|феврал\w*|март\w*|апрел\w*|ма[яй]\w*|июн\w*|июл\w*|август\w*|сентябр\w*|октябр\w*|ноябр\w*|декабр\w*))\b", re.IGNORECASE)
_PERSON = re.compile(r"\b([А-ЯЁ][а-яё]{2,}(?:\s+[А-ЯЁ][а-яё]{2,})?)\b")
_NUMBER = re.compile(r"\b\d+(?:[.,]\d+)?\s*(?:руб\w*|кг|кВт|м|дн\w*|час\w*)?\b", re.IGNORECASE)
_SUBJECT_MARKER = re.compile(
    r"(?:ответствен\w*\s+(?:за|по)|срок(?:\s+(?:по|для))?|"
    r"задач\w*\s+(?:по|для)|по\s+|для\s+|проект\w*\s+)(.+?)"
    r"(?=\s+(?:назначен\w*|отвечает\w*|исполнитель\w*|перенесли\w*|"
    r"перенесен\w*|перенесён\w*|до\b|к\b|решили\b|договорились\b|"
    r"утвердили\b|согласовали\b|поручили\b|назначили\b)|[,:—–-]|$)",
    re.IGNORECASE,
)
_SUBJECT_LEADING = re.compile(r"^(?:по|для|за|о|об)\s+", re.IGNORECASE)
_SUBJECT_STOP = re.compile(r"\s+(?:назначен\w*|отвечает\w*|перенесли\w*|до\b|решили\b|договорились\b)$", re.IGNORECASE)


@dataclass(frozen=True)
class DerivedFact:
    fact_type: str
    subject: str | None
    predicate: str
    value: str
    meeting_id: str
    transcript_id: str
    transcript_version: int
    speaker_id: str | None
    start_ms: int
    end_ms: int
    confidence: float
    evidence_segment_ids: tuple[str, ...]
    state: str = "ACTIVE"
    source_text: str = ""

    def to_dict(self) -> dict[str, Any]:
        return {
            "factType": self.fact_type,
            "subject": self.subject,
            "predicate": self.predicate,
            "value": self.value,
            "meetingId": self.meeting_id,
            "transcriptId": self.transcript_id,
            "transcriptVersion": self.transcript_version,
            "speakerId": self.speaker_id,
            "startMs": self.start_ms,
            "endMs": self.end_ms,
            "confidence": self.confidence,
            "evidenceSegmentIds": list(self.evidence_segment_ids),
            "state": self.state,
        }


def _as_segment(item: Any) -> tuple[str, int, int, str, str | None]:
    if isinstance(item, Mapping):
        return (str(item.get("id") or item.get("segmentId")), int(item.get("startMs", item.get("start_ms", 0))), int(item.get("endMs", item.get("end_ms", 0))), str(item.get("text") or "").strip(), str(item.get("speakerId") or item.get("speaker_id")) if item.get("speakerId", item.get("speaker_id")) else None)
    values = tuple(item)
    if len(values) >= 5:
        return str(values[0]), int(values[1]), int(values[2]), str(values[3]).strip(), str(values[4]) if values[4] else None
    raise ValueError("segment_requires_id_start_end_text")


def _extract_subject(text: str) -> str | None:
    """Extract only an explicit topic phrase; ambiguous prose stays unscoped."""
    match = _SUBJECT_MARKER.search(text)
    if not match:
        return None
    subject = _SUBJECT_LEADING.sub("", match.group(1).strip(" \t,.;:—–-"))
    subject = _SUBJECT_STOP.sub("", subject).strip(" \t,.;:—–-")
    if len(subject) < 3 or len(subject.split()) > 12:
        return None
    if not any(char.isalpha() for char in subject):
        return None
    return subject


def extract_transcript_facts(
    segments: Iterable[Any],
    *,
    meeting_id: str,
    transcript_id: str,
    transcript_version: int,
    minimum_confidence: float = 0.70,
) -> list[DerivedFact]:
    """Extract only explicit marker-based facts; never infer cross-segment causes."""
    facts: list[DerivedFact] = []
    for raw in segments:
        segment_id, start_ms, end_ms, text, speaker_id = _as_segment(raw)
        if not text:
            continue
        lowered = text.lower()
        candidates: list[tuple[str, str, str, float, str | None]] = []
        if any(token in lowered for token in ("ответствен", "отвечает", "исполнитель", "назначен")):
            people = [
                value for value in _PERSON.findall(text)
                if not any(value.lower().startswith(prefix) for prefix in ("ответствен", "исполн", "назнач", "срок", "решен", "поруч", "задач"))
            ]
            if people:
                candidates.append(("RESPONSIBLE", "responsible", people[0], 0.86, _extract_subject(text)))
        if any(token in lowered for token in ("срок", "до ", "законч", "заверш", "дата")):
            value = (_DATE.search(text) or _NUMBER.search(text))
            if value:
                candidates.append(("DEADLINE", "deadline", value.group(0), 0.82, _extract_subject(text)))
        if any(token in lowered for token in ("решили", "договорились", "утвердили", "согласовали")):
            candidates.append(("DECISION", "decision", text, 0.80, _extract_subject(text)))
        if any(token in lowered for token in ("поручили", "задача", "подготовить", "проверить", "сделать")):
            candidates.append(("TASK", "task", text, 0.78, _extract_subject(text)))
        if any(token in lowered for token in ("потому что", "по причине", "из-за", "из за", "поэтому")):
            candidates.append(("CAUSE", "cause", text, 0.78, _extract_subject(text)))
        if any(token in lowered for token in ("статус", "остановились", "продвигается")):
            candidates.append(("STATUS", "status", text, 0.75, _extract_subject(text)))
        for fact_type, predicate, value, confidence, subject in candidates:
            if confidence < minimum_confidence:
                continue
            facts.append(DerivedFact(fact_type, subject, predicate, value[:1000], meeting_id, transcript_id, int(transcript_version), speaker_id, start_ms, end_ms, confidence, (segment_id,), "ACTIVE", text[:2000]))
    return facts


def fact_insert_params(fact: DerivedFact) -> tuple[Any, ...]:
    """Return values matching ``transcript_facts``; IDs/evidence stay explicit."""
    return (fact.meeting_id, fact.transcript_id, fact.transcript_version, fact.fact_type, fact.subject, fact.predicate, fact.value, fact.speaker_id, fact.start_ms, fact.end_ms, fact.confidence, list(fact.evidence_segment_ids), fact.state, fact.source_text)

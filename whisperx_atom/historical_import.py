"""Privacy-safe historical transcript discovery and import payloads.

The importer deliberately keeps source files local.  Preview artifacts contain
only hashes, counts and decisions; transcript text is returned only in the
in-memory APPLY payload sent to the authenticated API.
"""

from __future__ import annotations

import hashlib
import re
import unicodedata
import zipfile
from dataclasses import dataclass, field
from datetime import date, datetime
from difflib import SequenceMatcher
from pathlib import Path
from typing import Iterable
from xml.etree import ElementTree


TIMESTAMP = re.compile(r"^\s*\[(?P<h>\d{1,2}):(?P<m>\d{2}):(?P<s>\d{2})(?:[\.,](?P<ms>\d{1,3}))?\]\s*(?P<text>.*)$")
DATE_DMY = re.compile(r"(?<!\d)(?P<value>\d{2}\.\d{2}\.\d{4})(?!\d)")
DATE_COMPACT = re.compile(r"(?<!\d)(?P<date>\d{8})[_-](?P<time>\d{6})(?!\d)")
SPEAKER = re.compile(r"^(?P<speaker>[\wА-Яа-яЁё][\wА-Яа-яЁё .-]{0,80}):\s+(?P<text>.+)$")
QUARANTINE_NAME = re.compile(r"(?:саммари|summary|тест|test|пример|sample|черновик|unknown|неопредел)", re.I)


@dataclass(frozen=True)
class HistoricalSegment:
    ordinal: int
    start_ms: int
    end_ms: int
    text: str
    speaker: str | None = None


@dataclass(frozen=True)
class HistoricalDocument:
    path: Path
    source_format: str
    source_sha256: str
    canonical_content_sha256: str
    title: str
    meeting_date: date | datetime | None
    date_precision: str
    timing_quality: str
    segments: tuple[HistoricalSegment, ...]
    word_count: int
    decision: str = "IMPORT"
    quarantine_reason: str | None = None
    dedup_group: str | None = None
    warnings: tuple[str, ...] = field(default_factory=tuple)

    @property
    def duration_ms(self) -> int:
        return max((segment.end_ms for segment in self.segments), default=0)

    def preview(self) -> dict[str, object]:
        """Return a report-safe object.  It intentionally has no text field."""
        value = self.meeting_date.isoformat() if self.meeting_date else None
        return {
            "sourceName": self.path.name,
            "sourceFormat": self.source_format,
            "sourceSha256": self.source_sha256,
            "canonicalContentSha256": self.canonical_content_sha256,
            "meetingDate": value,
            "datePrecision": self.date_precision,
            "timingQuality": self.timing_quality,
            "segmentCount": len(self.segments),
            "wordCount": self.word_count,
            "durationMs": self.duration_ms,
            "decision": self.decision,
            "quarantineReason": self.quarantine_reason,
            "dedupGroup": self.dedup_group,
            "warnings": list(self.warnings),
        }

    def api_payload(self, mode: str = "APPLY") -> dict[str, object]:
        """Build the additive API contract without sending a local path."""
        meeting_date = self.meeting_date.isoformat() if self.meeting_date else None
        return {
            "mode": mode,
            "sourceName": self.path.name,
            "sourceFormat": self.source_format,
            "sourceSha256": self.source_sha256,
            "canonicalContentSha256": self.canonical_content_sha256,
            "title": self.title,
            "meetingDate": meeting_date,
            "datePrecision": self.date_precision,
            "parserVersion": "historical-import-v1",
            "timingQuality": self.timing_quality,
            "wordCount": self.word_count,
            "segments": [
                {
                    "ordinal": segment.ordinal,
                    "startMs": segment.start_ms,
                    "endMs": segment.end_ms,
                    "speaker": segment.speaker,
                    "text": segment.text,
                }
                for segment in self.segments
            ],
        }


def sha256_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(chunk_size), b""):
            digest.update(chunk)
    return digest.hexdigest()


def normalize_content(value: str) -> str:
    """Normalize only formatting for exact duplicate detection."""
    value = unicodedata.normalize("NFKC", value).replace("ё", "е").replace("Ё", "Е")
    value = re.sub(r"\[\d{1,2}:\d{2}:\d{2}(?:[\.,]\d{1,3})?\]", " ", value)
    value = value.casefold()
    return "".join(ch for ch in value if ch.isalnum() or ch.isspace()).strip()


def _parse_date(name: str) -> tuple[date | datetime | None, str]:
    compact = DATE_COMPACT.search(name)
    if compact:
        try:
            return datetime.strptime(compact.group("date") + compact.group("time"), "%Y%m%d%H%M%S"), "SECOND"
        except ValueError:
            pass
    dmy = DATE_DMY.search(name)
    if dmy:
        try:
            return datetime.strptime(dmy.group("value"), "%d.%m.%Y").date(), "DAY"
        except ValueError:
            pass
    return None, "UNKNOWN"


def _timestamp_ms(match: re.Match[str]) -> int:
    milliseconds = (match.group("ms") or "0").ljust(3, "0")[:3]
    return ((int(match.group("h")) * 60 + int(match.group("m"))) * 60 + int(match.group("s"))) * 1000 + int(milliseconds)


def _split_timestamped(lines: Iterable[str]) -> tuple[tuple[HistoricalSegment, ...], str]:
    parsed: list[tuple[int, str, str | None]] = []
    current: tuple[int, str, str | None] | None = None
    for raw in lines:
        line = raw.strip()
        if not line:
            continue
        match = TIMESTAMP.match(line)
        if match:
            if current:
                parsed.append(current)
            text = match.group("text").strip()
            speaker_match = SPEAKER.match(text)
            current = (_timestamp_ms(match), speaker_match.group("text").strip() if speaker_match else text, speaker_match.group("speaker").strip() if speaker_match else None)
        elif current:
            start, text, speaker = current
            current = (start, f"{text} {line}".strip(), speaker)
    if current:
        parsed.append(current)
    segments = tuple(
        HistoricalSegment(index, start, parsed[index][0] if index + 1 == len(parsed) else parsed[index + 1][0], text, speaker)
        for index, (start, text, speaker) in enumerate(parsed)
    )
    return segments, "EXACT" if segments else "ABSENT"


def _plain_segments(lines: Iterable[str]) -> tuple[HistoricalSegment, ...]:
    return tuple(
        HistoricalSegment(index, 0, 0, line.strip(), None)
        for index, line in enumerate(lines)
        if line.strip()
    )


def _read_docx(path: Path) -> list[str]:
    with zipfile.ZipFile(path) as archive:
        xml = archive.read("word/document.xml")
    root = ElementTree.fromstring(xml)
    namespace = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
    paragraphs: list[str] = []
    for paragraph in root.iter(namespace + "p"):
        text = "".join(node.text or "" for node in paragraph.iter(namespace + "t")).strip()
        if text:
            paragraphs.append(text)
    return paragraphs


def parse_document(path: Path) -> HistoricalDocument:
    source_format = path.suffix.lower().lstrip(".")
    source_sha = sha256_file(path)
    meeting_date, date_precision = _parse_date(path.name)
    warnings: list[str] = []
    try:
        if source_format == "txt":
            raw = path.read_text(encoding="utf-8-sig", errors="replace")
            lines = raw.splitlines()
            segments, timing_quality = _split_timestamped(lines)
            if not segments:
                segments = _plain_segments(lines)
                timing_quality = "ABSENT"
        elif source_format == "docx":
            lines = _read_docx(path)
            segments, timing_quality = _split_timestamped(lines)
            if not segments:
                segments = _plain_segments(lines)
                timing_quality = "ABSENT"
        else:
            raise ValueError("UNSUPPORTED_FORMAT")
    except (OSError, UnicodeError, zipfile.BadZipFile, ElementTree.ParseError, KeyError) as exc:
        return HistoricalDocument(path, source_format, source_sha, "", path.stem, meeting_date, date_precision, "UNKNOWN", (), 0, "QUARANTINE", type(exc).__name__, warnings=())

    text = "\n".join(segment.text for segment in segments)
    canonical_sha = hashlib.sha256(normalize_content(text).encode("utf-8")).hexdigest()
    decision = "IMPORT"
    reason: str | None = None
    if source_format not in {"txt", "docx"}:
        decision, reason = "QUARANTINE", "UNSUPPORTED_FORMAT"
    elif meeting_date is None:
        decision, reason = "QUARANTINE", "DATE_UNRESOLVED"
    elif not segments:
        decision, reason = "QUARANTINE", "EMPTY_DOCUMENT"
    elif QUARANTINE_NAME.search(path.stem):
        decision, reason = "QUARANTINE", "NON_TRANSCRIPT_FILENAME"
    elif timing_quality == "ABSENT":
        warnings.append("TIMING_ABSENT")
    return HistoricalDocument(path, source_format, source_sha, canonical_sha, path.stem, meeting_date, date_precision, timing_quality, segments, len(normalize_content(text).split()), decision, reason, warnings=tuple(warnings))


def classify_duplicates(documents: Iterable[HistoricalDocument], similar_threshold: float = 0.80) -> list[HistoricalDocument]:
    """Classify exact duplicates automatically and similar same-date files for review."""
    result: list[HistoricalDocument] = []
    exact: dict[str, HistoricalDocument] = {}
    imported = [doc for doc in documents if doc.decision == "IMPORT"]
    for doc in documents:
        if doc.decision != "IMPORT":
            result.append(doc)
            continue
        duplicate = exact.get(doc.canonical_content_sha256)
        if duplicate:
            result.append(HistoricalDocument(**{**doc.__dict__, "decision": "EXCLUDE_DUPLICATE", "dedup_group": duplicate.canonical_content_sha256[:12], "quarantine_reason": "IDENTICAL_CANONICAL_CONTENT"}))
            continue
        exact[doc.canonical_content_sha256] = doc
        similar = next((candidate for candidate in imported if candidate is not doc and candidate.meeting_date == doc.meeting_date and candidate.canonical_content_sha256 != doc.canonical_content_sha256 and SequenceMatcher(None, normalize_content(" ".join(x.text for x in candidate.segments)), normalize_content(" ".join(x.text for x in doc.segments))).ratio() >= similar_threshold), None)
        if similar:
            result.append(HistoricalDocument(**{**doc.__dict__, "decision": "REVIEW_REQUIRED", "dedup_group": similar.canonical_content_sha256[:12], "quarantine_reason": "SIMILAR_SAME_DATE"}))
        else:
            result.append(doc)
    return result


def discover(directory: Path) -> list[HistoricalDocument]:
    docs = [parse_document(path) for path in sorted(directory.iterdir()) if path.is_file() and path.suffix.lower() in {".txt", ".docx"}]
    return classify_duplicates(docs)

from __future__ import annotations

import os
import re
from dataclasses import asdict, dataclass, field
from typing import Any


def _float_env(name: str, default: float) -> float:
    try:
        return float(os.getenv(name, str(default)))
    except (TypeError, ValueError):
        return default


def _int_env(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except (TypeError, ValueError):
        return default


@dataclass(frozen=True)
class TranscriptQualityThresholds:
    min_words: int = 3
    min_coverage: float = 0.55
    max_trailing_gap_seconds: float = 120.0
    max_internal_gap_seconds: float = 180.0
    low_confidence_threshold: float = 0.45
    max_low_confidence_ratio: float = 0.35
    repetition_threshold: float = 0.35
    timestamp_tolerance_seconds: float = 2.0
    fallback_enabled: bool = True
    fallback_vad_onset: float = 0.25
    fallback_chunk_size: int = 30

    @classmethod
    def from_env(cls) -> "TranscriptQualityThresholds":
        return cls(
            min_words=max(0, _int_env("TRANSCRIPT_MIN_WORDS", 3)),
            min_coverage=max(0.0, min(1.0, _float_env("TRANSCRIPT_MIN_COVERAGE", 0.55))),
            max_trailing_gap_seconds=max(0.0, _float_env("TRANSCRIPT_MAX_TRAILING_GAP_SECONDS", 120.0)),
            max_internal_gap_seconds=max(0.0, _float_env("TRANSCRIPT_MAX_INTERNAL_GAP_SECONDS", 180.0)),
            low_confidence_threshold=max(0.0, min(1.0, _float_env("TRANSCRIPT_LOW_CONFIDENCE_THRESHOLD", 0.45))),
            max_low_confidence_ratio=max(0.0, min(1.0, _float_env("TRANSCRIPT_MAX_LOW_CONFIDENCE_RATIO", 0.35))),
            repetition_threshold=max(0.0, min(1.0, _float_env("TRANSCRIPT_REPETITION_THRESHOLD", 0.35))),
            timestamp_tolerance_seconds=max(0.0, _float_env("TRANSCRIPT_TIMESTAMP_TOLERANCE_SECONDS", 2.0)),
            fallback_enabled=os.getenv("TRANSCRIPT_FALLBACK_ENABLED", "true").strip().lower() in {"1", "true", "yes", "on"},
            fallback_vad_onset=max(0.0, _float_env("TRANSCRIPT_FALLBACK_VAD_ONSET", 0.25)),
            fallback_chunk_size=max(1, _int_env("TRANSCRIPT_FALLBACK_CHUNK_SIZE", 30)),
        )

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class TranscriptQualityReport:
    duration_ms: int
    segment_count: int
    word_count: int
    text_word_count: int
    aligned_word_count: int
    first_speech_ms: int | None
    last_speech_ms: int | None
    leading_gap_ms: int
    trailing_gap_ms: int
    largest_internal_gap_ms: int
    speech_coverage_ratio: float
    transcript_span_ratio: float
    empty_segment_count: int
    invalid_timestamp_count: int
    non_monotonic_segment_count: int
    average_word_confidence: float | None
    low_confidence_word_ratio: float
    repetition_score: float
    has_text: bool
    has_words: bool
    quality_score: float
    reasons: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def _text(segment: dict[str, Any]) -> str:
    return " ".join(str(segment.get("text", "") or "").split()).strip()


def _text_word_count(texts: list[str]) -> int:
    """Count words present in segment text, independent of alignment output."""
    return sum(len(re.findall(r"[\wА-Яа-яЁё-]+", text, flags=re.UNICODE)) for text in texts)


def _words(result: dict[str, Any]) -> list[dict[str, Any]]:
    words = result.get("word_segments") or []
    if words:
        return [word for word in words if isinstance(word, dict)]
    collected: list[dict[str, Any]] = []
    for segment in result.get("segments", []) or []:
        for word in segment.get("words", []) or []:
            if isinstance(word, dict):
                collected.append(word)
    return collected


def _confidence(word: dict[str, Any]) -> float | None:
    for key in ("score", "confidence", "probability", "prob"):
        value = word.get(key)
        try:
            if value is not None:
                return max(0.0, min(1.0, float(value)))
        except (TypeError, ValueError):
            continue
    return None


def _repetition_score(texts: list[str]) -> float:
    normalized = [re.sub(r"\W+", " ", text.lower(), flags=re.UNICODE).strip() for text in texts if text]
    if not normalized:
        return 0.0
    duplicate_ratio = 1.0 - len(set(normalized)) / len(normalized)
    tokens = " ".join(normalized).split()
    trigrams = [tuple(tokens[index : index + 3]) for index in range(max(0, len(tokens) - 2))]
    trigram_ratio = 0.0 if not trigrams else 1.0 - len(set(trigrams)) / len(trigrams)
    return round(max(0.0, min(1.0, max(duplicate_ratio, trigram_ratio))), 4)


def build_transcript_quality_report(
    result: dict[str, Any] | None,
    duration_seconds: float | None,
    thresholds: TranscriptQualityThresholds | None = None,
) -> TranscriptQualityReport:
    thresholds = thresholds or TranscriptQualityThresholds.from_env()
    result = result or {}
    raw_segments = result.get("segments") or []
    duration_ms = max(0, int(round(float(duration_seconds or 0.0) * 1000)))
    valid: list[tuple[float, float, str]] = []
    empty_count = 0
    invalid_count = 0
    outside_count = 0
    non_monotonic_count = 0
    previous_start = -1.0
    for segment in raw_segments:
        text = _text(segment) if isinstance(segment, dict) else ""
        if not text:
            empty_count += 1
        try:
            start = float(segment.get("start", 0.0))
            end = float(segment.get("end", 0.0))
        except (AttributeError, TypeError, ValueError):
            invalid_count += 1
            continue
        if start < 0 or end < start:
            invalid_count += 1
            continue
        if duration_seconds and end > duration_seconds + thresholds.timestamp_tolerance_seconds:
            invalid_count += 1
            outside_count += 1
            continue
        if start < previous_start:
            non_monotonic_count += 1
        previous_start = start
        if text and end > start:
            valid.append((start, end, text))

    valid.sort(key=lambda item: (item[0], item[1]))
    first = valid[0][0] if valid else None
    last = max(item[1] for item in valid) if valid else None
    leading = max(0.0, first or 0.0)
    trailing = max(0.0, (duration_seconds or last or 0.0) - (last or 0.0))
    largest_internal = 0.0
    union_seconds = 0.0
    if valid:
        cursor = valid[0][0]
        union_seconds = 0.0
        for start, end, _ in valid:
            if start > cursor:
                largest_internal = max(largest_internal, start - cursor)
            if end > cursor:
                union_seconds += end - max(cursor, start)
                cursor = end
    total_seconds = max(0.0, float(duration_seconds or 0.0))
    speech_ratio = min(1.0, union_seconds / total_seconds) if total_seconds else (1.0 if valid else 0.0)
    span_ratio = min(1.0, max(0.0, ((last or 0.0) - (first or 0.0)) / total_seconds)) if total_seconds else (1.0 if valid else 0.0)
    words = _words(result)
    confidences = [confidence for word in words if (confidence := _confidence(word)) is not None]
    low_ratio = sum(value < thresholds.low_confidence_threshold for value in confidences) / len(confidences) if confidences else 0.0
    average_confidence = sum(confidences) / len(confidences) if confidences else None
    texts = [item[2] for item in valid]
    repetition = _repetition_score(texts)
    has_text = bool(texts)
    has_words = bool(words)
    text_word_count = _text_word_count(texts)
    aligned_word_count = len(words)

    coverage_component = min(1.0, max(speech_ratio, span_ratio) / max(thresholds.min_coverage, 0.01))
    if trailing > thresholds.max_trailing_gap_seconds:
        coverage_component *= 0.5
    if largest_internal > thresholds.max_internal_gap_seconds:
        coverage_component *= 0.7
    timestamp_component = max(0.0, 1.0 - min(1.0, (invalid_count + non_monotonic_count) / max(1, len(raw_segments))))
    word_component = 1.0 if has_words else 0.0
    confidence_component = average_confidence if average_confidence is not None else 0.5
    repetition_component = 1.0 - repetition
    score = round(100.0 * (0.30 * coverage_component + 0.25 * timestamp_component + 0.20 * word_component + 0.15 * confidence_component + 0.10 * repetition_component), 2)

    reasons: list[str] = []
    if not has_text:
        reasons.append("TRANSCRIPT_EMPTY")
    if text_word_count < thresholds.min_words:
        reasons.append("TRANSCRIPT_TOO_SHORT")
    if speech_ratio < thresholds.min_coverage and span_ratio < thresholds.min_coverage:
        reasons.append("TRANSCRIPT_LOW_COVERAGE")
    if trailing > thresholds.max_trailing_gap_seconds:
        reasons.append("TRANSCRIPT_INCOMPLETE")
    if largest_internal > thresholds.max_internal_gap_seconds:
        reasons.append("TRANSCRIPT_LARGE_INTERNAL_GAP")
    if invalid_count:
        reasons.append("TRANSCRIPT_INVALID_TIMECODE")
    if outside_count:
        reasons.append("TRANSCRIPT_OUTSIDE_MEDIA")
    if non_monotonic_count:
        reasons.append("TRANSCRIPT_NON_MONOTONIC")
    if not has_words:
        reasons.append("WORD_TIMESTAMPS_MISSING")
    if low_ratio > thresholds.max_low_confidence_ratio:
        reasons.append("TRANSCRIPT_LOW_CONFIDENCE")
    if repetition >= thresholds.repetition_threshold:
        reasons.append("TRANSCRIPT_REPETITION")

    return TranscriptQualityReport(
        duration_ms=duration_ms,
        segment_count=len(raw_segments),
        # ``word_count`` predates the split counters and is intentionally kept
        # as the aligned-word count for consumers that already persist it.
        word_count=aligned_word_count,
        text_word_count=text_word_count,
        aligned_word_count=aligned_word_count,
        first_speech_ms=int(round(first * 1000)) if first is not None else None,
        last_speech_ms=int(round(last * 1000)) if last is not None else None,
        leading_gap_ms=int(round(leading * 1000)),
        trailing_gap_ms=int(round(trailing * 1000)),
        largest_internal_gap_ms=int(round(largest_internal * 1000)),
        speech_coverage_ratio=round(speech_ratio, 4),
        transcript_span_ratio=round(span_ratio, 4),
        empty_segment_count=empty_count,
        invalid_timestamp_count=invalid_count,
        non_monotonic_segment_count=non_monotonic_count,
        average_word_confidence=round(average_confidence, 4) if average_confidence is not None else None,
        low_confidence_word_ratio=round(low_ratio, 4),
        repetition_score=repetition,
        has_text=has_text,
        has_words=has_words,
        quality_score=score,
        reasons=reasons,
    )


def quality_gate(report: TranscriptQualityReport, thresholds: TranscriptQualityThresholds | None = None) -> dict[str, Any]:
    thresholds = thresholds or TranscriptQualityThresholds.from_env()
    fatal = not report.has_text or report.segment_count == 0 or report.invalid_timestamp_count > 0 or report.non_monotonic_segment_count > 0
    # Missing word timestamps are an alignment-quality warning, not evidence
    # that a second full ASR pass will improve the transcript. A pre-alignment
    # retry is reserved for genuinely bad segment-level ASR output.
    retryable_reasons = {"TRANSCRIPT_EMPTY", "TRANSCRIPT_TOO_SHORT", "TRANSCRIPT_LOW_COVERAGE", "TRANSCRIPT_INCOMPLETE", "TRANSCRIPT_LARGE_INTERNAL_GAP", "TRANSCRIPT_LOW_CONFIDENCE", "TRANSCRIPT_REPETITION"}
    retryable = bool(set(report.reasons) & retryable_reasons)
    warning_reasons = list(report.reasons)
    valid = not fatal and report.has_text and report.segment_count > 0
    ready = valid and not warning_reasons and report.has_words and report.aligned_word_count >= thresholds.min_words
    return {"valid": valid, "ready": ready, "retryable": retryable, "fatal": fatal, "warnings": warning_reasons, "report": report.to_dict()}


def is_retryable_quality_failure(gate: dict[str, Any]) -> bool:
    return bool(gate.get("retryable") and not gate.get("ready"))


def compare_transcript_quality(
    primary: dict[str, Any],
    candidate: dict[str, Any],
    duration_seconds: float | None,
    thresholds: TranscriptQualityThresholds | None = None,
) -> tuple[dict[str, Any], str, TranscriptQualityReport, TranscriptQualityReport]:
    thresholds = thresholds or TranscriptQualityThresholds.from_env()
    primary_report = build_transcript_quality_report(primary, duration_seconds, thresholds)
    candidate_report = build_transcript_quality_report(candidate, duration_seconds, thresholds)
    primary_gate = quality_gate(primary_report, thresholds)
    candidate_gate = quality_gate(candidate_report, thresholds)
    candidate_is_better = candidate_gate["valid"] and (
        not primary_gate["valid"]
        or (candidate_gate["ready"] and not primary_gate["ready"])
        or candidate_report.quality_score > primary_report.quality_score
    )
    return (candidate, "fallback", primary_report, candidate_report) if candidate_is_better else (primary, "primary", primary_report, candidate_report)

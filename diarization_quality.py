from __future__ import annotations

from dataclasses import asdict, dataclass
from statistics import median
from typing import Any


UNKNOWN_SPEAKERS = {"", "UNKNOWN", "None", "none", "null"}


def normalize_speaker_label(value: Any) -> str | None:
    label = str(value or "").strip()
    if label.upper() in {"", "UNKNOWN", "NULL", "NONE", "НЕ ОПРЕДЕЛЁН", "НЕ ОПРЕДЕЛЕН"}:
        return None
    return label


@dataclass(frozen=True)
class DiarizationScore:
    profile: str
    score: float
    speaker_count: int
    diar_segment_count: int
    assigned_speaker_count: int
    unknown_ratio: float
    short_turn_count: int
    median_turn_sec: float
    reasons: list[str]

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def _speaker(value: Any) -> str:
    text = str(value or "").strip()
    return "UNKNOWN" if text in UNKNOWN_SPEAKERS else text


def _word_count(segment: dict[str, Any]) -> int:
    words = segment.get("words") or []
    if isinstance(words, list) and words:
        return len(words)
    return len(str(segment.get("text") or "").split())


def _duration(segment: dict[str, Any]) -> float:
    try:
        start = float(segment.get("start") or 0.0)
        end = float(segment.get("end") or start)
    except (TypeError, ValueError):
        return 0.0
    return max(0.0, end - start)


def _speaker_set(items: list[dict[str, Any]]) -> set[str]:
    speakers = set()
    for item in items:
        speaker = _speaker(item.get("speaker"))
        if speaker != "UNKNOWN":
            speakers.add(speaker)
    return speakers


def count_short_turns(segments: list[dict[str, Any]], *, max_sec: float = 1.2, max_words: int = 2) -> int:
    count = 0
    for index, segment in enumerate(segments):
        cur_speaker = _speaker(segment.get("speaker"))
        if cur_speaker == "UNKNOWN":
            continue
        prev_speaker = _speaker(segments[index - 1].get("speaker")) if index > 0 else ""
        next_speaker = _speaker(segments[index + 1].get("speaker")) if index + 1 < len(segments) else ""
        has_switch_context = prev_speaker != cur_speaker or next_speaker != cur_speaker
        is_between_same_speaker = prev_speaker == next_speaker and prev_speaker not in {"", "UNKNOWN"}
        if has_switch_context and _duration(segment) <= max_sec:
            count += 1
        elif is_between_same_speaker and (
            _duration(segment) <= max_sec * 1.5
            or (_duration(segment) <= max_sec * 2.5 and _word_count(segment) <= max_words)
        ):
            count += 1
    return count


def smooth_speaker_turns(
    segments: list[dict[str, Any]],
    *,
    max_sec: float = 1.2,
    max_words: int = 3,
) -> list[dict[str, Any]]:
    if len(segments) < 3:
        return [dict(segment) for segment in segments]

    smoothed = [dict(segment) for segment in segments]
    for index in range(1, len(smoothed) - 1):
        prev_speaker = _speaker(smoothed[index - 1].get("speaker"))
        cur_speaker = _speaker(smoothed[index].get("speaker"))
        next_speaker = _speaker(smoothed[index + 1].get("speaker"))
        if cur_speaker == "UNKNOWN" or prev_speaker != next_speaker or cur_speaker == prev_speaker:
            continue
        if _duration(smoothed[index]) <= max_sec or _word_count(smoothed[index]) <= max_words:
            smoothed[index]["speaker"] = prev_speaker
            for word in smoothed[index].get("words") or []:
                if isinstance(word, dict):
                    word["speaker"] = prev_speaker
    return smoothed


def score_diarization_result(
    segments: list[dict[str, Any]],
    diar_segments: list[dict[str, Any]],
    profile: str,
    expected_min: int,
    expected_max: int,
) -> DiarizationScore:
    expected_min = max(1, int(expected_min or 1))
    expected_max = max(expected_min, int(expected_max or expected_min))
    diar_speakers = _speaker_set(diar_segments)
    assigned_speakers = _speaker_set(segments)
    speaker_count = len(diar_speakers)
    assigned_speaker_count = len(assigned_speakers)
    text_segments = [segment for segment in segments if str(segment.get("text") or "").strip()]
    unknown_count = sum(1 for segment in text_segments if _speaker(segment.get("speaker")) == "UNKNOWN")
    unknown_ratio = unknown_count / len(text_segments) if text_segments else 1.0
    short_turn_count = count_short_turns(text_segments)
    durations = [_duration(segment) for segment in text_segments if _duration(segment) > 0.0]
    median_turn_sec = float(median(durations)) if durations else 0.0

    score = 100.0
    reasons: list[str] = []

    if speaker_count == 0:
        score -= 35.0
        reasons.append("no_diarization_speakers")
    if assigned_speaker_count == 0:
        score -= 35.0
        reasons.append("no_assigned_speakers")
    if expected_min > 1 and assigned_speaker_count <= 1:
        score -= 30.0
        reasons.append("collapsed_to_one_speaker")
    if assigned_speaker_count < expected_min:
        penalty = 12.0 * (expected_min - assigned_speaker_count)
        score -= penalty
        reasons.append("below_expected_speaker_range")
    if assigned_speaker_count > expected_max:
        penalty = 10.0 * (assigned_speaker_count - expected_max)
        score -= penalty
        reasons.append("above_expected_speaker_range")
    if expected_min == expected_max and assigned_speaker_count != expected_min:
        score -= 25.0
        reasons.append("does_not_match_fixed_speaker_count")
    if unknown_ratio > 0.0:
        score -= min(45.0, unknown_ratio * 45.0)
        reasons.append("unknown_speaker_ratio")
    if short_turn_count:
        score -= min(25.0, short_turn_count * 4.0)
        reasons.append("short_speaker_switches")
    if 0.0 < median_turn_sec < 1.0:
        score -= 8.0
        reasons.append("very_short_median_turn")
    if expected_min <= assigned_speaker_count <= expected_max and unknown_ratio <= 0.05:
        score += 8.0
        reasons.append("speaker_count_in_expected_range")
    if assigned_speaker_count > 1 and median_turn_sec >= 2.0:
        score += 5.0
        reasons.append("stable_multi_speaker_turns")

    return DiarizationScore(
        profile=profile,
        score=round(score, 3),
        speaker_count=speaker_count,
        diar_segment_count=len(diar_segments),
        assigned_speaker_count=assigned_speaker_count,
        unknown_ratio=round(unknown_ratio, 4),
        short_turn_count=short_turn_count,
        median_turn_sec=round(median_turn_sec, 3),
        reasons=reasons,
    )


def choose_best_diarization_candidate(candidates: list[dict[str, Any]]) -> dict[str, Any]:
    if not candidates:
        raise ValueError("No diarization candidates")

    priority = {"diar_soft": 1, "diar": 2}

    def sort_key(candidate: dict[str, Any]) -> tuple[float, int, int]:
        score = candidate.get("score")
        profile = getattr(score, "profile", str(candidate.get("profile") or ""))
        is_primary = 1 if candidate.get("is_primary") else 0
        return (float(getattr(score, "score", -9999.0)), is_primary, -priority.get(profile, 50))

    return max(candidates, key=sort_key)


def diarization_profiles_for_processing_profile(processing_profile: str, primary_profile: str) -> list[str]:
    primary_profile = primary_profile or "diar"
    if processing_profile == "fast":
        return [primary_profile]

    profiles = [primary_profile]
    if primary_profile != "diar_soft":
        profiles.append("diar_soft")
    if processing_profile in {"accurate", "noisy"} and "diar" not in profiles:
        profiles.append("diar")
    return profiles

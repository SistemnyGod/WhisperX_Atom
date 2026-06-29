from __future__ import annotations

from pathlib import Path
from typing import Any


PREPROCESS_PROFILES: dict[str, dict[str, str]] = {
    "asr": {
        "suffix": ".asr.wav",
        "filter": (
            "highpass=f=60,"
            "lowpass=f=7600,"
            "afftdn=nf=-18,"
            "dynaudnorm=f=150:g=15,"
            "acompressor=threshold=-26dB:ratio=2.5:attack=5:release=70,"
            "alimiter=limit=-1dB"
        ),
    },
    "asr_soft": {
        "suffix": ".asr_soft.wav",
        "filter": (
            "highpass=f=40,"
            "lowpass=f=7800,"
            "afftdn=nf=-14,"
            "dynaudnorm=f=120:g=21,"
            "acompressor=threshold=-30dB:ratio=2.0:attack=10:release=120,"
            "alimiter=limit=-1dB"
        ),
    },
    "diar": {
        "suffix": ".diar.wav",
        "filter": (
            "highpass=f=90,"
            "lowpass=f=7600,"
            "afftdn=nf=-18,"
            "dynaudnorm=f=200:g=17,"
            "acompressor=threshold=-30dB:ratio=3:attack=5:release=90,"
            "alimiter=limit=-1dB"
        ),
    },
    "diar_soft": {
        "suffix": ".diar_soft.wav",
        "filter": (
            "highpass=f=65,"
            "lowpass=f=7800,"
            "afftdn=nf=-14,"
            "dynaudnorm=f=180:g=22,"
            "acompressor=threshold=-32dB:ratio=2.2:attack=8:release=120,"
            "alimiter=limit=-1dB"
        ),
    },
}


def preprocess_output_path(input_path: Path, profile: str) -> Path:
    suffix = PREPROCESS_PROFILES.get(profile, PREPROCESS_PROFILES["asr"])["suffix"]
    return input_path.parent / f"{input_path.stem}{suffix}"


def preprocess_filter(profile: str) -> str:
    return PREPROCESS_PROFILES.get(profile, PREPROCESS_PROFILES["asr"])["filter"]


def timed_text_segments(result: dict[str, Any]) -> list[dict[str, Any]]:
    segments = []
    for seg in result.get("segments", []) or []:
        text = str(seg.get("text", "") or "").strip()
        if not text:
            continue
        start = float(seg.get("start", 0) or 0)
        end = float(seg.get("end", start) or start)
        if end < start:
            end = start
        segments.append({"start": start, "end": end, "text": text})
    return sorted(segments, key=lambda item: (item["start"], item["end"]))


def transcript_health(result: dict[str, Any], audio_duration: float) -> dict[str, float]:
    segments = timed_text_segments(result)
    if not segments:
        return {
            "audio_duration": float(audio_duration or 0.0),
            "segment_count": 0.0,
            "text_chars": 0.0,
            "leading_gap": float(audio_duration or 0.0),
            "largest_gap": float(audio_duration or 0.0),
            "coverage_end": 0.0,
            "coverage_ratio": 0.0,
            "speech_seconds": 0.0,
        }

    leading_gap = max(0.0, segments[0]["start"])
    largest_gap = leading_gap
    speech_seconds = 0.0
    previous_end = segments[0]["end"]
    for seg in segments:
        speech_seconds += max(0.0, seg["end"] - seg["start"])
    for seg in segments[1:]:
        gap = max(0.0, seg["start"] - previous_end)
        largest_gap = max(largest_gap, gap)
        previous_end = max(previous_end, seg["end"])

    coverage_end = previous_end
    if audio_duration > 0:
        largest_gap = max(largest_gap, max(0.0, audio_duration - coverage_end))
        coverage_ratio = min(1.0, coverage_end / audio_duration)
    else:
        coverage_ratio = 1.0

    return {
        "audio_duration": float(audio_duration or 0.0),
        "segment_count": float(len(segments)),
        "text_chars": float(sum(len(seg["text"]) for seg in segments)),
        "leading_gap": float(leading_gap),
        "largest_gap": float(largest_gap),
        "coverage_end": float(coverage_end),
        "coverage_ratio": float(coverage_ratio),
        "speech_seconds": float(speech_seconds),
    }


def should_retry_transcription(stats: dict[str, float]) -> bool:
    audio_duration = stats.get("audio_duration", 0.0)
    if audio_duration <= 0:
        return stats.get("segment_count", 0.0) <= 0
    if stats.get("segment_count", 0.0) <= 0:
        return True
    if stats.get("leading_gap", 0.0) >= max(90.0, audio_duration * 0.05):
        return True
    if stats.get("largest_gap", 0.0) >= max(180.0, audio_duration * 0.15):
        return True
    if audio_duration >= 600.0 and stats.get("coverage_ratio", 0.0) < 0.92:
        return True
    return False


def is_retry_result_better(current: dict[str, float], candidate: dict[str, float]) -> bool:
    if candidate.get("segment_count", 0.0) <= 0:
        return False
    if candidate.get("text_chars", 0.0) > current.get("text_chars", 0.0) * 1.1:
        return True
    if candidate.get("leading_gap", float("inf")) + 30.0 < current.get("leading_gap", float("inf")):
        return True
    if candidate.get("largest_gap", float("inf")) + 45.0 < current.get("largest_gap", float("inf")):
        return True
    if candidate.get("coverage_end", 0.0) > current.get("coverage_end", 0.0) + 45.0:
        return True
    return False


def merge_asr_results(prefix_result: dict[str, Any], main_result: dict[str, Any]) -> dict[str, Any]:
    merged = dict(main_result)
    main_segments = list(main_result.get("segments", []) or [])
    prefix_segments = list(prefix_result.get("segments", []) or [])
    if not prefix_segments:
        return merged

    first_main_start = min(
        (float(seg.get("start", 0) or 0) for seg in main_segments if str(seg.get("text", "")).strip()),
        default=float("inf"),
    )
    cutoff = first_main_start - 2.0
    kept_prefix_segments = [
        dict(seg)
        for seg in prefix_segments
        if str(seg.get("text", "")).strip() and float(seg.get("end", seg.get("start", 0)) or 0) <= cutoff
    ]
    if not kept_prefix_segments:
        return merged

    merged["segments"] = kept_prefix_segments + main_segments
    main_words = list(main_result.get("word_segments", []) or [])
    prefix_words = list(prefix_result.get("word_segments", []) or [])
    if prefix_words:
        kept_prefix_words = [
            dict(word)
            for word in prefix_words
            if float(word.get("end", word.get("start", 0)) or 0) <= cutoff
        ]
        if kept_prefix_words or main_words:
            merged["word_segments"] = kept_prefix_words + main_words
    elif "word_segments" in merged:
        merged["word_segments"] = main_words

    merged["text"] = " ".join(
        str(seg.get("text", "")).strip() for seg in merged["segments"] if str(seg.get("text", "")).strip()
    ).strip()
    return merged

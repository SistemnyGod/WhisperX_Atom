"""Compatibility imports for legacy GUI and watcher runtimes."""

from whisperx_atom.transcript_quality import *

from pathlib import Path
from typing import Any


PREPROCESS_PROFILES: dict[str, dict[str, str]] = {
    # Close microphones need only a conservative pass.  Keep the historical
    # names below for compatibility with older manifests and workers.
    "asr_standard": {"suffix": ".asr_standard.wav", "filter": "highpass=f=60,lowpass=f=7600,afftdn=nf=-24,dynaudnorm=f=150:g=7,acompressor=threshold=-24dB:ratio=2:attack=20:release=250:makeup=1,alimiter=limit=-1dB"},
    # The gain is deliberately bounded to 24 dB by dynaudnorm.  Compressor
    # makeup is zero so the far-field profile cannot silently exceed that
    # ceiling; the limiter only protects the derivative from clipping.  This
    # is an ASR-only derivative; canonical raw/FLAC remains untouched.
    "asr_far_field": {"suffix": ".asr_far_field.wav", "filter": "highpass=f=50,lowpass=f=7600,afftdn=nf=-20,dynaudnorm=f=100:g=24,acompressor=threshold=-30dB:ratio=3:attack=15:release=220:makeup=0,alimiter=limit=-1dB"},
    "asr": {"suffix": ".asr.wav", "filter": "highpass=f=60,lowpass=f=7600,afftdn=nf=-20,dynaudnorm=f=150:g=15,acompressor=threshold=-26dB:ratio=2.5:attack=5:release=70,alimiter=limit=-1dB"},
    "asr_soft": {"suffix": ".asr_soft.wav", "filter": "highpass=f=40,lowpass=f=7800,afftdn=nf=-20,dynaudnorm=f=120:g=21,acompressor=threshold=-30dB:ratio=2.0:attack=10:release=120,alimiter=limit=-1dB"},
    "diar": {"suffix": ".diar.wav", "filter": "highpass=f=90,lowpass=f=7600,afftdn=nf=-20,dynaudnorm=f=200:g=17,acompressor=threshold=-30dB:ratio=3:attack=5:release=90,alimiter=limit=-1dB"},
    "diar_soft": {"suffix": ".diar_soft.wav", "filter": "highpass=f=65,lowpass=f=7800,afftdn=nf=-20,dynaudnorm=f=180:g=22,acompressor=threshold=-32dB:ratio=2.2:attack=8:release=120,alimiter=limit=-1dB"},
}


def preprocess_output_path(input_path: Path, profile: str) -> Path:
    suffix = PREPROCESS_PROFILES.get(profile, PREPROCESS_PROFILES["asr"])["suffix"]
    return input_path.parent / f"{input_path.stem}{suffix}"


def preprocess_filter(profile: str) -> str:
    return PREPROCESS_PROFILES.get(profile, PREPROCESS_PROFILES["asr"])["filter"]


def timed_text_segments(result: dict[str, Any]) -> list[dict[str, Any]]:
    segments = []
    for segment in result.get("segments", []) or []:
        text = str(segment.get("text", "") or "").strip()
        if not text:
            continue
        start = float(segment.get("start", 0) or 0)
        end = float(segment.get("end", start) or start)
        if end < start:
            end = start
        segments.append({"start": start, "end": end, "text": text})
    return sorted(segments, key=lambda item: (item["start"], item["end"]))


def transcript_health(result: dict[str, Any], audio_duration: float) -> dict[str, float]:
    segments = timed_text_segments(result)
    if not segments:
        return {"audio_duration": float(audio_duration or 0.0), "segment_count": 0.0, "text_chars": 0.0, "leading_gap": float(audio_duration or 0.0), "largest_gap": float(audio_duration or 0.0), "coverage_end": 0.0, "coverage_ratio": 0.0, "speech_seconds": 0.0}
    leading_gap = max(0.0, segments[0]["start"])
    largest_gap = leading_gap
    speech_seconds = 0.0
    previous_end = segments[0]["end"]
    for segment in segments:
        speech_seconds += max(0.0, segment["end"] - segment["start"])
    for segment in segments[1:]:
        largest_gap = max(largest_gap, max(0.0, segment["start"] - previous_end))
        previous_end = max(previous_end, segment["end"])
    coverage_end = previous_end
    if audio_duration > 0:
        largest_gap = max(largest_gap, max(0.0, audio_duration - coverage_end))
        coverage_ratio = min(1.0, coverage_end / audio_duration)
    else:
        coverage_ratio = 1.0
    return {"audio_duration": float(audio_duration or 0.0), "segment_count": float(len(segments)), "text_chars": float(sum(len(segment["text"]) for segment in segments)), "leading_gap": float(leading_gap), "largest_gap": float(largest_gap), "coverage_end": float(coverage_end), "coverage_ratio": float(coverage_ratio), "speech_seconds": float(speech_seconds)}


def should_retry_transcription(stats: dict[str, float]) -> bool:
    audio_duration = stats.get("audio_duration", 0.0)
    if audio_duration <= 0:
        return stats.get("segment_count", 0.0) <= 0
    return stats.get("segment_count", 0.0) <= 0 or stats.get("leading_gap", 0.0) >= max(90.0, audio_duration * 0.05) or stats.get("largest_gap", 0.0) >= max(180.0, audio_duration * 0.15) or (audio_duration >= 600.0 and stats.get("coverage_ratio", 0.0) < 0.92)


def is_retry_result_better(current: dict[str, float], candidate: dict[str, float]) -> bool:
    return candidate.get("segment_count", 0.0) > 0 and (candidate.get("text_chars", 0.0) > current.get("text_chars", 0.0) * 1.1 or candidate.get("coverage_end", 0.0) > current.get("coverage_end", 0.0) + 45.0)


def merge_asr_results(prefix_result: dict[str, Any], main_result: dict[str, Any]) -> dict[str, Any]:
    merged = dict(main_result)
    main_segments = list(main_result.get("segments", []) or [])
    prefix_segments = list(prefix_result.get("segments", []) or [])
    if not prefix_segments:
        return merged
    first_main_start = min((float(segment.get("start", 0) or 0) for segment in main_segments if str(segment.get("text", "")).strip()), default=float("inf"))
    cutoff = first_main_start - 2.0
    kept = [dict(segment) for segment in prefix_segments if str(segment.get("text", "")).strip() and float(segment.get("end", segment.get("start", 0)) or 0) <= cutoff]
    if not kept:
        return merged
    merged["segments"] = kept + main_segments
    merged["word_segments"] = list(prefix_result.get("word_segments", []) or []) + list(main_result.get("word_segments", []) or [])
    merged["text"] = " ".join(str(segment.get("text", "")).strip() for segment in merged["segments"] if str(segment.get("text", "")).strip()).strip()
    return merged

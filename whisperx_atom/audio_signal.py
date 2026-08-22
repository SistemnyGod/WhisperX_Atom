from __future__ import annotations

"""Bounded, dependency-light acoustic diagnostics for ASR input.

The recorder must never reject a meeting because the microphone is quiet.  This
module therefore only produces metadata and a preprocessing recommendation.  It
reads a WAV in small windows so a long meeting cannot be copied into memory.
"""

from dataclasses import asdict, dataclass
from pathlib import Path
import math
import wave
from typing import Any, Iterable


@dataclass(frozen=True)
class AudioSignalMetrics:
    sample_rate: int
    channels: int
    duration_seconds: float
    active_speech_rms_p90: float
    noise_floor_rms_p20: float
    snr_db: float | None
    peak: float
    clipping_ratio: float
    active_speech_ratio: float
    silence_duration_seconds: float
    window_count: int
    signal_state: str
    dc_offset: float = 0.0
    crest_factor: float | None = None
    discontinuity_ratio: float = 0.0

    @property
    def active_speech_dbfs(self) -> float:
        return _dbfs(self.active_speech_rms_p90)

    @property
    def is_weak(self) -> bool:
        return self.signal_state in {"WEAK", "UNUSABLE"}

    @property
    def is_unusable(self) -> bool:
        return self.signal_state == "UNUSABLE"

    @property
    def recommended_profile(self) -> str:
        if self.signal_state in {"WEAK", "UNUSABLE"} or (self.snr_db is not None and self.snr_db < 8.0):
            return "LARGE_ROOM"
        return "STANDARD"

    def to_dict(self) -> dict[str, Any]:
        value = asdict(self)
        value.update({
            "active_speech_dbfs": round(self.active_speech_dbfs, 2),
            "recommended_profile": self.recommended_profile,
            "is_weak": self.is_weak,
            "is_unusable": self.is_unusable,
        })
        return value


def _dbfs(value: float) -> float:
    return 20.0 * math.log10(max(float(value), 1e-9))


def _percentile(values: list[float], percentile: float) -> float:
    if not values:
        return 0.0
    values = sorted(values)
    index = min(len(values) - 1, max(0, int(round((len(values) - 1) * percentile))))
    return float(values[index])


def _iter_window_rms(path: Path, window_ms: int = 100) -> Iterable[tuple[float, float, float, int, float, float, int]]:
    """Yield bounded signal metrics without loading the recording into memory.

    The final values are the signed sample sum, sum of squares and count of
    abrupt inter-sample jumps. They are diagnostics only and never alter the
    canonical audio.
    """
    try:
        import numpy as np
    except ImportError as exc:  # pragma: no cover - production image includes numpy
        raise RuntimeError("NUMPY_UNAVAILABLE") from exc

    with wave.open(str(path), "rb") as source:
        sample_rate = source.getframerate()
        channels = source.getnchannels()
        width = source.getsampwidth()
        if sample_rate <= 0 or channels <= 0 or width != 2:
            return
        frames_per_window = max(1, int(sample_rate * window_ms / 1000))
        previous: float | None = None
        while True:
            raw = source.readframes(frames_per_window)
            if not raw:
                break
            values = np.frombuffer(raw, dtype="<i2")
            if values.size == 0:
                continue
            if channels > 1:
                usable = values[: values.size - (values.size % channels)]
                values = usable.reshape(-1, channels).mean(axis=1).astype(np.float32)
            normalized = values.astype(np.float32) / 32768.0
            jumps = 0
            if previous is not None and normalized.size:
                jumps += int(abs(float(normalized[0]) - previous) > 0.75)
            if normalized.size > 1:
                jumps += int(np.count_nonzero(np.abs(np.diff(normalized)) > 0.75))
            previous = float(normalized[-1]) if normalized.size else previous
            yield (
                float(np.sqrt(np.mean(normalized * normalized, dtype=np.float64))),
                float(np.max(np.abs(normalized))),
                float(np.mean(np.abs(values) >= 32760)),
                int(values.size),
                float(np.sum(normalized, dtype=np.float64)),
                float(np.sum(normalized * normalized, dtype=np.float64)),
                jumps,
            )


def analyze_wav(path: Path, window_ms: int = 100) -> AudioSignalMetrics:
    """Analyze a PCM16 WAV and return stable diagnostics for quality metadata."""
    try:
        with wave.open(str(path), "rb") as source:
            sample_rate = source.getframerate()
            channels = source.getnchannels()
            frame_count = source.getnframes()
    except (OSError, wave.Error):
        return AudioSignalMetrics(0, 0, 0.0, 0.0, 0.0, None, 0.0, 0.0, 0.0, 0.0, 0, "UNUSABLE")

    rms_values: list[float] = []
    peaks: list[float] = []
    clipping: list[float] = []
    total_windows = 0
    signed_sum = 0.0
    square_sum = 0.0
    sample_count = 0
    discontinuities = 0
    for rms, peak, clipped, count, window_sum, window_square_sum, jumps in _iter_window_rms(path, window_ms):
        rms_values.append(rms)
        peaks.append(peak)
        clipping.append(clipped)
        total_windows += 1
        signed_sum += window_sum
        square_sum += window_square_sum
        sample_count += count
        discontinuities += jumps

    duration = frame_count / sample_rate if sample_rate > 0 else 0.0
    if not rms_values:
        return AudioSignalMetrics(sample_rate, channels, duration, 0.0, 0.0, None, 0.0, 0.0, 0.0, duration, 0, "UNUSABLE")

    # The lower percentile is a robust noise-floor estimate, while the upper
    # percentile follows speech in a distant microphone without being fooled by
    # one click or a single clipping sample.
    noise = _percentile(rms_values, 0.20)
    active = _percentile(rms_values, 0.90)
    peak = max(peaks)
    clip_ratio = sum(clipping) / len(clipping)
    active_threshold = max(0.003, noise * 1.6)
    active_windows = sum(value >= active_threshold for value in rms_values)
    active_ratio = active_windows / len(rms_values)
    snr = 20.0 * math.log10(max(active, 1e-9) / max(noise, 1e-9)) if noise > 0 else None
    silence_duration = max(0.0, duration * (1.0 - active_ratio))
    dc_offset = signed_sum / max(1, sample_count)
    global_rms = math.sqrt(square_sum / max(1, sample_count))
    crest_factor = peak / max(global_rms, 1e-9)
    discontinuity_ratio = discontinuities / max(1, sample_count - 1)

    if active <= 0.001 or peak <= 0.001:
        state = "UNUSABLE"
    elif clip_ratio > 0.01 or peak >= 0.99:
        # Clipping is actionable even when the overall RMS is low (for
        # example, a short loud consonant in a distant-room recording).
        state = "CLIPPING"
    elif active < 0.012 or (snr is not None and snr < 3.0):
        # A constant noisy room has active windows but no separation between
        # speech and the floor.  Treat it as weak so AUTO selects the
        # far-field profile instead of presenting noise as a clean signal.
        state = "WEAK"
    else:
        state = "OK"
    return AudioSignalMetrics(
        sample_rate=sample_rate,
        channels=channels,
        duration_seconds=round(duration, 4),
        active_speech_rms_p90=round(active, 8),
        noise_floor_rms_p20=round(noise, 8),
        snr_db=round(snr, 3) if snr is not None else None,
        peak=round(peak, 8),
        clipping_ratio=round(clip_ratio, 8),
        active_speech_ratio=round(active_ratio, 6),
        silence_duration_seconds=round(silence_duration, 4),
        window_count=total_windows,
        signal_state=state,
        dc_offset=round(dc_offset, 8),
        crest_factor=round(crest_factor, 4),
        discontinuity_ratio=round(discontinuity_ratio, 8),
    )


def mute_wav_intervals(path: Path, intervals: Iterable[tuple[int, int, str]], padding_ms: int = 150) -> Path | None:
    """Create an ASR-only copy with system TTS intervals muted.

    The canonical WAV and the user-visible FLAC are never touched.  Returning
    ``None`` for a non-PCM WAV lets the caller keep the original source rather
    than failing a meeting because a technical marker could not be applied.
    """
    if not intervals or not path.is_file():
        return None
    try:
        with wave.open(str(path), "rb") as source:
            if source.getsampwidth() != 2 or source.getcomptype() != "NONE":
                return None
            sample_rate = source.getframerate()
            channels = source.getnchannels()
            ranges = [(max(0, int((start - padding_ms) * sample_rate / 1000)), max(0, int((end + padding_ms) * sample_rate / 1000))) for start, end, _ in intervals]
            output = path.with_name(f"{path.stem}.tts_muted.wav")
            with wave.open(str(output), "wb") as target:
                target.setnchannels(channels)
                target.setsampwidth(2)
                target.setframerate(sample_rate)
                frame_offset = 0
                while True:
                    raw = source.readframes(max(1, sample_rate * 2))
                    if not raw:
                        break
                    frame_count = len(raw) // (2 * max(1, channels))
                    end_offset = frame_offset + frame_count
                    if any(frame_offset < stop and end_offset > start for start, stop in ranges):
                        try:
                            import numpy as np
                            values = np.frombuffer(raw, dtype="<i2").copy()
                            for start, stop in ranges:
                                left = max(0, start - frame_offset) * channels
                                right = min(frame_count, stop - frame_offset) * channels
                                if right > left:
                                    values[left:right] = 0
                            raw = values.astype("<i2", copy=False).tobytes()
                        except ImportError:
                            return None
                    target.writeframes(raw)
                    frame_offset = end_offset
            return output
    except (OSError, wave.Error):
        return None


def language_quality(text: str, language: str | None, confidence: float | None = None) -> dict[str, Any]:
    """Detect obvious wrong-language/Whisper hallucinations without blocking ASR."""
    import re

    normalized_language = (language or "ru").strip().lower()
    letters = [char for char in text if char.isalpha()]
    cyrillic = sum("\u0400" <= char <= "\u04ff" for char in letters)
    latin = sum(("a" <= char.lower() <= "z") for char in letters)
    total = max(1, len(letters))
    cyrillic_ratio = cyrillic / total
    latin_ratio = latin / total
    known_hallucination = bool(re.search(r"thank you for watching|thanks for watching|subscribe to|thank you", text, re.I))
    tokens = re.findall(r"[\w\u0400-\u04ff]+", text.lower())
    repeated_ratio = 0.0
    if len(tokens) >= 6:
        repeated_ratio = 1.0 - (len(set(tokens)) / len(tokens))
    low_confidence = confidence is not None and confidence < 0.38
    mismatch = normalized_language.startswith("ru") and len(letters) >= 12 and (
        (latin_ratio >= 0.55 and cyrillic_ratio < 0.30)
        or known_hallucination
        or (repeated_ratio >= 0.68 and low_confidence)
    )
    return {
        "language": normalized_language,
        "cyrillic_ratio": round(cyrillic_ratio, 4),
        "latin_ratio": round(latin_ratio, 4),
        "known_hallucination": known_hallucination,
        "confidence": round(float(confidence), 4) if confidence is not None else None,
        "repeated_ratio": round(repeated_ratio, 4),
        "low_confidence": low_confidence,
        "mismatch": mismatch,
        "code": "ASR_LANGUAGE_MISMATCH" if mismatch else None,
    }

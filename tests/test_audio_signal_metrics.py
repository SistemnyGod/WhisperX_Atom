from __future__ import annotations

import math
import struct
import wave

from whisperx_atom.audio_signal import analyze_wav, language_quality


def _wav(path, samples: list[int], rate: int = 16_000) -> None:
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        handle.writeframes(struct.pack("<" + "h" * len(samples), *samples))


def test_signal_metrics_distinguish_silence_and_far_field(tmp_path):
    silent = tmp_path / "silent.wav"
    _wav(silent, [0] * 16_000)
    silent_metrics = analyze_wav(silent)
    assert silent_metrics.signal_state == "UNUSABLE"
    assert silent_metrics.recommended_profile == "LARGE_ROOM"

    # Noise floor followed by a deliberately quiet but measurable voice-like
    # tone.  The analyzer must remain bounded and recommend enhancement.
    samples = [int(80 * math.sin(index / 3)) for index in range(8_000)]
    samples += [int(700 * math.sin(index / 5)) for index in range(8_000)]
    distant = tmp_path / "distant.wav"
    _wav(distant, samples)
    metrics = analyze_wav(distant)
    assert metrics.window_count == 100
    assert metrics.duration_seconds == 1.0
    assert metrics.active_speech_rms_p90 > metrics.noise_floor_rms_p20
    assert metrics.recommended_profile == "LARGE_ROOM"


def test_language_quality_flags_common_english_hallucination_only_for_russian():
    result = language_quality("Thank you for watching!", "ru")
    assert result["mismatch"] is True
    assert result["code"] == "ASR_LANGUAGE_MISMATCH"
    assert language_quality("Добрый день коллеги, начинаем совещание", "ru")["mismatch"] is False
    assert language_quality("Thank you for watching!", "en")["mismatch"] is False

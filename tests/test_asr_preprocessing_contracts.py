from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_auto_mode_uses_canonical_worker_asr_wav_directly():
    pipeline = read("app/transcription_pipeline.py")
    processing = read("whisperx_atom/processing.py")
    assert 'os.getenv("PREPROCESS_ASR", "auto")' in pipeline
    assert "def _is_canonical_asr_wav" in pipeline
    assert 'mode == "auto" and not canonical' in pipeline
    assert "if ctx.asr_audio_path != request.media_path" in processing


def test_noncanonical_asr_and_diarization_keep_independent_preprocessing():
    pipeline = read("app/transcription_pipeline.py")
    processing = read("whisperx_atom/processing.py")
    assert '"preprocessing_profile": "asr_soft" if apply else None' in pipeline
    assert "self._preprocessing_engine.prepare_diarization_input" in processing
    assert "class WhisperXPreprocessingEngine" in read("whisperx_atom/preprocessing_engine.py")
    assert 'for suffix in (".asr.wav", ".asr_soft.wav", ".diar.wav", ".diar_soft.wav")' in pipeline


def test_preprocessing_decision_is_persisted_in_metadata():
    processing = read("whisperx_atom/processing.py")
    assert '"asr_preprocessing": ctx.asr_preprocessing' in processing
    assert "**ctx.asr_preprocessing" in processing
    pipeline = read("app/transcription_pipeline.py")
    for field in ("asr_input_path_kind", "preprocessing_mode", "preprocessing_applied", "preprocessing_profile", "preprocessing_ms"):
        assert field in pipeline


def test_large_room_derivative_keeps_gain_bounded():
    quality = read("transcription_quality.py")
    assert 'dynaudnorm=f=100:g=24' in quality
    assert 'acompressor=threshold=-30dB:ratio=3:attack=15:release=220:makeup=0' in quality
    assert 'alimiter=limit=-1dB' in quality

from __future__ import annotations

import struct
import shutil
import tempfile
import os
import wave
from pathlib import Path

import pytest

from whisperx_atom.processing import _is_silent_pcm


ROOT = Path(__file__).parents[1]


def _wav(path: Path, value: int, frames: int = 16_000) -> None:
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(16_000)
        output.writeframes(struct.pack("<" + "h" * frames, *([value] * frames)))


def test_silence_detection_uses_vectorized_windows_for_synthetic_pcm():
    if os.getenv("WHISPERX_RUN_FILESYSTEM_TESTS") != "1":
        pytest.skip("synthetic filesystem tests are enabled in CI or an explicit local run")
    root = Path(tempfile.mkdtemp(prefix=".technical-test-", dir=str(ROOT)))
    try:
        silent = root / "silent.wav"
        weak = root / "weak.wav"
        speech = root / "speech.wav"
        try:
            _wav(silent, 0)
            _wav(weak, 50)
            _wav(speech, 8_000)
        except PermissionError:
            pytest.skip("filesystem sandbox does not allow synthetic WAV files")

        assert _is_silent_pcm(silent) is True
        assert _is_silent_pcm(weak) is True
        assert _is_silent_pcm(speech) is False
    finally:
        shutil.rmtree(root, ignore_errors=True)


def test_encoder_timeout_and_archive_timeout_contracts_are_explicit():
    runner = (ROOT / "apps/recorder-agent/ExternalProcessRunner.cs").read_text(encoding="utf-8")
    encoder = (ROOT / "apps/recorder-agent/FlacEncoder.cs").read_text(encoding="utf-8")
    worker = (ROOT / "apps/recorder-agent/GlobalRawEncoderWorker.cs").read_text(encoding="utf-8")
    archive = (ROOT / "apps/recorder-agent/LocalArchiveWriter.cs").read_text(encoding="utf-8")

    assert '"ATOM_ENCODER_TIMEOUT_SECONDS"' in runner
    assert '"ATOM_ARCHIVE_TIMEOUT_SECONDS"' in runner
    assert "Kill(entireProcessTree: true)" in runner
    assert "ENCODER_TIMEOUT" in runner and "ARCHIVE_TIMEOUT" in runner
    assert "EncodeAsync" in encoder and "RunEncoderAsync" in encoder
    assert "WaitAsync(PollInterval" in worker and "wake.Drain()" in worker
    assert "RunArchiveAsync" in archive


def test_canonical_asr_provenance_is_required_for_enrichment():
    processing = (ROOT / "whisperx_atom/processing.py").read_text(encoding="utf-8")
    persistence = (ROOT / "workers/ml_worker/persistence.py").read_text(encoding="utf-8")
    assert "ASR_INPUT_MISMATCH" in processing
    assert "asr_audio_hash" in processing
    assert "asr_audio_hash" in persistence
    assert "prepare_asr_input(request.media_path)" in processing
    assert "actual_hash = _sha256_file(request.media_path)" in processing


def test_worker_images_install_pool_extra():
    summary_requirements = (ROOT / "workers/summary_worker/requirements.txt").read_text(encoding="utf-8")
    ml_requirements = (ROOT / "workers/ml_worker/requirements.gpu.txt").read_text(encoding="utf-8")
    assert "psycopg[binary,pool]" in summary_requirements
    assert "psycopg[binary,pool]" in ml_requirements

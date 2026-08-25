from __future__ import annotations

import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

from workers.media_worker.media_worker import analyze_asr_signal, prepare_media


ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "ffmpeg/ffprobe unavailable")
class MediaPipelineTests(unittest.TestCase):
    def test_release_bundle_verifies_numpy_inside_the_media_image(self) -> None:
        verifier = (ROOT / "scripts" / "verify-media-worker-image.ps1").read_text(encoding="utf-8")
        builder = (ROOT / "scripts" / "build-server-bundle.ps1").read_text(encoding="utf-8")
        self.assertIn("import numpy", verifier)
        self.assertIn("analyze_wav(path)", verifier)
        self.assertIn("verify-media-worker-image.ps1", builder)

    def test_numpy_unavailable_is_an_advisory_diagnostic(self) -> None:
        with patch("workers.media_worker.media_worker.analyze_wav", side_effect=RuntimeError("NUMPY_UNAVAILABLE")):
            result = analyze_asr_signal(Path("ignored.wav"))
        self.assertEqual("UNAVAILABLE", result["analysis_state"])
        self.assertEqual("UNKNOWN", result["signal_state"])
        self.assertEqual("NUMPY_UNAVAILABLE", result["analysis_error"])

    def test_analyzer_failure_is_not_reported_as_unusable_audio(self) -> None:
        with patch("workers.media_worker.media_worker.analyze_wav", side_effect=ValueError("bad analyzer")):
            result = analyze_asr_signal(Path("ignored.wav"))
        self.assertEqual("FAILED", result["analysis_state"])
        self.assertEqual("UNKNOWN", result["signal_state"])
        self.assertEqual("AUDIO_SIGNAL_ANALYSIS_FAILED", result["analysis_error"])

    def test_prepare_media_writes_atomic_derivatives(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "input.wav"
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.25", "-ac", "1", "-ar", "48000", str(source)], check=True)
            result = prepare_media(source, root / "derived")
            self.assertGreater(result.duration_ms, 0)
            signal = result.quality_report["audio_signal_metrics"]
            self.assertEqual("READY", signal["analysis_state"])
            self.assertIn(signal["signal_state"], {"OK", "WEAK", "CLIPPING", "UNUSABLE"})
            self.assertEqual("READY", result.quality_report["analysis_state"])
            self.assertEqual(signal["signal_state"], result.quality_report["signal_state"])
            self.assertIsNone(result.quality_report["analysis_error"])
            for path in (result.archive_flac, result.preview_opus, result.asr_wav):
                self.assertTrue(path.exists())
                self.assertGreater(path.stat().st_size, 0)
                self.assertFalse(path.with_name(path.stem + ".part" + path.suffix).exists())


if __name__ == "__main__":
    unittest.main()

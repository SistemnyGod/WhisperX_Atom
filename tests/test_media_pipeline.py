from __future__ import annotations

import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from workers.media_worker.media_worker import prepare_media


@unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "ffmpeg/ffprobe unavailable")
class MediaPipelineTests(unittest.TestCase):
    def test_prepare_media_writes_atomic_derivatives(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "input.wav"
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.25", "-ac", "1", "-ar", "48000", str(source)], check=True)
            result = prepare_media(source, root / "derived")
            self.assertGreater(result.duration_ms, 0)
            for path in (result.archive_flac, result.preview_opus, result.asr_wav):
                self.assertTrue(path.exists())
                self.assertGreater(path.stat().st_size, 0)
                self.assertFalse(path.with_name(path.stem + ".part" + path.suffix).exists())


if __name__ == "__main__":
    unittest.main()

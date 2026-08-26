from __future__ import annotations

import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

from workers.media_worker import media_worker
from workers.media_worker.media_worker import prepare_media, probe_audio


@unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "ffmpeg/ffprobe unavailable")
class MediaFormatTests(unittest.TestCase):
    formats = {
        "wav": ["-c:a", "pcm_s16le"],
        "flac": ["-c:a", "flac"],
        "mp3": ["-c:a", "libmp3lame", "-b:a", "64k"],
        "m4a": ["-c:a", "aac", "-b:a", "64k"],
        "aac": ["-f", "adts", "-c:a", "aac", "-b:a", "64k"],
        "ogg": ["-c:a", "libvorbis", "-b:a", "64k"],
        "opus": ["-f", "opus", "-c:a", "libopus", "-b:a", "32k"],
    }

    def _make_audio(self, path: Path, extra: list[str]) -> None:
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.35", "-ac", "1", "-ar", "48000", *extra, str(path)], check=True)

    def test_supported_audio_formats_probe_and_prepare(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for extension, codec_args in self.formats.items():
                source = root / f"sample.{extension}"
                try:
                    self._make_audio(source, codec_args)
                except subprocess.CalledProcessError as exc:
                    self.skipTest(f"ffmpeg build lacks {extension} encoder: {exc}")
                probe = probe_audio(source)
                self.assertGreater(probe["duration_ms"], 0, extension)
                derivatives = prepare_media(source, root / "derived" / extension)
                self.assertTrue(derivatives.archive_flac.exists(), extension)
                self.assertTrue(derivatives.preview_opus.exists(), extension)
                self.assertTrue(derivatives.asr_wav.exists(), extension)

    def test_video_without_audio_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "video.mp4"
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=5:duration=0.4", "-an", "-c:v", "mpeg4", str(source)], check=True)
            with self.assertRaises(ValueError):
                probe_audio(source)

    def test_corrupt_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "broken.flac"
            source.write_bytes(b"not an audio container")
            with self.assertRaises(Exception):
                probe_audio(source)

    def test_duration_limit_is_opt_in_and_malformed_values_do_not_block_worker(self) -> None:
        with patch.dict("os.environ", {"MAX_MEDIA_DURATION_MS": "0"}, clear=False):
            self.assertEqual(media_worker._duration_limit_from_env(), 0)
        with patch.dict("os.environ", {"MAX_MEDIA_DURATION_MS": "-1"}, clear=False):
            self.assertEqual(media_worker._duration_limit_from_env(), 0)
        with patch.dict("os.environ", {"MAX_MEDIA_DURATION_MS": "not-a-number"}, clear=False):
            self.assertEqual(media_worker._duration_limit_from_env(), 0)
        with patch.dict("os.environ", {"MAX_MEDIA_DURATION_MS": "7200000"}, clear=False):
            self.assertEqual(media_worker._duration_limit_from_env(), 7200000)

    def test_malformed_size_limit_uses_safe_default_and_processing_budget_scales(self) -> None:
        with patch.dict("os.environ", {"MAX_MEDIA_BYTES": "not-a-number"}, clear=False):
            self.assertEqual(media_worker._positive_int_env("MAX_MEDIA_BYTES", media_worker.DEFAULT_MAX_BYTES), media_worker.DEFAULT_MAX_BYTES)
        with patch.dict("os.environ", {"MAX_MEDIA_BYTES": "-1"}, clear=False):
            self.assertEqual(media_worker._positive_int_env("MAX_MEDIA_BYTES", media_worker.DEFAULT_MAX_BYTES), media_worker.DEFAULT_MAX_BYTES)
        self.assertGreater(media_worker._processing_timeout_seconds(60 * 60 * 1000), 900)


if __name__ == "__main__":
    unittest.main()

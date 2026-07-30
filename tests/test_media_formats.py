from __future__ import annotations

import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

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


if __name__ == "__main__":
    unittest.main()

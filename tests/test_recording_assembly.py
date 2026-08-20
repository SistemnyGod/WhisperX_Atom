from __future__ import annotations

import hashlib
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from workers.media_worker import recording_assembly


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    digest.update(path.read_bytes())
    return digest.hexdigest()


@unittest.skipUnless(shutil.which("ffmpeg"), "ffmpeg unavailable")
class RecordingAssemblyTests(unittest.TestCase):
    def test_two_tracks_are_assembled_and_mixed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            chunks = {}
            for name, frequency in (("mic-0", 440), ("mic-1", 445), ("loop-0", 660), ("loop-1", 665)):
                path = root / f"{name}.flac"
                subprocess.run(
                    ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", f"sine=frequency={frequency}:duration=0.25", "-ac", "1", "-ar", "48000", "-c:a", "flac", str(path)],
                    check=True,
                )
                chunks[f"/data/{name}"] = path

            def chunk(key: str, sequence: int, start: int) -> recording_assembly.Chunk:
                path = chunks[key]
                return recording_assembly.Chunk(sequence, key, start, 12_000, path.stat().st_size, _sha256(path))

            tracks = [
                recording_assembly.Track("mic", "microphone", (chunk("/data/mic-0", 0, 0), chunk("/data/mic-1", 1, 12_000)), recording_profile="ONLINE"),
                recording_assembly.Track("loop", "system", (chunk("/data/loop-0", 0, 0), chunk("/data/loop-1", 1, 12_000)), recording_profile="ONLINE"),
            ]

            class FakeConnection:
                def __enter__(self):
                    return self

                def __exit__(self, *_):
                    return False

                def execute(self, *_args):
                    return None

            with patch.object(recording_assembly, "_load_tracks", return_value=tracks), patch.object(recording_assembly, "_storage_path", side_effect=lambda key: chunks[key]), patch.object(recording_assembly, "psycopg", SimpleNamespace(connect=lambda *_args: FakeConnection())):
                output = recording_assembly.assemble_recording_session("session-1", root / "assembled")

            self.assertEqual(output.name, "mixed.flac")
            self.assertGreater(output.stat().st_size, 0)
            self.assertTrue((root / "assembled" / "track-mic.flac").exists())
            self.assertTrue((root / "assembled" / "track-loop.flac").exists())

    def test_sequence_gap_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "chunk.flac"
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=duration=0.1", "-c:a", "flac", str(path)], check=True)
            item = recording_assembly.Chunk(1, "/data/chunk", 0, 4_800, path.stat().st_size, _sha256(path))
            track = recording_assembly.Track("mic", "microphone", (item,))
            with patch.object(recording_assembly, "_storage_path", return_value=path):
                with self.assertRaisesRegex(ValueError, "sequence_gap"):
                    recording_assembly._concat_track(track, Path(directory) / "out.flac")
if __name__ == "__main__":
    unittest.main()

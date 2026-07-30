from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from workers.import_worker.worker import HotFolderImporter, is_candidate, safe_filename


class HotFolderImportTests(unittest.TestCase):
    def test_safe_filename_rejects_traversal(self) -> None:
        self.assertEqual(safe_filename("../../meeting?.flac"), "meeting_.flac")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            audio = root / "meeting.FLAC"
            video = root / "meeting.mp4"
            audio.write_bytes(b"x")
            video.write_bytes(b"x")
            self.assertTrue(is_candidate(audio))
            self.assertFalse(is_candidate(video))

    def test_file_requires_two_stable_scans_and_is_archived(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inbox, staging, archive, rejected = (root / name for name in ("inbox", "staging", "archive", "rejected"))
            inbox.mkdir()
            source = inbox / "daily.flac"
            source.write_bytes(b"audio")
            importer = HotFolderImporter()
            importer.inbox, importer.staging, importer.archive, importer.rejected = inbox, staging, archive, rejected
            importer.token = "token"
            with patch("workers.import_worker.worker.probe_audio", return_value={"duration_ms": 1000}), patch("workers.import_worker.worker.post_import", return_value={"id": "job"}):
                self.assertEqual(importer.scan_once(), 0)
                self.assertEqual(importer.scan_once(), 1)
            self.assertFalse(source.exists())
            self.assertTrue(list(archive.rglob("daily.flac")))


if __name__ == "__main__":
    unittest.main()

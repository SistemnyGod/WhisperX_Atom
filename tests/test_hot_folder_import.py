from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from workers.import_worker.worker import HotFolderImporter, TransientImportError, atomic_copy, is_candidate, safe_filename


class HotFolderImportTests(unittest.TestCase):
    def test_atomic_copy_preserves_source_and_never_leaves_part_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "inbox" / "sample.flac"
            target = root / "staging" / "sample.flac"
            source.parent.mkdir()
            source.write_bytes(b"audio-data")

            atomic_copy(source, target)

            self.assertEqual(target.read_bytes(), b"audio-data")
            self.assertTrue(source.exists())
            self.assertEqual(list(target.parent.glob("*.part")), [])
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

    def test_part_file_waits_for_atomic_rename(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inbox = root / "inbox"
            inbox.mkdir()
            partial = inbox / "meeting.wav.part"
            partial.write_bytes(b"partial")
            importer = HotFolderImporter()
            importer.inbox = inbox
            importer.staging = root / "staging"
            importer.archive = root / "archive"
            importer.rejected = root / "rejected"
            importer.scan_once()
            importer.scan_once()
            self.assertTrue(partial.exists())
            self.assertFalse((root / "rejected").exists())

    def test_transient_api_failure_keeps_source_for_retry(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inbox, staging, archive, rejected = (root / name for name in ("inbox", "staging", "archive", "rejected"))
            inbox.mkdir()
            source = inbox / "offline.flac"
            source.write_bytes(b"audio")
            importer = HotFolderImporter()
            importer.inbox, importer.staging, importer.archive, importer.rejected = inbox, staging, archive, rejected
            with patch("workers.import_worker.worker.probe_audio", return_value={"duration_ms": 1000}), patch(
                "workers.import_worker.worker.post_import",
                side_effect=[TransientImportError("import API unavailable"), {"id": "job"}],
            ):
                self.assertEqual(importer.scan_once(), 0)
                # The second scan reaches the API and must retain the source;
                # a later stable pair is allowed to retry it.
                self.assertEqual(importer.scan_once(), 0)
                self.assertTrue(source.exists())
                self.assertFalse((root / "rejected").exists())


if __name__ == "__main__":
    unittest.main()

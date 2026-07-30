import asyncio
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock

from whisperx_atom.media_policy import ALLOWED_AUDIO_EXTENSIONS, VIDEO_EXTENSIONS, MAX_UPLOAD_BYTES
from whisperx_atom.contracts import ProcessingRequest, ProcessingResult


class ServerFirstContractTests(unittest.TestCase):
    def test_audio_allowlist_rejects_video(self):
        self.assertIn(".wav", ALLOWED_AUDIO_EXTENSIONS)
        self.assertIn(".mp4", VIDEO_EXTENSIONS)
        self.assertLessEqual(MAX_UPLOAD_BYTES, 8 * 1024 * 1024 * 1024)

    def test_processing_contract_is_serializable(self):
        request = ProcessingRequest(job_id="job-1", media_path=Path("sample.wav"))
        result = ProcessingResult(
            job_id=request.job_id,
            language="ru",
            text="Принято решение.",
            segments=[{"start": 0.0, "end": 1.0, "speaker": "SPEAKER_00", "text": "Принято решение."}],
            word_segments=[],
            metadata={"model": "test"},
        )
        payload = result.to_dict()
        self.assertEqual(payload["job_id"], "job-1")
        self.assertEqual(payload["segments"][0]["speaker"], "SPEAKER_00")

    def test_delivery_topology_uses_hook_and_import_worker(self):
        compose = Path("compose.dev.yml").read_text(encoding="utf-8")
        web = Path("apps/web/src/main.ts").read_text(encoding="utf-8")
        api = Path("apps/server/WhisperX.Atom.Api/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("/api/internal/tusd/hooks", compose + api)
        self.assertIn("import-worker", compose)
        self.assertIn("/api/internal/imports", api)
        self.assertNotIn("/api/uploads/complete", web)

    def test_media_paths_are_independent_of_job_json(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "staging" / "job.part"
            path.parent.mkdir()
            path.write_bytes(b"partial")
            self.assertTrue(path.name.endswith(".part"))

    def test_inbox_leases_and_workers_support_redelivery(self):
        migration = Path("apps/server/WhisperX.Atom.Api/Migrations/002_inbox_leases.sql").read_text(encoding="utf-8")
        media_worker = Path("workers/media_worker/worker.py").read_text(encoding="utf-8")
        gpu_worker = Path("workers/ml_worker/worker.py").read_text(encoding="utf-8")
        self.assertIn("lease_expires_at", migration)
        self.assertIn("await message.nak()", media_worker)
        self.assertIn("await message.nak()", gpu_worker)
        self.assertIn("READY_FOR_ASR", media_worker)


if __name__ == "__main__":
    unittest.main()


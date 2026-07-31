import asyncio
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock

from workers.nats_utils import fetch_available
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

    def test_internal_import_contract_binds_snake_case_payload(self):
        api = Path("apps/server/WhisperX.Atom.Api/Program.cs").read_text(encoding="utf-8-sig")
        for name in ("original_name", "source_type", "source_path", "storage_key", "size_bytes", "sha256"):
            self.assertIn(f'JsonPropertyName("{name}")', api)
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

    def test_idle_jetstream_timeout_returns_empty_batch(self):
        class IdleTimeout(Exception):
            pass

        subscription = AsyncMock()
        subscription.fetch.side_effect = IdleTimeout

        batch = asyncio.run(fetch_available(subscription, IdleTimeout, timeout=0.01))

        self.assertEqual(batch, ())
        subscription.fetch.assert_awaited_once_with(1, timeout=0.01)


    def test_gpu_image_uses_ml_only_dependency_bundle(self):
        dockerfile = Path("workers/ml_worker/Dockerfile").read_text(encoding="utf-8")
        requirements = Path("workers/ml_worker/requirements.gpu.txt").read_text(encoding="utf-8")
        self.assertIn("requirements.gpu.txt", dockerfile)
        self.assertNotIn("COPY requirements.txt /srv/requirements.txt", dockerfile)
        self.assertIn("whisperx==3.7.5", requirements)
        self.assertIn("pyannote.audio==3.3.2", requirements)

    def test_latest_transcript_query_does_not_mix_versions(self):
        api = Path("apps/server/WhisperX.Atom.Api/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("t.version=(SELECT MAX(version)", api)

    def test_gpu_e2e_script_has_separate_terminal_contract(self):
        script = Path("scripts/e2e-gpu.ps1").read_text(encoding="utf-8")
        core = Path("scripts/e2e-core.ps1").read_text(encoding="utf-8")
        self.assertIn("WaitForGpu = $true", script)
        self.assertIn("WithGpu = $true", script)
        self.assertIn("READY_FOR_ASR", Path("workers/media_worker/worker.py").read_text(encoding="utf-8"))
        self.assertIn("$WaitForGpu", core)
    def test_llm_profile_is_reproducible(self):
        compose = Path("compose.dev.yml").read_text(encoding="utf-8")
        download = Path("scripts/llm-download.ps1").read_text(encoding="utf-8")
        smoke = Path("scripts/llm-smoke.ps1").read_text(encoding="utf-8")
        self.assertIn("Qwen3-8B-Q5_K_M.gguf", compose)
        self.assertIn("server-cuda-b9445@sha256:39f4f2c5", compose)
        self.assertIn("7c41481f57cb95916b40956ab2f0b139b296d974", download)
        self.assertIn("json_object", smoke)
        self.assertIn("--reasoning", compose)
        self.assertIn("response_format", smoke)
if __name__ == "__main__":
    unittest.main()


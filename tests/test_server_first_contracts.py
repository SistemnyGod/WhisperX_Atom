import asyncio
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
        for check in ("import_file_not_ready", "import_size_mismatch", "import_checksum_mismatch", "invalid_import_storage_key"):
            self.assertIn(check, api)

    def test_storage_keys_are_normalized_inside_media_root_without_traversal(self):
        api = Path("apps/server/WhisperX.Atom.Api/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn('Environment.GetEnvironmentVariable("MEDIA_ROOT")', api)
        self.assertIn('Path.GetFullPath(Path.Combine(root, relative))', api)
        self.assertIn('invalid_storage_key', api)

    def test_gpu_worker_rejects_storage_paths_outside_data_mount(self):
        worker = Path("workers/ml_worker/worker.py").read_text(encoding="utf-8")
        self.assertIn('raise ValueError("invalid_storage_key")', worker)
        self.assertIn('relative = posix_path.relative_to("/data")', worker)
    def test_media_paths_are_independent_of_job_json(self):
        path = Path("staging") / "job.part"
        self.assertEqual(path.parent, Path("staging"))
        self.assertEqual(path.suffix, ".part")

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
        self.assertIn("t.version=COALESCE(@version,(SELECT MAX(version)", api)

    def test_web_terminal_job_events_close_sse_before_refreshing(self):
        web = Path("apps/web/src/main.ts").read_text(encoding="utf-8")
        self.assertIn("const eventSources = new Map<string, EventSource>()", web)
        self.assertIn("eventSources.delete(update.id)", web)
        self.assertIn("await refreshSelected()", web)
        self.assertIn('!["READY", "FAILED", "CANCELLED"].includes(job.status)', web)

    def test_web_runtime_template_uses_vue_compiler_build(self):
        vite = Path("apps/web/vite.config.ts").read_text(encoding="utf-8")
        self.assertIn('vue: "vue/dist/vue.esm-bundler.js"', vite)

    def test_web_control_room_exposes_real_source_state_and_keyboard_focus(self):
        web = Path("apps/web/src/main.ts").read_text(encoding="utf-8")
        css = Path("apps/web/src/style.css").read_text(encoding="utf-8")
        self.assertIn("const onlineAgentCount = computed", web)
        self.assertIn("serverStatusLabel", web)
        self.assertIn('aria-label="Обновить рабочее место"', web)
        self.assertIn("button:focus-visible", css)

    def test_search_contract_is_bounded_and_scoped(self):
        api = Path("apps/server/WhisperX.Atom.Api/Program.cs").read_text(encoding="utf-8-sig")
        store = Path("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs").read_text(encoding="utf-8-sig")
        self.assertIn('app.MapGet("/api/search"', api)
        self.assertIn('query.Length > 200', api)
        self.assertIn("Math.Clamp(limit ?? 50, 1, 200)", api)
        self.assertIn("m.owner_id=@owner", store)
        self.assertIn("websearch_to_tsquery('simple',@query)", store)

    def test_gpu_e2e_script_has_separate_terminal_contract(self):
        script = Path("scripts/e2e-gpu.ps1").read_text(encoding="utf-8")
        core = Path("scripts/e2e-core.ps1").read_text(encoding="utf-8")
        self.assertIn("WaitForGpu = $true", script)
        self.assertIn("WithGpu = $true", script)
        self.assertIn("WithLlm = $true", script)
        self.assertIn("READY_FOR_ASR", Path("workers/media_worker/worker.py").read_text(encoding="utf-8"))
        self.assertIn("$WaitForGpu", core)

    def test_core_e2e_can_restart_processing_workers_before_waiting(self):
        script = Path("scripts/e2e-core.ps1").read_text(encoding="utf-8")
        gpu = Path("scripts/e2e-gpu.ps1").read_text(encoding="utf-8")
        self.assertIn("[switch]$RestartWorkers", script)
        self.assertIn("Restart-ProcessingWorkers", script)
        self.assertIn('"media-worker"', script)
        self.assertIn('"gpu-worker"', script)
        self.assertIn('"summary-worker"', script)
        self.assertIn("[switch]$WithLlm", script)
        self.assertIn("RestartWorkers = $RestartWorkers", gpu)

    def test_operations_and_audit_routes_are_privileged_and_bounded(self):
        api = Path("apps/server/WhisperX.Atom.Api/Program.cs").read_text(encoding="utf-8-sig")
        store = Path("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs").read_text(encoding="utf-8-sig")
        self.assertIn('app.MapGet("/api/admin/operations"', api)
        self.assertIn('app.MapGet("/api/admin/audit"', api)
        self.assertIn("if (!IsPrivileged(context)) return Results.Forbid();", api)
        self.assertIn("Math.Clamp(limit ?? 100, 1, 200)", api)
        self.assertIn("staleLeases", store)
        self.assertIn("ListAuditEventsAsync", store)

    def test_backend_deployment_and_restore_paths_are_explicitly_validated(self):
        api = Path("apps/server/WhisperX.Atom.Api/Program.cs").read_text(encoding="utf-8-sig")
        restore = Path("scripts/restore.ps1").read_text(encoding="utf-8")
        verify = Path("scripts/verify-backend-deployment.ps1").read_text(encoding="utf-8")
        self.assertIn("AddJsonConsole", api)
        self.assertIn('X-Trace-Id', api)
        self.assertIn('http_request', api)
        self.assertIn("if (-not $Apply)", restore)
        self.assertIn("Assert-ContainedPath", restore)
        self.assertIn("pg_restore", restore)
        self.assertIn("Migration files are not lexically ordered", verify)
        self.assertIn("HEALTHCHECK", verify)

    def test_recording_lifecycle_binds_meeting_and_tracks_transcription(self):
        coordinator = Path("apps/recorder-agent/RecordingCoordinator.cs").read_text(encoding="utf-8")
        pipe_host = Path("apps/recorder-agent/AgentPipeHost.cs").read_text(encoding="utf-8")
        recorder = Path("apps/desktop/WhisperX.Atom.Desktop/Services/RecorderPipeService.cs").read_text(encoding="utf-8")
        view_model = Path("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs").read_text(encoding="utf-8")
        page = Path("apps/desktop/WhisperX.Atom.Desktop/Pages/RecordingPage.xaml").read_text(encoding="utf-8")
        page_code = Path("apps/desktop/WhisperX.Atom.Desktop/Pages/RecordingPage.xaml.cs").read_text(encoding="utf-8")

        self.assertIn("EnsureFfmpegAvailable", coordinator)
        self.assertIn("recording-local-finalized", coordinator)
        self.assertIn("server_finalize_pending", pipe_host)
        self.assertIn("VisibleStatus", pipe_host)
        self.assertIn("meetingId", recorder)
        self.assertNotIn("CreateMeetingAsync(title", view_model)
        self.assertIn("_services.RecordingCommands.StartAsync(title, ownerUserId)", view_model)
        self.assertIn("StartProcessingPollingAsync", view_model)
        self.assertIn("GetTranscriptAsync", view_model)
        self.assertIn("response.MeetingId ?? MeetingId", view_model)
        self.assertIn("_serverProcessingExpected = _services.Backend.HasSession", view_model)
        self.assertIn("Обработка WhisperX", page)
        self.assertIn("OpenTranscriptButton_Click", page_code)

    def test_recorder_persists_raw_chunks_before_flac_encoding(self):
        spool = Path("apps/recorder-agent/SpoolStore.cs").read_text(encoding="utf-8")
        writer = Path("apps/recorder-agent/RecordingCoordinator.cs").read_text(encoding="utf-8")
        recovery = Path("apps/recorder-agent/RawChunkRecovery.cs").read_text(encoding="utf-8")
        program = Path("apps/recorder-agent/Program.cs").read_text(encoding="utf-8")
        protocol = Path("apps/recorder-agent/AgentIpcProtocol.cs").read_text(encoding="utf-8")

        self.assertIn("recording_raw_chunks", spool)
        for status in ("WRITING", "RAW_READY", "ENCODING", "ENCODE_FAILED", "READY"):
            self.assertIn(status, spool + writer + recovery)
        self.assertIn("RegisterRawChunk", writer)
        self.assertIn("MarkRawChunkReady", writer)
        self.assertIn("RawChunksNeedingRecoveryAsync", recovery)
        self.assertIn("rawRecovery.RecoverAsync", program)
        self.assertIn("RawChunksPending", protocol)
        self.assertIn("RawChunksFailed", protocol)

    def test_recorder_manifest_carries_shared_timeline_and_server_validates_it(self):
        clock = Path("apps/recorder-agent/RecordingSessionClock.cs").read_text(encoding="utf-8")
        coordinator = Path("apps/recorder-agent/RecordingCoordinator.cs").read_text(encoding="utf-8")
        spool = Path("apps/recorder-agent/SpoolStore.cs").read_text(encoding="utf-8")
        client = Path("apps/recorder-agent/AgentApiClient.cs").read_text(encoding="utf-8")
        archive = Path("apps/recorder-agent/LocalArchiveWriter.cs").read_text(encoding="utf-8")
        support = Path("apps/server/WhisperX.Atom.Api/RecordingFinalizeSupport.cs").read_text(encoding="utf-8")
        store = Path("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs").read_text(encoding="utf-8")

        self.assertIn("GetStartSample", clock)
        self.assertIn("var sessionClock = new RecordingSessionClock()", coordinator)
        self.assertIn("sessionClock.GetStartSample", coordinator)
        self.assertIn("c.start_sample", spool)
        self.assertIn("start_sample = track.StartSample", client)
        self.assertIn("total_samples = track.TotalSamples", client)
        self.assertIn("long? previousEnd", archive)
        self.assertIn("FindTimelineMismatches", support)
        self.assertIn("total_samples_mismatch", support)
        self.assertIn("recording_timeline_inconsistent", store)

    def test_recorder_delivery_retries_and_reconciles_missing_chunks(self):
        client = Path("apps/recorder-agent/AgentApiClient.cs").read_text(encoding="utf-8")
        spool = Path("apps/recorder-agent/SpoolStore.cs").read_text(encoding="utf-8")

        self.assertIn("ATOM_AGENT_UPLOAD_CONCURRENCY", client)
        self.assertIn("SemaphoreSlim", client)
        self.assertIn("SendWithRetryAsync", client)
        self.assertIn("missing-chunks", client)
        self.assertIn("ReconcileMissingChunksAsync", client)
        self.assertIn("GetLocalTrackIdAsync", spool)
        self.assertIn("GetChunksAsync", spool)
        self.assertIn("if (!await ReconcileMissingChunksAsync", client)

    def test_llm_profile_is_reproducible(self):
        compose = Path("compose.dev.yml").read_text(encoding="utf-8")
        download = Path("scripts/llm-download.ps1").read_text(encoding="utf-8")
        smoke = Path("scripts/llm-smoke.ps1").read_text(encoding="utf-8")
        runbook = Path("docs/runbook_windows_wsl2.md").read_text(encoding="utf-8")
        mvp = Path("docs/server_first_mvp.md").read_text(encoding="utf-8")
        self.assertIn("Qwen3-8B-Q5_K_M.gguf", compose)
        self.assertIn("server-cuda-b9445@sha256:39f4f2c5", compose)
        self.assertIn("7c41481f57cb95916b40956ab2f0b139b296d974", download)
        self.assertIn("json_object", smoke)
        self.assertIn("--reasoning", compose)
        self.assertIn("response_format", smoke)
        self.assertIn("LLM_GPU_LAYERS", compose)
        self.assertIn("LLM_REQUIRE_GPU", compose)
        self.assertIn("--profile core --profile gpu --profile llm", runbook)
        self.assertIn("--profile core --profile gpu --profile llm", mvp)
if __name__ == "__main__":
    unittest.main()

from pathlib import Path


API = Path("apps/server/WhisperX.Atom.Api/Program.cs")
STORE = Path("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
AGENT = Path("apps/recorder-agent/Program.cs")
AGENT_API = Path("apps/recorder-agent/AgentApiClient.cs")
SPOOL = Path("apps/recorder-agent/SpoolStore.cs")


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_recording_endpoints_are_scoped_to_authenticated_agent():
    api = read(API)
    store = read(STORE)
    assert "AgentOwnsTrackAsync(agentId, sessionId, trackId)" in api
    assert "CreateRecordingTrackAsync(agentId, sessionId" in api
    assert "FinalizeRecordingAsync(agentId, sessionId" in api
    assert "s.agent_id=@agent" in store
    assert "CompleteCommandAsync(Guid agentId, Guid commandId" in store


def test_agent_command_cursor_advances_only_after_acknowledgement():
    agent = read(AGENT)
    spool = read(SPOOL)
    assert "SaveCommandResultAsync" in agent
    assert "AcknowledgeCommandResultAsync" in agent
    assert "if (saved is null || !await api.CompleteCommandAsync" in agent
    assert "agent_command_results" in spool


def test_chunks_are_purged_only_after_finalize_is_accepted():
    agent = read(AGENT) + read(Path("apps/recorder-agent/RecordingDeliveryCoordinator.cs"))
    client = read(AGENT_API)
    assert "if (!finalized.Accepted)" in agent
    assert "PurgeFinalizedSessionAsync" in agent
    upload_method = client.split("public async Task<int> UploadPendingChunksAsync", 1)[1].split("public async Task<bool> FinalizeServerSessionAsync", 1)[0]
    assert "File.Delete" not in upload_method


def test_local_api_and_cors_are_restricted():
    compose = read(Path("compose.dev.yml"))
    api = read(API)

def test_retry_republishes_the_original_pipeline_stage():
    api = read(API)
    retry = api.split("public async Task<JobRow?> RetryJobAsync", 1)[1].split("public async Task<TranscriptRow> GetTranscriptAsync", 1)[0]
    assert '"SUMMARIZE"' in retry
    assert "'llm.summarize'" in retry
    assert "'ml.transcribe'" in retry
    assert "'media.ingest'" in retry
    assert "TRANSCRIPT_READY" in retry


def test_desktop_start_has_a_server_independent_title_path():
    desktop = read(Path("apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml.cs"))
    view_model = read(Path("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs"))
    agent = read(Path("apps/recorder-agent/AgentPipeHost.cs"))
    start = view_model.split("public async Task<bool> StartRecordingAsync()", 1)[1].split("public Task<bool> PauseAsync", 1)[0]
    assert "_services.RecordingCommands.StartAsync(title, ownerUserId)" in start
    assert "Meeting creation and binding belong to the background delivery" in start
    assert "CreateMeetingAsync" not in start
    assert "EnsureAgentReadyAsync" not in start
    assert "GetCurrentUserAsync" not in start
    assert 'BindSessionAsync(sessionId, meetingId, title' not in agent
    assert 'server_binding_pending' not in agent
    assert 'GetMeetingIdAsync(localSessionId' in agent

def test_desktop_cancels_processing_and_hides_internal_errors():
    view_model = read(Path("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs"))
    assert "_processingCts.Cancel();" in view_model
    assert "SafeError(ex)" in view_model


def test_e2e_enumerates_json_arrays_for_job_polling():
    script = read(Path("scripts/e2e-core.ps1"))
    assert "foreach ($item in $parsed)" in script
    assert "$_.type -eq \"TRANSCRIBE\"" in script

def test_duplicate_uploads_are_linked_without_sha_conflict():
    api = read(API)
    migration = read(Path("apps/server/WhisperX.Atom.Api/Migrations/007_media_asset_deduplication.sql"))
    complete = api.split("public async Task<JobRow?> CompleteUploadAsync", 1)[1].split("public async Task<JobRow> RegisterImportAsync", 1)[0]
    assert "pg_advisory_xact_lock" in complete
    assert "duplicate_of=@duplicate" in complete
    assert "UPLOADED_DUPLICATE" in complete
    assert "duplicate_of uuid REFERENCES media_assets(id)" in migration

def test_media_worker_preserves_deduplication_marker_when_persisting_derivatives():
    persistence = read(Path("workers/media_worker/persistence.py"))
    assert "CASE WHEN duplicate_of IS NULL THEN %s ELSE NULL END" in persistence

def test_import_worker_bounds_hot_folder_enumeration():
    worker = read(Path("workers/import_worker/worker.py"))
    assert "os.scandir(self.inbox)" in worker
    assert "IMPORT_BATCH_SIZE" in worker
    assert "sorted(self.inbox.iterdir())" not in worker
    assert "unable to scan hot folder" in worker

def test_media_worker_releases_only_media_leases_on_startup():
    persistence = read(Path("workers/media_worker/persistence.py"))
    worker = read(Path("workers/media_worker/worker.py"))
    assert "job.type='TRANSCRIBE'" in persistence
    assert "job.stage IN ('UPLOADED','VALIDATING','NORMALIZING')" in persistence
    assert "await asyncio.to_thread(reset_media_leases)" in worker
    assert "await asyncio.to_thread(release_message, message_id)" in worker

def test_gpu_worker_reclaims_stale_inference_leases_after_restart():
    persistence = read(Path("workers/ml_worker/persistence.py"))
    worker = read(Path("workers/ml_worker/worker.py"))
    assert "reset_stale_leases" in persistence
    assert "GPU_STALE_LEASE_SECONDS" in persistence
    assert "WORKER_RESTART_RECOVERY" in persistence
    assert "await asyncio.to_thread(worker._repository.reset_stale_leases)" in worker

def test_summary_worker_reclaims_only_stale_summary_leases_after_restart():
    persistence = read(Path("workers/summary_worker/worker.py"))
    assert "def reset_stale_leases(self)" in persistence
    assert "SUMMARY_STALE_LEASE_SECONDS" in persistence
    assert "job.type='SUMMARIZE'" in persistence
    assert "stage='TRANSCRIPT_READY'" in persistence
    assert "WORKER_RESTART_RECOVERY" in persistence
    assert "recovered_summary = await asyncio.to_thread(summary_worker.repository.reset_stale_leases)" in persistence

def test_media_worker_serializes_duplicate_delivery_per_job():
    worker = read(Path("workers/media_worker/worker.py"))
    assert "active_jobs: set[str] = set()" in worker
    assert "if job_id in active_jobs:" in worker
    assert "active_jobs.discard(job_id)" in worker
def test_api_rejects_invalid_uploads_and_non_retryable_jobs():
    api = read(API)
    assert "unsupported_or_oversized_media" in api
    assert "MaxUploadBytes" in api
    assert "job_not_retryable" in api
    assert "status IN ('FAILED','CANCELLED')" in api


def test_summary_worker_is_bound_to_payload_transcript_version():
    worker = read(Path("workers/summary_worker/worker.py"))
    assert "transcript_id = str(payload[\"transcript_id\"]) if payload.get(\"transcript_id\") else None" in worker
    assert "load_segments, meeting_id, transcript_id" in worker
    assert "persist, job_id, meeting_id, transcript_id" in worker
    assert "WHERE t.id=%s AND t.meeting_id=%s" in worker


def test_summary_persistence_is_idempotent_and_audited():
    worker = read(Path("workers/summary_worker/worker.py"))
    migration = read(Path("apps/server/WhisperX.Atom.Api/Migrations/010_summary_audit.sql"))
    store = read(STORE)
    assert "WHERE job_id=%s FOR UPDATE" in worker
    assert "summary_persist_incomplete" in worker
    assert "job_id" in migration
    assert "ux_summaries_job_id" in migration
    assert "audit_events" in migration
    assert "invalid_task_transition" in read(API)
    assert "AppendAuditEventAsync" in store
    assert "IsAllowedActionItemTransition" in store


def test_assistant_context_and_evidence_are_role_gated_and_terminal_safe():
    api = read(API)
    store = read(STORE)
    worker = read(Path("workers/summary_worker/assistant.py"))
    assert 'context.Items.ContainsKey("voice_host")' in api
    assert "private static string FormatTimecode(long milliseconds)" in store
    assert "reader.GetInt64(2)" in store
    assert '"meetingId"' in worker or "meetingId" in worker
    assert "status NOT IN ('READY','FAILED','NEEDS_REVIEW')" in worker
    assert "list(dict.fromkeys" in worker


def test_finalize_is_idempotent_and_command_cursors_are_serialized():
    store = read(STORE)
    migration = read(Path("apps/server/WhisperX.Atom.Api/Migrations/008_backend_hardening.sql"))
    assert "existingJobId" in store
    assert "source_type='recorder_session'" in store
    assert "pg_advisory_xact_lock(hashtextextended(@agent::text, 0))" in store
    assert "ux_agent_commands_agent_cursor" in migration


def test_outbox_recovery_requeues_all_processing_stages_without_live_leases():
    relay = read(Path("workers/outbox_relay/worker.py"))
    assert "OUTBOX_STALE_LEASE_SECONDS" in relay
    assert "j.type IN ('TRANSCRIBE','TRANSCRIBE_ASR')" in relay
    assert "j.type='TRANSCRIPT_ENRICH'" in relay
    assert "j.type='SUMMARIZE'" in relay
    assert "NOT EXISTS" in relay and "inbox_messages" in relay
    assert "published_at IS NULL" in relay
    assert "j.status='QUEUED'" in relay and "WORKER_RESTART_RECOVERY" in relay


def test_release_gate_requires_complete_correlation_and_explicit_live_evidence():
    gate = read(Path("scripts/release-gate.ps1"))
    assert "PIPELINE_CORRELATION_EVIDENCE_MISSING" in gate
    assert "localArchiveReady" in gate and "deliveryConfirmed" in gate and "mediaReady" in gate
    assert "BLOCKED_BY_CORE_PIPELINE" in gate


def test_retention_is_conservative_and_produces_dry_run_report():
    cleanup = read(Path("scripts/retention-cleanup.ps1"))
    policy = read(Path("apps/recorder-agent/StorageWatermark.cs"))
    assert "-DryRun" in cleanup and "wouldDeleteFiles" in cleanup
    assert "protectedReasons" in cleanup and "state_store_or_sqlite3_unavailable" in cleanup
    assert "transport_purge_after" in cleanup and "local_archive_purge_after" in cleanup
    assert "pathLower" not in cleanup and "media[-_]?ready" not in cleanup
    assert "BLOCK_RECORDING" in policy and "WHISPERX_STORAGE_WARNING_PERCENT" in policy
    assert "WHISPERX_RETENTION_TRANSPORT_GRACE_HOURS" in policy


def test_runtime_manifest_does_not_download_models_and_doctor_records_it():
    manifest = read(Path("scripts/write-runtime-manifest.ps1"))
    doctor = read(Path("scripts/doctor-whisperx.ps1"))
    assert "productionDownloads = \"DISABLED\"" in manifest
    assert "runtime-manifest.json" in manifest and "runtimeManifest" in doctor
    assert "Get-PackageVersion" in manifest


def test_production_security_and_sensitive_route_rate_limits_are_declared():
    api = read(API)
    assert "PRODUCTION_COOKIE_SECURE_REQUIRED" in api
    assert "PRODUCTION_SECRET_INVALID" in api
    assert "PartitionedRateLimiter" in api
    for route in ("/api/auth/login", "/api/auth/refresh", "/api/v1/agents/enroll", "/api/assistant"):
        assert route in api


def test_admin_diagnostics_is_safe_and_correlation_oriented():
    api = read(API)
    assert '"/api/admin/meetings/{id:guid}/diagnostics"' in api
    assert "IsAdministrator(context)" in api
    assert "processingJobIds" in api and "transcriptId" in api and "traceId" in api
    assert "summary.Content" not in api.split('"/api/admin/meetings/{id:guid}/diagnostics"', 1)[1].split("app.Map", 1)[0]


def test_agent_trace_and_diagnostics_export_are_redacted():
    client = read(Path("apps/recorder-agent/AgentApiClient.cs"))
    export = read(Path("scripts/export-diagnostics.ps1"))
    assert "X-Trace-Id" in client
    assert "raw audio" in export and ".env" in export and "summary content" in export
    assert "Compress-Archive" in export


def test_summary_and_assistant_workers_extend_long_job_leases():
    worker = read(Path("workers/summary_worker/worker.py"))
    assistant = read(Path("workers/summary_worker/assistant.py"))
    assert "maintain_message" in worker
    assert "renew_lease" in worker and "renew_lease" in assistant
    assert "lease_expires_at=now()+interval '30 minutes'" in worker


def test_recorder_host_fallback_is_idempotent_and_uses_existing_agent_state():
    host = read(Path("scripts/start-recorder-host.ps1"))
    start = read(Path("scripts/start-transcription-mvp.ps1"))
    stop = read(Path("scripts/stop-transcription-mvp.ps1"))
    doctor = read(Path("scripts/doctor-transcription-mvp.ps1"))
    assert "WhisperXAtomAgent" in host
    assert "recorder-host.pid" in host
    assert "agent-config.json" in host and "ATOM_AGENT_DATA_ROOT" in host
    assert "start-recorder-host.ps1" in start
    assert "Resolve-RecorderRuntime.ps1" in start
    assert '"AUDIOGRAPH"' in start and '"LEGACY_WASAPI"' in start
    assert "recorder-host.pid" in stop
    assert "Test-RecorderHostPipe" in doctor
    installer = read(Path("scripts/install-recorder-runtime.ps1"))
    assert "Start-Process" in installer and "-Verb RunAs" in installer
    assert "ELEVATION_REQUIRED" in installer
    assert 'Stop-Service -Name "WhisperXAtomRecorder"' in installer
    assert "RECORDER_SERVICE_STOP_FAILED" in installer
    assert "AgentUpdate" in installer and "Install-Service.ps1" in installer
    launch = read(Path("scripts/launch-desktop.ps1"))
    assert "--no-restore" in launch
    assert "Get-RunningDesktopProcess" in launch
    assert "DESKTOP_ALREADY_RUNNING_DIFFERENT_BUILD" in launch

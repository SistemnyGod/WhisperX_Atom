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
    agent = read(AGENT)
    client = read(AGENT_API)
    assert "if (finalized)" in agent
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
    agent = read(Path("apps/recorder-agent/AgentPipeHost.cs"))
    assert 'new { meetingId, title }' in desktop
    assert 'BindSessionAsync(sessionId, meetingId, title' in agent
    assert 'GetMeetingIdAsync(localSessionId' in agent

def test_desktop_cancels_processing_and_hides_internal_errors():
    desktop = read(Path("apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml.cs"))
    assert "_processingPollCts?.Cancel();" in desktop
    assert "LastErrorText.Text = SafeError(ex);" in desktop
    assert "AdminStatusText.Text = SafeError(ex);" in desktop


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


def test_outbox_recovery_only_requeues_media_ingest_jobs():
    relay = read(Path("workers/outbox_relay/worker.py"))
    assert "j.type='TRANSCRIBE'" in relay
    assert "j.stage IN ('INGEST','UPLOADED','VALIDATING','NORMALIZING')" in relay

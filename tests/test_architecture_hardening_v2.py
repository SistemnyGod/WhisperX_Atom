from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_duration_aware_watchdog_scales_long_enrichment_stages():
    source = (ROOT / "workers/ml_worker/worker.py").read_text()
    assert '"ALIGNING"' in source and "duration * 0.75" in source
    assert '"DIARIZING"' in source and "duration)" in source
    assert '"TRANSCRIBING", "ASR", "ASR_RUNNING"' in source and "duration * 1.5" in source
    assert "duration * 3" in source and "8 * 60 * 60" in source


def test_priority_coordination_migration_is_additive_and_fts_cleanup_is_narrow():
    coordination = (ROOT / "apps/server/WhisperX.Atom.Api/Migrations/042_gpu_priority_coordination.sql").read_text()
    cleanup = (ROOT / "apps/server/WhisperX.Atom.Api/Migrations/041_remove_duplicate_russian_fts.sql").read_text()
    assert "ADD COLUMN IF NOT EXISTS workload_type" in coordination
    assert "ADD COLUMN IF NOT EXISTS workload_priority" in coordination
    assert "DROP INDEX IF EXISTS ix_transcript_segments_russian_fts" in cleanup
    assert "ix_transcript_segments_text_russian" not in cleanup


def test_llm_runtime_ownership_is_additive_and_process_scoped():
    ownership = (ROOT / "apps/server/WhisperX.Atom.Api/Migrations/043_llm_runtime_ownership.sql").read_text()
    coordination = (ROOT / "workers/gpu_runtime_coordination.py").read_text()
    worker = (ROOT / "workers/summary_worker/worker.py").read_text()
    assert "ADD COLUMN IF NOT EXISTS llm_owner_heartbeat_at" in ownership
    assert "ADD COLUMN IF NOT EXISTS llm_active_workload" in ownership
    assert "ADD COLUMN IF NOT EXISTS llm_active_request_id" in ownership
    assert "summary-runtime:{socket.gethostname()}:{os.getpid()}" in worker
    assert "heartbeat_owner" in coordination
    assert "reclaim_stale_owner" in coordination
    assert "pg_try_advisory_lock" in coordination
    assert "NOT COALESCE(llm_active,FALSE)" in coordination
    assert "GPU_LLM_OWNER_HEARTBEAT_SECONDS" in worker
    assert "GPU_LLM_OWNER_STALE_SECONDS" in coordination


def test_priority_gate_ignores_stale_assistant_without_lease():
    source = (ROOT / "workers/gpu_lease.py").read_text()
    compose = (ROOT / "compose.dev.yml").read_text()
    assert "GPU_PRIORITY_RUNNING_FRESHNESS_SECONDS" in source
    assert "GPU_PRIORITY_RUNNING_FRESHNESS_SECONDS" in compose
    assert "q.updated_at >= now()" in source
    assert "FROM inbox_messages i" in source
    assert "i.job_id=q.id" in source


def test_worker_uses_v1_preemption_before_lease_and_v2_after_priority_lease():
    source = (ROOT / "workers/ml_worker/worker.py").read_text()
    assert 'workload_type = "V2_ENRICH" if enrichment_job else "V1_ASR"' in source
    assert "if not enrichment_job:" in source
    assert "if enrichment_job:" in source
    assert "request_workload" in source


def test_desktop_progress_fingerprint_ignores_heartbeat_and_has_background_state():
    source = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/ProcessingJobTracker.cs").read_text()
    assert "Background" in source
    fingerprint = source.split("private static string Fingerprint", 1)[1].split("private static DateTimeOffset?", 1)[0]
    assert "LastHeartbeat" not in fingerprint
    assert "ProgressChangedAt" in fingerprint

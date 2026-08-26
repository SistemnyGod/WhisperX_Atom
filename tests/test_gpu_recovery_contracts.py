from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_gpu_claim_has_explicit_ownership_outcomes_and_delayed_redelivery():
    persistence = read("workers/ml_worker/persistence.py")
    worker = read("workers/ml_worker/worker.py")
    assert "MessageClaimResult" in persistence
    for state in ("ACQUIRED", "OWNED_BY_THIS_WORKER", "OWNED_BY_OTHER_WORKER"):
        assert state in persistence
    assert "MessageAlreadyClaimed" in worker
    assert "message.nak(delay=exc.delay_seconds)" in worker
    assert "GPU_JOB_OWNERSHIP_CONFLICT" in worker
    assert "AND worker_id=%s" in persistence.split("def renew_lease", 1)[1].split("def job_progress_liveness", 1)[0]


def test_assistant_gpu_wait_does_not_consume_retry_budget():
    assistant = read("workers/summary_worker/assistant.py")
    lease = read("workers/gpu_lease.py")
    worker = read("workers/summary_worker/worker.py")
    assert "AssistantGpuBusy" in lease
    assert "AssistantGpuWaitScheduled" in assistant
    assert "schedule_gpu_wait" in assistant
    assert "retry_count=retry_count+1" not in assistant.split("def schedule_gpu_wait", 1)[1].split("def context", 1)[0]
    assert "ASSISTANT_WAITING_FOR_GPU" in assistant
    assert "AssistantMessageAlreadyClaimed" in assistant
    assert "RETURNING message_id" in assistant.split("def renew_lease", 1)[1].split("def query", 1)[0]
    assert "except AssistantGpuWaitScheduled" in worker
    assert "except AssistantMessageAlreadyClaimed" in worker


def test_recovery_command_is_preview_by_default_and_preserves_results():
    recovery = read("workers/ml_worker/recovery.py")
    launcher = read("scripts/start-server-bundle.ps1")
    assert "--preview" in recovery and "--apply" in recovery
    assert "WORKER_RESTART_RECOVERY" in recovery
    assert "media, transcripts" in recovery
    assert "SERVER_GPU_RECOVERY_PREVIEW_FAILED" in launcher
    assert "SERVER_GPU_RECOVERY_APPLY_FAILED" in launcher


def test_v2_priority_timeout_is_delayed_retryable_and_does_not_become_terminal_gpu_failure():
    worker = read("workers/ml_worker/worker.py")
    assert '"gpu_lease_priority_timeout"' in worker
    assert 'return "GPU_PRIORITY_TIMEOUT"' in worker
    assert 'if failure_code == "GPU_PRIORITY_TIMEOUT" and enrichment_job' in worker
    assert '"GPU_PRIORITY_WAIT_RETRY_PENDING"' in worker
    assert 'raise RetryScheduled(retry_delay_seconds(scheduled_attempt))' in worker


def test_readiness_surfaces_orphaned_gpu_ownership_and_assistant_queue():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert "gpu_job_orphaned" in api
    assert "gpu_asr_active" in api
    assert "orphanedGpuJobs" in api
    assert "QueuedAssistantQueries" in store

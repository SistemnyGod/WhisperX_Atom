from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_desktop_tracker_has_bounded_sse_and_explicit_liveness_states():
    tracker = read("apps/desktop/WhisperX.Atom.Desktop/Services/ProcessingJobTracker.cs")
    assert "ProcessingJobState" in tracker
    assert "Stalled" in tracker and "Blocked" in tracker and "Ready" in tracker
    assert "CancelAfter(TimeSpan.FromSeconds(5))" in tracker
    assert "JOB_QUEUED_TIMEOUT" in tracker
    assert "JOB_PROGRESS_STALLED" in tracker
    assert "JOB_TRACKER_TIMEOUT" in tracker
    assert "GetMeetingPipelineAsync" in tracker


def test_assistant_retry_is_bounded_and_terminal_grounding_is_not_retried():
    assistant = read("workers/summary_worker/assistant.py")
    worker = read("workers/summary_worker/worker.py")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/038_assistant_retry_policy.sql")
    assert "ASSISTANT_MAX_RETRIES" in assistant
    assert "ASSISTANT_CONTEXT_SIZE" in assistant
    assert "is_retryable_assistant_error" in assistant
    assert "AssistantRetryScheduled" in assistant and "schedule_retry" in assistant
    assert "no_evidence" in assistant and "grounding_rejected" in assistant
    assert "await message.nak(delay=exc.delay_seconds)" in worker
    assert "retry_count" in migration and "next_retry_at" in migration
    assert "retry_count < %s" in assistant


def test_assistant_retry_metadata_is_additive_in_api_contract():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    assert "RetryCount" in store and "retry_count" in store
    assert "RetryCount" in client and "NextRetryAt" in client


def test_gpu_priority_order_keeps_v1_ahead_of_assistant_enrichment_and_summary():
    lease = read("workers/gpu_lease.py")
    gpu = read("workers/ml_worker/worker.py")
    assistant = read("workers/summary_worker/assistant.py")
    summary = read("workers/summary_worker/worker.py")
    assert "ASR (10)" in lease and "Assistant (30)" in lease
    assert "priority=10" in gpu and "priority=50" in gpu
    assert "self._enrichment_gpu_lease if enrichment_job else self._asr_gpu_lease" in gpu
    assert "priority=30" in assistant
    assert "priority=100" in summary
    # Enrichment must not make itself look like pending V1 work.  Keep this
    # assertion semantic rather than depending on the exact SQL formatting:
    # the lease has one explicit ASR type set and a separate enrichment gate.
    asr_type_set = lease.split("WHERE type IN", 1)[1].split(")", 1)[0]
    assert "TRANSCRIBE" in asr_type_set
    assert "TRANSCRIPT_ENRICH" not in asr_type_set
    assert "type='TRANSCRIPT_ENRICH'" in lease


def test_gpu_progress_watchdog_has_stage_liveness_and_single_requeue_gate():
    worker = read("workers/ml_worker/worker.py")
    persistence = read("workers/ml_worker/persistence.py")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/039_job_progress_watchdog.sql")
    coordinator = read("workers/gpu_runtime_coordination.py")
    assert "_watch_progress" in worker
    assert "WorkerProcessRestartRequested" in worker
    assert "requeue_or_fail_stage_timeout" in persistence
    assert "GPU_STAGE_TIMEOUT_REQUEUED" in persistence
    assert "GPU_STAGE_TIMEOUT" in persistence
    assert "timeout_requeue_count" in migration
    assert "stage_changed_at" in migration and "progress_changed_at" in migration
    # Heartbeat renewal must not fake progress and the trigger must handle
    # INSERT without reading OLD.
    assert "updated_at=now()" not in persistence.split("def renew_lease", 1)[1].split("def job_progress_liveness", 1)[0]
    assert "TG_OP = 'INSERT'" in migration
    assert "wait_for_llm_release" in coordinator
    assert "preempt_if_requested" in coordinator


def test_summary_and_assistant_release_resident_llm_for_durable_asr_request():
    summary = read("workers/summary_worker/worker.py")
    assistant = read("workers/summary_worker/assistant.py")
    llama = read("workers/summary_worker/llama_subprocess.py")
    compose = read("compose.dev.yml")
    for source in (summary, assistant):
        assert "llm_may_start" in source
        assert "mark_llm_resident" in source
        assert "preempt_if_requested" in source
    assert 'LLM_RESIDENT_ENABLED", "true"' in llama
    assert 'LLM_IDLE_UNLOAD_SECONDS", "900"' in llama
    assert "GPU_RUNTIME_COORDINATION_ENABLED" in compose


def test_healthy_asr_wait_does_not_consume_assistant_retry_budget():
    assistant = read("workers/summary_worker/assistant.py")
    worker = read("workers/summary_worker/worker.py")
    assert 'raise AssistantGpuBusy("ASSISTANT_WAITING_FOR_GPU")' in assistant
    assert "retry_count unchanged" in assistant
    assert 'os.getenv("ASSISTANT_GPU_QUEUE_TIMEOUT_SECONDS", "3600")' in worker

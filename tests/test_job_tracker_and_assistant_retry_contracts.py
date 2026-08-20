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

import errno

from workers.media_worker.retry import (
    MAX_MEDIA_ATTEMPTS,
    classify_media_failure,
    should_retry,
)


def test_media_failure_classifier_keeps_deterministic_recording_errors_permanent():
    cases = {
        ValueError("recording_chunk_checksum_mismatch:track:0"): "RECORDING_CHUNK_CHECKSUM_MISMATCH",
        ValueError("recording_chunk_sample_gap:track:1"): "RECORDING_TIMELINE_INVALID",
        FileNotFoundError("confirmed chunk missing"): "RECORDING_CHUNK_MISSING",
        ValueError("media has no audio"): "MEDIA_NO_AUDIO",
    }
    for exc, code in cases.items():
        failure = classify_media_failure(exc)
        assert failure.code == code
        assert not failure.transient


def test_one_retry_policy_allows_first_transient_failure_only():
    failure = classify_media_failure(OSError(errno.EBUSY, "file temporarily locked"))
    assert failure.transient
    assert should_retry(0, failure)
    assert not should_retry(MAX_MEDIA_ATTEMPTS, failure)


def test_retry_integration_contract_uses_delayed_nak_and_atomic_attempt_guard():
    worker = open("workers/media_worker/worker.py", encoding="utf-8").read()
    persistence = open("workers/media_worker/persistence.py", encoding="utf-8").read()
    assert "schedule_media_retry" in worker
    assert "await message.nak(delay=RETRY_DELAY_SECONDS)" in worker
    assert "stage='MEDIA_RETRY_WAIT'" in persistence
    assert "attempt=attempt+1" in persistence and "attempt=0" in persistence
    assert "MEDIA_RETRY_WAIT" in persistence
    assert "status <> 'READY'" in persistence  # duplicate READY cannot overwrite the asset


def test_media_lifecycle_does_not_resurrect_cancelled_or_finalized_sessions():
    persistence = open("workers/media_worker/persistence.py", encoding="utf-8").read()
    update = persistence.split("def update_recording_session_state", 1)[1].split("def record_stage_timing", 1)[0]
    assert "state NOT IN ('CANCELLED','FINALIZED')" in update

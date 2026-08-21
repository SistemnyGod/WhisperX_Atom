from pathlib import Path


def test_media_ready_for_asr_resets_watchdog_backoff_atomically():
    source = Path("workers/media_worker/persistence.py").read_text(encoding="utf-8")
    statement = source[source.index("UPDATE jobs SET status='QUEUED',stage='READY_FOR_ASR'"):]
    # The optional not_before delay is part of the parameter tuple now, so do
    # not key this source contract to a fixed tuple arity.
    statement = statement[: statement.index("\n", statement.index("socket.gethostname()")) + 1]
    assert "watchdog_requeue_count=0" in statement
    assert "last_watchdog_requeue_at=NULL" in statement
    assert "stage='READY_FOR_ASR'" in statement

from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_api_exposes_authorized_cancellation_and_deletion():
    source = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'MapPost("/api/meetings/{id:guid}/cancel"' in source
    assert 'MapDelete("/api/meetings/{id:guid}"' in source
    assert "CanAccessMeetingAsync(context, id)" in source
    assert "recording_must_stop_before_cancellation" in source
    assert "filesDeletionQueued" in source
    assert 'physicalCleanup = queued > 0 ? "PENDING" : "COMPLETE"' in source


def test_physical_cleanup_is_durable_and_not_bound_to_delete_request():
    source = read("apps/server/WhisperX.Atom.Api/Program.cs")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/047_storage_deletion_queue.sql")
    assert "storage_deletion_queue" in source
    assert "StorageDeletionService" in source
    assert "QueueStorageDeletionAsync" not in source
    assert "completed_at IS NULL" in source
    assert "FOR UPDATE SKIP LOCKED" in source
    assert "storage_deletion_queue" in migration
    assert "ux_storage_deletion_queue_pending_key" in migration


def test_cancelled_jobs_cannot_reenter_the_pipeline():
    server = read("apps/server/WhisperX.Atom.Api/Program.cs")
    media = read("workers/media_worker/persistence.py")
    ml = read("workers/ml_worker/persistence.py")
    summary = read("workers/summary_worker/worker.py")
    assert "status='CANCELLED'" in server
    assert "status <> 'CANCELLED'" in media
    assert "status <> 'CANCELLED'" in ml
    assert "status <> 'CANCELLED'" in summary
    assert "result discarded because the meeting was cancelled or deleted" in read("workers/ml_worker/worker.py")


def test_server_cancellation_reaches_recorder_and_excludes_local_spool():
    server = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")
    agent = read("apps/recorder-agent/Program.cs")
    assert "QueueRecorderCancellationAsync" in server
    assert '"CANCEL_SERVER_SESSION"' in server
    assert "AgentSessionCancellationTarget" in server
    assert "GetAgentSessionCancellationTargetsAsync" in server
    assert "CANCEL_SERVER_SESSION" in agent
    assert "CANCEL_LOCAL_SESSION" in agent
    assert "CancelServerSessionAsync" in spool
    assert "CancelLocalSessionAsync" in spool
    assert "c.status NOT IN ('CONFIRMED','CANCELLED','BLOCKED','UPLOADING')" in spool
    assert "s.state<>'CANCELLED'" in spool
    assert "SELECT status FROM meetings WHERE id=@id FOR UPDATE" in store


def test_desktop_requires_confirmation_for_cancel_and_delete():
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/MeetingsPage.xaml")
    codebehind = read("apps/desktop/WhisperX.Atom.Desktop/Pages/MeetingsPage.xaml.cs")
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    assert 'Content="Отменить обработку"' in page
    assert 'Content="Удалить"' in page
    assert "CancelProcessingButton_Click" in codebehind
    assert "DeleteMeetingButton_Click" in codebehind
    assert "Удалить совещание?" in codebehind
    assert "CancelMeetingAsync" in client and "DeleteMeetingAsync" in client

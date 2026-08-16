from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_local_acceptance_never_fabricates_server_meeting_id():
    script = read("scripts/acceptance-local-recording.ps1")
    assert "$meetingId = $null" in script
    assert "localOnly = $true" in script
    assert "[Guid]::NewGuid()" not in script
    assert "DELIVERY_FAILED" in script
    assert "MEETING_NOT_FOUND" in script


def test_agent_allows_offline_start_without_owner_or_meeting():
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    start = host.split('case "START":', 1)[1].split('case "PAUSE":', 1)[0]
    assert "Offline/local-first capture is deliberately allowed" in start
    assert 'return Error("OWNER_REQUIRED")' not in start
    assert "localOnly" in start


def test_spool_has_nullable_meeting_and_explicit_delivery_mode():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    assert "meeting_bind_state" in spool
    assert "delivery_mode" in spool
    assert "NOT_REQUESTED" in spool
    assert "PENDING_SERVER" in spool


def test_offline_delivery_is_pending_not_failed():
    coordinator = read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    assert 'DeliveryMode, "LOCAL_ONLY"' in coordinator
    assert 'deliveryState: "PENDING_SERVER"' in coordinator
    assert 'new FinalizationResult(true, "DELIVERY_PENDING"' in coordinator


def test_desktop_start_defers_meeting_creation_to_idempotent_background_bind():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    start = view_model.split("public async Task<bool> StartRecordingAsync", 1)[1].split("public Task<bool> PauseAsync", 1)[0]
    assert "CreateMeetingAsync" not in start
    assert "_services.RecordingCommands.StartAsync(title, ownerUserId)" in start

    host = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "localOnly).ConfigureAwait(false)" in host


def test_server_bind_is_idempotent_by_agent_and_local_session():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/025_recording_bind_idempotency.sql")
    assert "local_session_id=@local" in store
    assert "Created: false" in store
    assert "ux_recording_sessions_agent_local_session" in migration

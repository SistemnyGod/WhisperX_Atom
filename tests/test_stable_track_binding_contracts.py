from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def test_recorder_advertises_stable_track_binding_capability_end_to_end():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    host = read("apps/recorder-host/RecorderHostRuntime.cs")
    heartbeat = read("apps/recorder-agent/AgentApiClient.cs")
    assert "STABLE_TRACK_BINDING_V1" in protocol
    assert "AgentIpcProtocol.StableTrackBindingCapability" in host
    assert "stableTrackBindingV1 = true" in heartbeat


def test_api_fails_closed_for_legacy_or_missing_local_track_binding():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert "RECORDER_UPGRADE_REQUIRED" in api
    assert "LOCAL_TRACK_ID_REQUIRED" in api
    assert "AgentSupportsStableTrackBindingAsync" in api
    assert "capabilities ? 'stableTrackBindingV1'" in store


def test_desktop_requires_stable_binding_before_reporting_recorder_ready():
    controller = read("apps/desktop/WhisperX.Atom.Desktop/Services/RecorderServiceController.cs")
    snapshot = read("apps/desktop/WhisperX.Atom.Desktop/Services/ProductRuntimeSnapshot.cs")
    assert "StableTrackBindingCapability" in controller
    assert "StableTrackBindingCapability" in snapshot


def test_duplicate_track_recovery_is_explicit_and_preserves_chunks():
    recovery = read("workers/ml_worker/recording_recovery.py")
    launcher = read("scripts/recover-recording-duplicate-track.ps1")
    assert "--session-id" in recovery
    assert "--apply" in recovery
    assert "NOT EXISTS (SELECT 1 FROM recording_chunks" in recovery
    assert "DELETE FROM recording_tracks" in recovery
    assert "media.ingest" in recovery
    assert "--preview" in launcher
    assert "--session-id" in launcher

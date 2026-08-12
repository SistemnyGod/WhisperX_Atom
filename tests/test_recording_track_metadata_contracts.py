from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_track_metadata_migration_is_nullable_and_backward_compatible():
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/017_recording_track_metadata.sql")
    for column in ("device_name", "selection_mode", "recording_profile", "encoding", "bits_per_sample"):
        assert f"ADD COLUMN IF NOT EXISTS {column}" in migration
    dto = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "string? DeviceName = null" in dto and "int? BitsPerSample = null" in dto


def test_agent_api_store_and_diagnostics_preserve_safe_track_metadata():
    agent = read("apps/recorder-agent/AgentApiClient.cs")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assembly = read("workers/media_worker/recording_assembly.py")
    for field in ("deviceName", "selectionMode", "recordingProfile", "encoding", "bitsPerSample"):
        assert field in agent
    assert "CreateRecordingTrackAsync(agentId, sessionId, request.TrackType" in api
    assert "recordingTracks = recordingTracks.Select" in api
    for field in ("DeviceId", "DeviceName", "SelectionMode", "RecordingProfile", "Encoding", "BitsPerSample"):
        assert field in store
    assert "recording_profile" in assembly and "device_name" in assembly

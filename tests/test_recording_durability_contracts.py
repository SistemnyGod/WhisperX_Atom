from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SPOOL = (ROOT / "apps" / "recorder-agent" / "SpoolStore.cs").read_text(encoding="utf-8")
VALIDATOR = (ROOT / "apps" / "recorder-agent" / "RecordingTimelineValidator.cs").read_text(encoding="utf-8")
COORDINATOR = (ROOT / "apps" / "recorder-agent" / "RecordingCoordinator.cs").read_text(encoding="utf-8")


def test_local_durability_reuses_encoded_chunks_after_raw_retention():
    """A purged raw row must not make an already durable FLAC look empty."""
    assert "FROM recording_chunks c" in SPOOL
    assert "LEFT JOIN recording_track_info t" in SPOOL
    assert '"UPLOADING"' in SPOOL
    assert "recording_raw_chunks" in SPOOL
    assert "durable_timeline" in SPOOL


def test_local_durability_has_one_pure_timeline_policy():
    assert "RecordingTimelineValidator.Validate(chunks)" in SPOOL
    assert "SESSION_TIMELINE_GAP" in VALIDATOR
    assert "SESSION_TIMELINE_OVERLAP" in VALIDATOR
    assert "SESSION_TRACK_FORMAT_MISMATCH" in VALIDATOR
    assert "SESSION_CHUNK_NOT_DURABLE" in VALIDATOR


def test_durability_policy_requires_contiguous_samples_and_durable_bytes():
    assert "chunk.StartSample != expectedStart" in VALIDATOR
    assert "chunk.SampleCount <= 0" in VALIDATOR
    assert "if (!chunk.IsDurable)" in VALIDATOR


def test_overflow_directory_scan_is_throttled_outside_capture_callback():
    assert "DiscoveryScanInterval" in COORDINATOR
    assert "Volatile.Read(ref _lastDiscoveryScanTimestamp)" in COORDINATOR
    assert "ProcessDiscoveredChunksAsync" in COORDINATOR

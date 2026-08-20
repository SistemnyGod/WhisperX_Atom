from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def test_canonical_recorder_host_owns_retention_worker():
    program = read("apps/recorder-host/Program.cs")
    worker = read("apps/recorder-host/StorageRetentionWorker.cs")
    assert "AddHostedService<StorageRetentionWorker>()" in program
    assert "WHISPERX_RETENTION_INTERVAL_SECONDS" in worker
    assert "Math.Clamp(seconds, 30, 3600)" in worker
    assert "PurgeEligibleRawRecoveryAsync" in worker
    assert "PurgeEligiblePlayableAudioAsync" in worker
    assert "CleanupStaleGeneratedPartsAsync" in worker
    assert "RunStepAsync" in worker
    assert "remaining cleanup steps" in worker
    assert "LastArchiveBytesReclaimed" in worker
    assert "LastRetentionCandidates" in worker


def test_retention_health_is_additive_and_exposed_by_canonical_host():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "StorageRetentionLastRunAtUtc" in protocol
    assert "StorageRetentionPlayableBytesReclaimed" in protocol
    assert "StorageRetentionFailures" in protocol
    assert "StorageRetentionMetrics retentionMetrics" in runtime
    assert "StorageRetentionLastSuccessAtUtc: retention.LastSuccessAtUtc" in runtime


def test_desktop_system_diagnostics_show_retention_without_touching_recording_controls():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/AgentsViewModel.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/AgentsPage.xaml")
    assert "StorageRetentionLastRunAtUtc" in view_model
    assert "RetentionText" in view_model
    assert "Retention локального хранилища" in page
    assert "RetentionWarningText" in page


def test_playable_retention_has_explicit_state_and_deadline():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    policy = read("apps/recorder-agent/StorageWatermark.cs")
    assert "playable_audio_purge_after" in spool
    assert "playable_audio_purged_at" in spool
    assert "ArmPlayableAudioRetentionAsync" in spool
    assert "playable_audio_state='PURGED'" in spool
    assert "delivery_mode<>'LOCAL_ONLY'" in spool
    assert "PlayableAudioRetention <= TimeSpan.Zero" in spool
    assert "WHISPERX_RETENTION_PLAYABLE_HOURS" in policy


def test_retention_validates_master_before_purging_and_preserves_pcm_parts():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    helper = spool.split("private static bool IsValidatedArchivePath", 1)[1].split("public async Task<int> ArmPlayableAudioRetentionAsync", 1)[0]
    cleanup = spool.split("public async Task<long> CleanupStaleGeneratedPartsAsync", 1)[1].split("public async Task<long> PurgeEligibleRawRecoveryAsync", 1)[0]
    assert '"export", "master.flac"' in helper
    assert '"manifest.json"' in helper
    assert '"*.wav.part", "*.flac.part", "*.opus.part"' in cleanup
    assert "*.pcm.part" not in cleanup
    assert "encoding_lease_expires_at > $now" in cleanup
    assert "status='UPLOADING'" in cleanup


def test_raw_purge_requires_a_present_validated_flac_file():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    raw = spool.split("public async Task<long> PurgeEligibleRawRecoveryAsync", 1)[1].split("public async Task<long> PurgeEligibleLocalArchivesAsync", 1)[0]
    assert "c.local_path" in raw
    assert "c.size_bytes" in raw
    assert "File.Exists(row.EncodedPath)" in raw
    assert "encodedLength != row.EncodedSize" in raw


def test_retention_does_not_delete_only_local_copy_or_active_sessions():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    transport = spool.split("GetTransportPurgeCandidatesAsync", 1)[1].split("GetLocalArchivePurgeCandidatesAsync", 1)[0]
    playable = spool.split("GetPlayableAudioPurgeCandidatesAsync", 1)[1].split("PurgeEligiblePlayableAudioAsync", 1)[0]
    for section in (transport, playable):
        assert "delivery_mode <> 'LOCAL_ONLY'" in section or "delivery_mode<>'LOCAL_ONLY'" in section
        assert "s.state NOT IN ('RECORDING','PAUSED','STARTING','FINALIZING'" in section
    raw = spool.split("PurgeEligibleRawRecoveryAsync", 1)[1].split("PurgeEligibleLocalArchivesAsync", 1)[0]
    assert "s.delivery_state IN ('CONFIRMED','COMPLETED')" in raw
    assert "s.media_validated_at IS NOT NULL" in raw

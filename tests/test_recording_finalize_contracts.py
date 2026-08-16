from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_local_archive_uses_bomless_concat_and_atomic_audio_outputs():
    archive = read("apps/recorder-agent/LocalArchiveWriter.cs")
    assert "new UTF8Encoding(false)" in archive
    assert ".part" in archive and ".concat.txt" in archive
    assert "ValidateAudioFileAsync" in archive
    assert "ffprobe" in archive
    assert '"-f", "flac", outputPart' in archive
    assert "IsUsableManifest" in archive
    assert "IsArchiveFileUsable" in archive
    assert "master.flac" in archive
    assert "File.Move(outputPart, output, true)" in archive
    assert "manifest.json" in archive
    assert "var outputDirectory = Path.GetDirectoryName(output)" in archive
    assert "Directory.CreateDirectory(outputDirectory)" in archive
    assert "Path.GetTempPath()" in archive
    assert "WhisperXAtom" in archive


def test_recording_session_has_separate_local_and_delivery_state():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    for field in ("local_finalize_state", "delivery_state", "archive_path", "last_error_code", "retry_count", "next_retry_at"):
        assert field in spool
    for field in ("LocalFinalizeState", "ArchivePath", "ErrorCode", "Retryable", "NextRetryAtUtc"):
        assert field in protocol
    assert "FinalizationResult" in protocol
    assert "LOCAL_ENCODING_FAILED" in host
    assert "SERVER_UNAVAILABLE" in host


def test_archive_completes_before_server_delivery_to_protect_transport_chunks():
    coordinator = read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    run = coordinator.split("private async Task<FinalizationResult> RunCoreAsync", 1)[1]
    assert "await stop.LocalFinalization;" in host
    assert "var archiveResult = await CreateLocalArchiveAsync" in run
    assert "var deliveryResult = await DeliverToServerAsync" in run
    assert run.index("CreateLocalArchiveAsync") < run.index("DeliverToServerAsync")
    assert "Local archive failed; continuing server delivery" in coordinator
    assert "localFinalizeState: \"LOCAL_READY\"" in coordinator
    assert 'return ("FAILED", previous?.ArchivePath)' in coordinator
    assert "deliveryState: \"DELIVERY_FAILED\"" in coordinator
    assert "archiveBarrier" in coordinator
    assert "STREAM_COPY" in read("apps/recorder-agent/LocalArchiveWriter.cs")
    assert "REENCODE_FALLBACK" in read("apps/recorder-agent/LocalArchiveWriter.cs")
    assert "codec_name" in read("apps/recorder-agent/LocalArchiveWriter.cs")
    assert "LocalArchiveState" in read("apps/recorder-agent/AgentIpcProtocol.cs")
    assert "ServerFinalizeState" in read("apps/recorder-agent/AgentIpcProtocol.cs")
    assert "MediaState" in read("apps/recorder-agent/AgentIpcProtocol.cs")


def test_archive_finalization_waits_for_a_stable_encoded_chunk_set():
    coordinator = read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    assert "WaitForEncodedChunksAsync" in coordinator
    assert "raw.Pending == 0 && raw.Writing == 0 && raw.Encoding == 0" in coordinator
    assert "recording_chunks_incomplete" in coordinator
    assert "stableReads >= 2" in coordinator


def test_desktop_maps_finalize_codes_and_never_displays_raw_finalize_error():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/RecordingPage.xaml")
    assert "MapRecordingError" in view_model
    assert "LOCAL_ARCHIVE_FAILED" in view_model
    assert "SERVER_UNAVAILABLE" in view_model
    assert "Открыть локальный архив" in page
    assert "LocalFinalizeStatusLabel" in page
    assert "DeliveryStatusLabel" in page


def test_new_recording_detaches_previous_session_trackers_before_resetting_ui():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    start = view_model.split("public async Task<bool> StartRecordingAsync()", 1)[1]
    start = start.split("public Task<bool> PauseAsync", 1)[0]
    assert "await StopSessionTrackingAsync();" in start
    assert "await StopProcessingPollingAsync();" in start
    assert "private async Task StopSessionTrackingAsync()" in view_model


def test_start_does_not_replay_configuration_commands_in_capture_critical_path():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    start = view_model.split("public async Task<bool> StartRecordingAsync()", 1)[1].split("public Task<bool> PauseAsync", 1)[0]
    assert "SyncConfigurationAsync" not in start
    assert "await RefreshAsync();" in start
    assert "var preflight = await _services.Recorder.PreflightAsync();" in start
    assert "_services.Recorder.StartAsync(title, null, ownerUserId, localOnly: false)" in start


def test_recovery_has_bounded_backoff_and_skips_cancelled_sessions():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    worker = read("apps/recorder-agent/Program.cs") + read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    assert "state NOT IN ('CANCELLED','FINALIZED')" in spool
    assert "'FINALIZED'" in spool
    assert "GetRetryDelay" in worker
    assert "<= 1 => 2" in worker and "2 => 5" in worker and "3 => 15" in worker and "4 => 30" in worker
    assert "PersistFailureAsync" in worker


def test_server_finalize_revalidates_confirmed_chunk_checksum():
    support = read("apps/server/WhisperX.Atom.Api/RecordingFinalizeSupport.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert "FindMissingAsync" in support
    assert "ComputeSha256Async(path)" in support
    assert "await RecordingFinalizeSupport.FindMissingAsync" in store


def test_missing_chunk_reconciliation_validates_confirmed_storage():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    missing = store.split("public async Task<IReadOnlyList<int>?> MissingChunksAsync", 1)[1]
    assert "status='CONFIRMED'" in missing
    assert "StorageHelpers.StoragePath(chunk.StorageKey)" in missing
    assert "new FileInfo(path).Length != chunk.SizeBytes" in missing
    assert "ComputeSha256Async(path)" in missing


def test_recording_assembly_resolves_chunks_below_media_root():
    assembly = read("workers/media_worker/recording_assembly.py")
    assert "PurePosixPath" in assembly
    assert "invalid_recording_chunk_storage_key" in assembly
    assert 'Path(os.getenv("MEDIA_ROOT", "/data"))' in assembly

from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_archive_creation_is_single_flight_and_temps_are_attempt_scoped():
    archive = read("apps/recorder-agent/LocalArchiveWriter.cs")
    coordinator = read("apps/recorder-agent/SessionFinalizationCoordinator.cs")
    assert "ConcurrentDictionary<string, Lazy<Task<string>>>" in archive
    assert "LazyThreadSafetyMode.ExecutionAndPublication" in archive
    assert "delivery.RunAsync" in read("apps/recorder-agent/Program.cs")
    assert "delivery.RunAsync" in read("apps/recorder-agent/AgentPipeHost.cs")
    assert ".concat.txt" in archive and "Guid.NewGuid().ToString(\"N\")" in archive
    assert "SemaphoreSlim(1, 1)" in coordinator


def test_server_receipt_and_status_are_agent_scoped_before_spool_cleanup():
    api = read("apps/recorder-agent/AgentApiClient.cs")
    server = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    agent = read("apps/recorder-agent/AgentPipeHost.cs") + read("apps/recorder-agent/Program.cs") + read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    assert "ServerFinalizeReceipt" in api
    assert "SetServerReceiptAsync" in api
    assert "/status" in server
    assert "s.agent_id=@agent" in store
    assert "GetServerMediaStatusAsync" in agent
    assert "PurgeFinalizedSessionAsync" in agent
    assert "WAITING_SERVER_ASSEMBLY" in agent


def test_sqlite_persists_server_correlation_and_delivery_metrics():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    for field in ("media_asset_id", "processing_job_id", "trace_id", "server_accepted_at", "media_validated_at"):
        assert field in spool
    for field in ("MediaAssetId", "ProcessingJobId", "TraceId", "ChunksReady", "ChunksUploading", "BytesPending"):
        assert field in protocol


def test_active_recording_rebinds_before_background_upload():
    worker = read("apps/recorder-agent/Program.cs")
    api = read("apps/recorder-agent/AgentApiClient.cs")
    assert "EnsureActiveSessionBoundAsync" in worker
    assert "await EnsureActiveSessionBoundAsync(stoppingToken)" in worker
    assert "state.State is not (RecorderState.Recording or RecorderState.Paused)" in worker
    assert "GetServerBindingAsync(localSessionId, track.TrackId" in worker
    assert "upload will retry without losing local chunks" in worker
    assert "_bindingGate" in api
    assert "await _bindingGate.WaitAsync(cancellationToken)" in api


def test_server_marks_chunk_confirmed_only_after_atomic_file_move():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    upload = api.split('app.MapPut("/api/v1/recording-sessions/', 1)[1].split('app.MapGet("/api/v1/recording-sessions/', 1)[0]
    assert "StageChunkAsync" in store
    assert "ConfirmChunkAsync" in store
    assert "@sha,'STAGING',NULL" in store
    assert "AND status='CONFIRMED'" in store
    fresh_upload = upload.split("var staged =", 1)[1]
    assert fresh_upload.index("File.Move(partPath, path, true)") < fresh_upload.index("ConfirmChunkAsync")
    assert "chunk_confirmation_pending" in upload
    assert "Guid.NewGuid().ToString(\"N\")" in upload


def test_transport_spool_purge_requires_server_media_validation():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    purge = spool.split("public async Task PurgeFinalizedSessionAsync", 1)[1]
    eligibility = spool.split("public async Task<IReadOnlyList<RetentionCandidate>> GetTransportPurgeCandidatesAsync", 1)[1].split("public async Task<IReadOnlyList<RetentionCandidate>> GetLocalArchivePurgeCandidatesAsync", 1)[0]
    assert "s.media_validated_at IS NOT NULL" in eligibility
    assert "s.transport_purge_after IS NOT NULL AND s.transport_purge_after <= $now" in eligibility
    assert "s.delivery_state IN ('CONFIRMED','COMPLETED')" in eligibility
    assert "pending.status<>'CONFIRMED'" in eligibility
    assert "if (candidate is null) return;" in purge
    assert purge.index('DELETE FROM recording_raw_chunks') < purge.index("UPDATE recording_sessions SET state='FINALIZED'")


def test_retention_deadlines_are_persisted_and_master_zero_is_forever():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    worker = read("apps/recorder-agent/Program.cs")
    assert "transport_purge_after" in spool
    assert "raw_purge_after" in spool
    assert "local_archive_purge_after" in spool
    assert "now.Add(policy.TransportGrace)" in spool
    assert "policy.LocalMasterRetention <= TimeSpan.Zero ? DBNull.Value" in spool
    assert "PurgeEligibleLocalArchivesAsync" in worker
    assert "PurgeEligibleRawRecoveryAsync" in worker


def test_terminal_server_assembly_failure_does_not_retry_forever_or_purge_spool():
    client = read("apps/recorder-agent/AgentApiClient.cs")
    delivery = read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    assert "ServerMediaStatus" in client
    assert "TerminalFailure" in client
    assert '"SERVER_ASSEMBLY_FAILED"' in client
    assert "GetServerMediaStatusAsync" in delivery
    assert 'new FinalizationResult(false, "SERVER_ASSEMBLY"' in delivery
    assert 'new FinalizationResult(true, "SERVER_FINALIZE"' in delivery


def test_capture_write_failure_is_persisted_and_does_not_leave_recording_running():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    for code in ("AUDIO_SOURCE_FAILED", "STORAGE_WRITE_FAILED", "ENCODER_FAILED"):
        assert code in coordinator
        assert code in host
        assert code in desktop
    assert "ReportFailure(ClassifyWriteFailure(ex), ex)" in coordinator
    assert "BeginStopTracks()" in coordinator
    assert 'localFinalizeState: "FINALIZING_LOCAL"' in coordinator
    assert "_state.Restore(RecorderState.Error, \"capture-failure\")" in coordinator
    assert "BuildSessionStatusAsync(visible.SessionId!" in host


def test_realtime_capture_handoff_does_not_hash_or_persist_on_callback():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    writer = coordinator.split("internal sealed class PcmFlacChunkWriter", 1)[1]
    append = writer.split("public void Append", 1)[1].split("public void FlushCurrentChunk", 1)[0]
    queue = writer.split("private void QueueCurrentChunk", 1)[1].split("private async Task ProcessQueueAsync", 1)[0]
    assert "Channel.CreateBounded" in writer
    assert "BoundedChannelFullMode.Wait" in writer
    assert "ConcurrentQueue<PendingRawChunk>" not in writer
    assert "FileStream RawStream" not in writer
    for forbidden in ("FlacEncoder.ComputeSha256", "RegisterRawChunk", "Flush(true)", "FFmpeg", "WaitAsync"):
        assert forbidden not in append + queue
    assert "raw.Dispose()" in queue
    assert "File.Move(rawPartPath, rawPath, true)" in queue
    assert queue.index("File.Move(rawPartPath, rawPath, true)") < queue.index("_pending.Writer.TryWrite(descriptor)")
    process = writer.split("private async Task ProcessChunkAsync", 1)[1]
    assert "RegisterRawChunk" in process
    assert "FlacEncoder.ComputeSha256" in process
    assert "Flush(true)" in process
    assert "while (true)" in writer
    assert "ProcessDiscoveredChunksAsync" in writer


def test_disk_backed_overflow_is_bounded_and_restart_recoverable():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    recovery = read("apps/recorder-agent/RawChunkRecovery.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")
    writer = coordinator.split("internal sealed class PcmFlacChunkWriter", 1)[1]
    assert "Math.Clamp(value, 1, 64)" in writer  # capacity=1 is a supported stress setting
    assert "_overflow" not in writer
    assert 'Directory.EnumerateFiles(directory, "*.pcm")' in writer
    assert "RawChunkExistsAsync" in writer and "RawChunkExistsAsync" in spool
    assert 'EnumerateFiles(recordingsRoot, "*.pcm", SearchOption.AllDirectories)' in recovery
    assert 'EnumerateFiles(recordingsRoot, "*.pcm.part", SearchOption.AllDirectories)' in recovery
    assert "GetUnregisteredClosedRawBacklogAsync" in spool


def test_upload_queue_prioritizes_active_sessions_and_persists_chunk_backoff():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    api = read("apps/recorder-agent/AgentApiClient.cs")
    assert "ROW_NUMBER() OVER (PARTITION BY c.session_id" in spool
    assert "s.state IN ('RECORDING','PAUSED') THEN 0" in spool
    assert "s.state IN ('FINALIZING','FINALIZE_ACCEPTED') THEN 1" in spool
    for column in ("last_attempt_at", "next_attempt_at", "last_error_code"):
        assert column in spool
    assert "TryBeginChunkUploadAsync" in api
    assert "MarkUploadFailedAsync" in api
    assert "CHUNK_UPLOAD_FAILED" in api
    assert "await Task.WhenAll(uploads)" in api


def test_active_capture_has_priority_over_background_delivery_error_in_status():
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    visible = host.split("private (RecorderState State, string? SessionId, string? Error) VisibleStatus()", 1)[1]
    assert "active capture is authoritative" in visible
    assert visible.index("state.State is RecorderState.Recording") < visible.index("_finalizationErrors")
    assert "backgroundPendingSessions" not in visible  # internal details stay out of primary status


def test_ipc_health_separates_active_capture_from_background_delivery():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")
    for field in ("ActiveSessionId", "BackgroundPendingSessions", "BackgroundFailedSessions"):
        assert field in protocol
    assert "BackgroundPendingUploadSessionCountAsync" in spool
    assert "BackgroundFailedSessionCountAsync" in spool
    assert "activeSessionId = state.State is RecorderState.Recording or RecorderState.Paused" in host
    assert "backgroundPendingSessions = await spool.BackgroundPendingUploadSessionCountAsync" in host
    assert "backgroundFailedSessions = await spool.BackgroundFailedSessionCountAsync" in host


def test_orphan_pcm_part_is_registered_for_restart_recovery():
    recovery = read("apps/recorder-agent/RawChunkRecovery.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")
    assert "EnumerateFiles(recordingsRoot, \"*.pcm.part\"" in recovery
    assert "GetTrackInfoAsync" in recovery and "RegisterRawChunk" in recovery
    assert "GetNextTrackStartSampleAsync" in recovery
    assert "encoding" in spool and "bits_per_sample" in spool


def test_writing_raw_chunk_promotes_pcm_part_before_discarding_it():
    recovery = read("apps/recorder-agent/RawChunkRecovery.cs")
    writing = recovery.split('if (string.Equals(raw.Status, "WRITING"', 1)[1]
    writing = writing.split('else if (!File.Exists(raw.RawPath)', 1)[0]
    assert 'File.Exists(raw.RawPath + ".part")' in writing
    assert 'File.Move(raw.RawPath + ".part", raw.RawPath, true)' in writing
    assert writing.index('File.Move(raw.RawPath + ".part"') < writing.index('raw_chunk_was_not_closed')


def test_normal_encoder_dispose_removes_only_empty_open_pcm_part():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    assert "string? emptyRawPart = null;" in coordinator
    assert "_raw is not null && _sampleCount == 0" in coordinator
    assert "File.Exists(emptyRawPart)" in coordinator


def test_recorder_restart_event_is_structured_and_idempotent():
    program = read("apps/recorder-agent/Program.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")
    assert "RECORDER_RECOVERED_AFTER_RESTART" in program
    assert "previousLocalFinalizeState" in program and "previousDeliveryState" in program
    assert "AddEventIfMissingAsync" in program and "AddEventIfMissingAsync" in spool


def test_startup_retries_legacy_archive_failure_once_without_creating_a_loop():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    program = read("apps/recorder-agent/Program.cs")
    assert "includeLegacyArchiveFailures" in spool
    assert "LOCAL_ENCODING_FAILED','LOCAL_ARCHIVE_FAILED" in spool
    assert "includeLegacyArchiveFailures: true" in program
    assert "SessionsNeedingRecoveryAsync(includeLegacyArchiveFailures, cancellationToken)" in program
    assert "excludes LOCAL_FAILED" in program


def test_recording_ui_renders_real_peak_history_as_waveform():
    control = read("apps/desktop/WhisperX.Atom.Desktop/Controls/AudioWaveformMonitor.xaml.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/RecordingPage.xaml")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    assert "SamplesProperty" in control and "WaveformLine.Points" in control
    assert "MicrophoneWaveform" in page and "SystemAudioWaveform" in page
    assert "AppendWaveformSample" in view_model
    assert "MicrophonePeak" in view_model and "SystemAudioPeak" in view_model


def test_audio_telemetry_includes_rms_clipping_and_stale_window():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    for field in ("Rms", "RmsDb", "Clipping", "LastAudioAtUtc", "SilenceDurationMs", "TelemetryStale"):
        assert field in coordinator
    assert "TimeSpan.FromMilliseconds(750)" in coordinator
    for field in ("MicrophoneRms", "MicrophoneClipping", "MicrophoneTelemetryStale", "RawBacklogHealth"):
        assert field in protocol
    assert "peaks.MicrophoneRms" in host


def test_transcript_fallback_does_not_retry_only_missing_word_timestamps_and_cleans_alternate_temp():
    quality = read("whisperx_atom/transcript_quality.py")
    pipeline = read("app/transcription_pipeline.py")
    assert '"WORD_TIMESTAMPS_MISSING"' not in quality.split("retryable_reasons =", 1)[1].split("\n", 1)[0]
    assert "temp_paths" in pipeline
    assert "register_temp" in pipeline
    assert "alternate_path = self._preprocess_audio_profile" in pipeline
    cache_key = pipeline.split("def _asr_key", 1)[1].split("def get_asr_model", 1)[0]
    assert "(self, model: str, device: str, compute_type: str, backend: str) -> tuple" in cache_key
    assert "return (backend, model, device, compute_type)" in cache_key


def test_delivery_pipeline_is_shared_by_ipc_and_background_worker():
    coordinator = read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    program = read("apps/recorder-agent/Program.cs")
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    assert "class RecordingDeliveryCoordinator" in coordinator
    for operation in ("archive.CreateAsync", "UploadPendingChunksAsync", "FinalizeServerSessionAsync", "PurgeFinalizedSessionAsync"):
        assert operation in coordinator
    assert "delivery.RunAsync" in program
    assert "delivery.RunAsync" in host
    assert "finalizationCoordinator.RunAsync" not in program
    assert "finalizationCoordinator.RunAsync" not in host


def test_recording_profiles_preserve_track_metadata_and_use_controlled_mix():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")
    client = read("apps/recorder-agent/AgentApiClient.cs")
    assembly = read("workers/media_worker/recording_assembly.py")
    worker = read("workers/media_worker/worker.py")
    for profile in ("ROOM", "ONLINE", "MIC_ONLY", "SYSTEM_ONLY"):
        assert profile in coordinator
    for field in ("EndpointId", "DeviceFriendlyName", "SelectionMode", "Profile"):
        assert field in spool and field in client
    assert "normalize=1" in assembly
    assert "alimiter=limit=0.95" in assembly
    assert "ASSEMBLING" in worker and "ASSEMBLED" in worker and "MEDIA_READY" in worker


def test_media_quality_report_is_emitted_with_ready_for_asr_payload():
    media = read("workers/media_worker/media_worker.py")
    worker = read("workers/media_worker/worker.py")
    assert "audio-quality.json" in media
    assert "quality_report" in media
    assert '"audio_quality"' in worker

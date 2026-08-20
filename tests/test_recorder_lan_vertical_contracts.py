from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_start_is_local_first_and_pipe_is_parallel():
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    security = read("apps/recorder-agent/AgentPipeSecurity.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    start = host.split('case "START":', 1)[1].split('case "PAUSE":', 1)[0]
    assert "BindSessionAsync" not in start
    assert '"server_binding_pending"' not in start
    assert 'return new AgentIpcResponse(true, state.State.ToString(), sessionId, null' in start
    assert "MaxServerInstances" in security
    assert "MaxServerInstances = 8" in protocol
    assert "SemaphoreSlim _commandGate" in host
    assert "ConcurrentBag<Task>" not in host
    assert "ConcurrentDictionary<Task, byte>" in host
    assert "_connections.TryRemove" in host


def test_offline_local_session_can_start_without_server_owner_or_meeting():
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    start = host.split('case "START":', 1)[1].split('case "PAUSE":', 1)[0]
    assert "Offline/local-first capture is deliberately allowed" in start
    assert 'return Error("OWNER_REQUIRED")' not in start
    assert "recorder.StartAsync(meetingId, title" in start


def test_start_publishes_local_session_before_capture_callback_can_fail():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    start = coordinator.split("public async Task<string> StartAsync", 1)[1].split("private void EnsureStorageAvailable", 1)[0]
    assert "Publish the local session id before starting WASAPI" in start
    assert start.index("lock (_gate) _sessionId = sessionId") < start.index("microphone.Start()")
    assert "SetSessionStateAsync(sessionId, \"FAILED\", CancellationToken.None)" in start
    assert 'if (microphone.IsFailed)' in start
    assert 'profile == "SYSTEM_ONLY" && systemAudio?.IsFailed == true' in start


def test_typed_ownership_errors_are_preserved():
    api = read("apps/recorder-agent/AgentApiClient.cs")
    server = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    for code in ("OWNER_REQUIRED", "AGENT_USER_LINK_REQUIRED", "MEETING_NOT_FOUND", "MEETING_OWNER_MISMATCH", "MEETING_CANCELLED"):
        assert code in server or code in store
    assert "class AgentApiException" in api
    assert "Retryable" in api
    assert "TraceId" in api


def test_extensible_float_format_is_normalized_before_ffmpeg():
    resolver = read("apps/recorder-agent/AudioSampleFormat.cs")
    encoder = read("apps/recorder-agent/FlacEncoder.cs")
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    assert "WaveFormatExtensible" in resolver
    assert "IeeeFloatSubFormat" in resolver
    assert '"FLOAT32"' in resolver
    assert '"f32le"' in resolver
    assert "AudioStreamFormat contract" in encoder
    assert "format.FfmpegInput" in encoder
    assert "AudioSampleFormatResolver.Resolve(format)" in read("apps/recorder-agent/LegacyWasapiCaptureEngine.cs")


def test_lan_start_keeps_qwen_off_until_explicit_flag():
    compose = read("compose.lan.yml")
    startup = read("scripts/start-whisperx-lan-server.ps1")
    assert '${AUTO_SUMMARY_ENABLED:-false}' in compose
    assert "EnableQwen" in startup
    assert "after transcript gates" in startup


def test_lan_runtime_has_one_canonical_compose_project_and_safe_stop():
    startup = read("scripts/start-whisperx-lan-server.ps1")
    stop = read("scripts/stop-whisperx-lan-server.ps1")
    batch = read("run_whisperx_lan_server.bat")
    assert 'projectName = "whisperx-atom"' in startup
    assert '"--project-name", $projectName' in startup
    assert "label=com.docker.compose.project=whisperx-atom-lan" in startup
    assert "docker stop" in startup
    assert 'projectName = "whisperx-atom"' in stop
    assert '"--project-name", $projectName' in stop
    assert '"--profile", "llm"' in stop
    assert "docker compose stop" not in stop
    assert "start-whisperx-lan-server.ps1" in batch


def test_lan_doctor_checks_core_processing_legacy_and_qwen_state():
    doctor = read("scripts/doctor-whisperx-lan-server.ps1")
    assert 'projectName = "whisperx-atom"' in doctor
    assert 'Check "coreServices"' in doctor
    assert 'Check "processingServices"' in doctor
    assert 'Check "legacyRuntime"' in doctor
    assert "label=com.docker.compose.project=whisperx-atom-lan" in doctor
    assert 'Check "qwen"' in doctor
    assert 'core-readiness.json' in doctor
    assert 'processing-readiness.json' in doctor


def test_lan_start_recovers_inherited_runnable_work_before_gpu_workers():
    startup = read("scripts/start-whisperx-lan-server.ps1")
    assert "STARTUP_RECOVERY_FAILED" in startup
    assert "WORKER_RESTART_RECOVERY" in startup
    assert "RETRY_LIMIT_EXCEEDED" in startup
    assert "ADMIN_REVIEW" in startup
    assert "STARTUP_QUEUE_GUARD_BLOCKED" not in startup
    assert 'postgres", "nats", "api", "tusd", "lan-gateway' in startup
    assert 'media-worker", "gpu-worker' in startup


def test_existing_bootstrap_does_not_rotate_a_healthy_agent_token():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    bootstrap = store.split("public async Task<AgentBootstrapResult?> BootstrapAgentAsync", 1)[1].split("public async Task<bool> AgentUserLinkedAsync", 1)[0]
    assert "if (existing is null)" in bootstrap
    assert "enrollment_hash" in bootstrap
    assert "repeat bootstrap is normally a link refresh" in bootstrap
    assert "replaceMismatchedLocalCredential" in bootstrap
    assert "requested != existing.Id" in bootstrap
    assert "CASE WHEN @replaceCredential THEN @hash ELSE enrollment_hash END" in bootstrap
    assert "existing is null || replaceMismatchedLocalCredential ? token : null" in bootstrap
    assert "enrollment_hash=excluded.enrollment_hash" not in bootstrap


def test_bootstrap_exposes_explicit_agent_state_without_token_rotation():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    assert 'state = result.Linked ? "AGENT_READY" : "AGENT_LINK_PENDING"' in api
    assert "linked = result.Linked" in api
    assert "reenrollRequired = false" in api
    assert '"AGENT_LINK_PENDING"' in client


def test_desktop_blocks_deterministic_bootstrap_errors_but_allows_network_fallback():
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    assert "class DesktopApiException" in client
    assert "retryable" in client
    assert "LastStatus" in view_model
    assert "OfflineEligible" in view_model
    assert '"AGENT_USER_LINK_REQUIRED"' in view_model


def test_existing_agent_origin_update_preserves_identity_and_token():
    api = read("apps/recorder-agent/AgentApiClient.cs")
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    coordinator = read("apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs")
    assert "UpdateServerUrlAsync" in api
    assert '"UPDATE_SERVER_URL"' in host
    assert "Existing Agents keep their token" in coordinator


def test_offline_capture_requires_previous_bootstrap_owner():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    coordinator = read("apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs")
    assert "OfflineEligible" in view_model
    assert "services.Backend.CanUseOffline" in coordinator


def test_recorder_uses_separate_long_upload_timeout():
    api = read("apps/recorder-agent/AgentApiClient.cs")
    assert "CreateHttpClient(TimeSpan.FromSeconds(8))" in api
    assert "CreateHttpClient(TimeSpan.FromSeconds(120))" in api
    assert "Timeout.InfiniteTimeSpan" in api


def test_missing_chunk_probe_preserves_typed_auth_errors():
    api = read("apps/recorder-agent/AgentApiClient.cs")
    probe = api.split("private async Task<IReadOnlyList<int>?> ReadMissingChunksAsync", 1)[1].split("private async Task<HttpResponseMessage> SendWithRetryAsync", 1)[0]
    assert 'await EnsureSuccessAsync(response, "SERVER_UNAVAILABLE")' in probe
    assert "if (!response.IsSuccessStatusCode) return null" not in probe


def test_desktop_processing_tracks_transcript_reprocess_jobs():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    assert "private static bool IsTranscriptJob(DesktopJob job)" in view_model
    assert 'string.Equals(job.Type, "TRANSCRIBE_REPROCESS"' in view_model
    assert view_model.count("jobs.Where(IsTranscriptJob)") >= 2


def test_summary_rebuild_is_blocked_explicitly_when_qwen_is_disabled():
    mapper = read("apps/desktop/WhisperX.Atom.Desktop/Services/UiStatusMapper.cs")
    meetings = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/MeetingsViewModel.cs")
    summaries = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/SummariesViewModel.cs")
    assert "IsQwenDisabled" in mapper
    assert "SummaryDisabledMessage" in mapper
    assert meetings.count("UiStatusMapper.IsQwenDisabled(readiness)") == 1
    assert summaries.count("UiStatusMapper.IsQwenDisabled(readiness)") >= 2
    assert "QueueSummaryRebuildAsync" in meetings and "QueueSummaryRebuildAsync" in summaries


def test_recorder_ipc_has_bounded_control_response_timeout():
    client = read("apps/desktop/WhisperX.Atom.Desktop/AgentPipeClient.cs")
    assert "ControlRequestTimeout = TimeSpan.FromSeconds(8)" in client
    assert "timeout.CancelAfter(ControlRequestTimeout)" in client
    assert "ReadLineAsync(requestCancellation)" in client
    assert 'RecorderIpcException("RECORDER_IPC_TIMEOUT"' in client

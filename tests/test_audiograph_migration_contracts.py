from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_audio_contracts_define_engine_selection_and_first_frame_boundary():
    contracts = read("apps/recorder-agent/AudioContracts.cs")
    assert "IAudioCaptureEngine" in contracts
    assert "IAudioDeviceCatalog" in contracts
    assert "AudioSelectionMode" in contracts and "Default" in contracts and "Fixed" in contracts
    assert "AudioEngineKind" in contracts and "LegacyWasapi" in contracts and "AudioGraph" in contracts
    assert "ChannelReader<AudioFrame> Frames" in contracts
    assert "48 kHz, mono, PCM16" in contracts


def test_audiograph_host_uses_user_session_engine_and_separate_pipe():
    project = read("apps/recorder-host/WhisperX.Atom.Recorder.Host.csproj")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    engine = read("apps/recorder-host/AudioGraphCaptureEngine.cs")
    catalog = read("apps/recorder-host/AudioGraphDeviceCatalog.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    assert "WhisperX.Atom.Recorder.Core.csproj" in project
    assert "AudioGraph" in engine and "AudioGraph.CreateAsync" in engine
    assert "CreateDeviceInputNodeAsync" in engine and "AUDIO_NO_FRAMES" in engine
    assert "DeviceInformation.CreateWatcher" in catalog
    assert "GetAudioCaptureSelector" in catalog
    assert "WhisperXAtomRecorderHost" in runtime
    assert "WhisperXAtomAgent" in protocol
    assert "AudioGraphHost" in protocol and "ForCurrentProcess" in protocol
    legacy = read("apps/recorder-agent/LegacyWasapiCaptureEngine.cs")
    assert "LegacyWasapiCaptureEngine" in legacy
    assert "WasapiCapture" in legacy and "AudioSampleFormatResolver.Resolve" in legacy


def test_audiograph_queue_is_bounded_and_overrun_is_explicit():
    engine = read("apps/recorder-host/AudioGraphCaptureEngine.cs")
    assert "Channel.CreateBounded<AudioFrame>" in engine
    assert "AUDIO_PIPELINE_OVERRUN" in engine
    assert "TryWrite(audioFrame)" in engine


def test_device_discovery_is_incremental_and_not_clear_add_polling():
    catalog = read("apps/recorder-host/AudioGraphDeviceCatalog.cs")
    assert "Added" in catalog and "Updated" in catalog and "Removed" in catalog
    assert "DEFAULT_DEVICE_CHANGED" in catalog
    assert "EnumerationCompleted" in catalog
    assert "_devices.Clear()" not in catalog.split("public ValueTask DisposeAsync", 1)[0]
    assert "FindAllAsync" in catalog


def test_feature_flag_and_user_scoped_host_configuration_are_present():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    resolver = read("apps/recorder-agent/RecorderRuntimeResolver.cs")
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/AgentPipeClient.cs")
    api = read("apps/recorder-agent/AgentApiClient.cs")
    script = read("scripts/start-recorder-host.ps1")
    assert "AUDIO_CAPTURE_ENGINE" in resolver
    assert "ForCurrentProcess" in desktop
    assert "DataProtectionScope.CurrentUser" in api
    assert "ATOM_AGENT_DPAPI_SCOPE" in script and "CURRENT_USER" in script
    assert "WhisperX.Atom.Recorder.Host.csproj" in script
    assert "WhisperXAtomRecorderHost" in script
    host_runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "RecorderHostPipeSecurity.CreateServer" in host_runtime
    assert "PipeAccessRights.FullControl" in read("apps/recorder-host/RecorderHostPipeSecurity.cs")


def test_desktop_runtime_resolver_defaults_to_audiograph_and_keeps_legacy_explicit():
    resolver = read("apps/recorder-agent/RecorderRuntimeResolver.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    app = read("apps/desktop/WhisperX.Atom.Desktop/App.xaml.cs")

    assert 'return new RecorderRuntimeSelection(AudioGraph, "RELEASE_DEFAULT")' in resolver
    assert 'environmentOverride ?? Environment.GetEnvironmentVariable(EnvironmentVariable)' in resolver
    assert 'ReadConfiguredEngine(machineConfigPath ?? MachineConfigPath)' in resolver
    assert "InitializeForDesktop" in app
    assert "RecorderRuntimeResolver.Current.PipeName" in protocol
    assert "LEGACY_WASAPI" in resolver


def test_release_package_contains_recorder_host_and_pinned_tools():
    publish = read("scripts/publish-desktop.ps1")
    installer = read("apps/desktop/Installer/WhisperXAtom.iss")
    install_service = read("apps/desktop/Installer/Install-Service.ps1")
    host_user_config = read("apps/desktop/Installer/Configure-RecorderHostUser.ps1")

    assert "WhisperX.Atom.Recorder.Host.csproj" in publish
    assert '$recorderHostOut = Join-Path $output "RecorderHost"' in publish
    assert "ffmpeg-manifest.json" in publish
    assert "RecorderHost\\*" in installer
    assert "Configure-RecorderHostUser.ps1" in installer
    assert "-RecorderHostDirectory" in installer
    assert 'captureEngine = "AUDIOGRAPH"' in install_service
    assert "StartupType Manual" in install_service
    assert "Test-PinnedFfmpegPayload" in install_service
    assert "PINNED_FFMPEG_CHECKSUM_MISMATCH" in install_service
    assert "WHISPERX_RECORDER_HOST_EXE" in host_user_config


def test_desktop_local_first_start_and_profile_migration_are_explicit():
    contracts = read("apps/desktop/WhisperX.Atom.Desktop/Services/FrontendContracts.cs")
    pipe = read("apps/desktop/WhisperX.Atom.Desktop/Services/RecorderPipeService.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    host = read("apps/recorder-host/RecorderHostRuntime.cs")

    assert "bool localOnly = false" in contracts
    assert "new { title, meetingId, ownerUserId, localOnly, acousticProfile }" in pipe
    assert "_services.RecordingCommands.StartAsync(title, ownerUserId)" in view_model
    command_service = read("apps/desktop/WhisperX.Atom.Desktop/Services/RecordingCommandService.cs")
    assert "localOnly: false" in command_service
    assert "CreateMeetingAsync" not in view_model.split("public async Task<bool> StartRecordingAsync", 1)[1].split("public Task<bool> PauseAsync", 1)[0]
    assert 'normalized is "ROOM" or "ONLINE" or "MIC_ONLY" or "SYSTEM_ONLY"' in view_model
    assert 'new("ONLINE", "Онлайн — микрофон + система")' in view_model
    assert 'SystemAudioCaptureEngine' in host


def test_desktop_start_uses_host_preflight_default_and_background_delivery():
    host = read("apps/recorder-host/RecorderHostRuntime.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")

    assert "public async Task<AgentIpcResponse> PreflightAsync" in host
    assert '"PREFLIGHT" => await _runtime.PreflightAsync' in host
    assert "new AgentPreflightResult" in host
    assert "BACKEND_NOT_CONFIGURED_RECORDING_CAN_START_OFFLINE" in host
    assert "SERVER_UNAVAILABLE_RECORDING_CAN_START_OFFLINE" in host
    assert "await _engine.SelectDeviceAsync(selectionMode, _storage.MicrophoneDeviceId" in host
    assert "EffectiveMicrophoneDeviceId" in protocol
    assert "MicrophoneCaptureReady: microphoneReady" in host
    assert "SystemAudioCaptureReady: systemReady" in host
    assert "health.DeviceWatcherReady && health.AudioGraphReady" in view_model
    assert 'health.CaptureEngine, "LEGACY_WASAPI"' in view_model
    start = view_model.split("public async Task<bool> StartRecordingAsync", 1)[1].split("public Task<bool> PauseAsync", 1)[0]
    assert "_services.RecordingCommands.StartAsync(title, ownerUserId)" in start
    assert "CreateMeetingAsync" not in start and "CancelMeetingAsync" not in start


def test_legacy_naudio_selection_is_not_persisted_as_an_audiograph_device():
    user_config = read("apps/desktop/Installer/Configure-RecorderHostUser.ps1")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")

    assert "$isAudioGraphDeviceId" in user_config
    assert "$existingDevice.StartsWith(" in user_config
    assert "userReselectRequired = $requiresReselect" in user_config
    assert "SetUserReselectRequired(true)" in runtime
    assert "SetAudioDevices(null, _storage.SystemAudioDeviceId)" in runtime
    assert "PersistCurrentConfigurationAsync" in runtime


def test_audiograph_microphone_selection_ignores_legacy_system_audio_field():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "_systemAudioDeviceId = settings.SystemAudioDeviceId" in view_model
    assert "SelectSystemDeviceAsync" in runtime
    assert 'Error("AUDIOGRAPH_SYSTEM_AUDIO_DEFERRED"' not in runtime


def test_audiograph_health_and_desktop_preserve_live_media_time():
    engine = read("apps/recorder-host/AudioGraphCaptureEngine.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    assert "public long CurrentMediaTimeMs" in engine
    assert "MediaTimeMs: _sessionId is null ? null : (_writer is not null ? _engine.CurrentMediaTimeMs : _systemEngine.CurrentMediaTimeMs)" in runtime
    assert "if (response.MediaTimeMs is long mediaTimeMs)" in view_model
    assert "resetting the timer to 00:00:00" in view_model


def test_server_origin_update_does_not_fail_on_capability_warning():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    update = runtime.split("public async Task<AgentIpcResponse> UpdateServerUrlAsync", 1)[1].split(
        "public async Task<AgentIpcResponse> SetArchiveRootAsync", 1
    )[0]
    assert "return health with { Ok = true, Error = null }" in update


def test_audiograph_device_subscription_uses_a_dedicated_long_lived_pipe():
    host = read("apps/recorder-host/RecorderHostRuntime.cs")
    desktop_client = read("apps/desktop/WhisperX.Atom.Desktop/AgentPipeClient.cs")
    recorder_service = read("apps/desktop/WhisperX.Atom.Desktop/Services/RecorderPipeService.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")

    assert "StreamDeviceEventsAsync" in host
    assert '"SUBSCRIBE_AUDIO_DEVICE_EVENTS" => await HealthAsync' not in host
    assert "IAsyncEnumerable<AgentIpcResponse> SubscribeAsync" in desktop_client
    assert 'SubscribeAsync("SUBSCRIBE_AUDIO_DEVICE_EVENTS"' in recorder_service
    assert "DeviceSubscriptionLoopAsync" in view_model


def test_audiograph_diagnostics_and_acceptance_scripts_are_redacted():
    probe = read("scripts/probe-audiograph-runtime.ps1")
    acceptance = read("scripts/acceptance-audiograph-local-recording.ps1")
    assert "artifacts\\audio-runtime\\audiograph-report.json" in probe
    assert "AUDIOGRAPH_FIRST_FRAME_READY" in probe
    assert "audioContentIncluded = $false" in probe
    assert "AUDIOGRAPH_LOCAL_RECORDING" in acceptance
    assert "localOnly = $true" in acceptance
    assert "transcriptIncluded = $false" in acceptance
    installer = read("scripts/install-recorder-host.ps1")
    assert "WHISPERX_RECORDER_HOST_EXE" in installer
    assert "ffmpeg.exe" in installer and "ffprobe.exe" in installer


def test_audiograph_probe_preserves_attempt_diagnostics_and_duration_mapping():
    engine = read("apps/recorder-host/AudioGraphCaptureEngine.cs")
    contracts = read("apps/recorder-agent/AudioContracts.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    probe = read("scripts/probe-audiograph-runtime.ps1")

    assert "AudioGraphAttemptDiagnostics" in contracts
    assert "AttemptDiagnostics = LastAttemptDiagnostics" in engine
    assert "GraphCreateAttempted" in engine and "InputNodeCreateAttempted" in engine
    assert "buffer.Length" in engine
    assert "reference.As<IMemoryBufferByteAccess>()" in engine
    assert "((IMemoryBufferByteAccess)reference)" not in engine
    assert "AUDIO_BUFFER_INTEROP_FAILED" in engine
    assert "InvalidCastException" in engine
    assert "_attempt.EmptyFrameCount++" in engine
    assert "_attempt.NonEmptyFrameCount++" in engine
    assert "if (!_probeMode)" in engine
    assert "NormalizeToPcm16" in engine
    assert "FLOAT32_TO_PCM16" in engine
    assert "AUDIO_FORMAT_UNSUPPORTED" in engine
    assert "TimeSpan.FromMilliseconds(boundedDurationMs)" in runtime
    assert "durationMs = $Seconds * 1000" in probe
    assert "AUDIOGRAPH_QUANTUM_READY" in probe
    assert "AUDIOGRAPH_FIRST_FRAME_READY" in probe

    acceptance = read("scripts/acceptance-audiograph-local-recording.ps1")
    assert "[string]$DeviceId = \"\"" in acceptance
    assert "AUDIOGRAPH_FIXED_DEVICE_MISMATCH" in acceptance
    assert "probePayload.deviceId = $DeviceId" in acceptance


def test_audiograph_normalizes_observed_float_frames_instead_of_trusting_pcm_metadata():
    engine = read("apps/recorder-host/AudioGraphCaptureEngine.cs")
    contracts = read("apps/recorder-agent/AudioContracts.cs")
    assert "NormalizeFrame(nativeBytes, _outputEncodingProperties, _graph?.SamplesPerQuantum)" in engine
    assert "expectedFloatLength" in engine and "expectedPcmLength" in engine
    assert "BitConverter.ToSingle(nativeBytes" in engine
    assert '"FLOAT32_TO_PCM16"' in engine
    assert "AUDIO_BUFFER_FORMAT_MISMATCH" in engine
    assert "NonFiniteSampleCount" in contracts
    assert "ObservedBytesPerSample" in contracts
    assert "FormatIntegrityVerified" in contracts


def test_audiograph_health_exposes_effective_endpoint_signal_and_attempt_diagnostics():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    diagnostics = read("apps/desktop/WhisperX.Atom.Desktop/Services/ClientRuntimeDiagnostics.cs")
    assert "EffectiveMicrophoneDeviceName" in protocol
    assert 'MicrophoneSignalState = "UNKNOWN"' in protocol
    assert "LastAudioGraphAttempt" in protocol
    assert '"FORMAT_MISMATCH"' in runtime
    assert '"READY_NO_SIGNAL"' in runtime
    assert "RuntimeSid" in runtime and "WindowsSessionId" in runtime
    assert "health.Health?.LastAudioGraphAttempt" in diagnostics


def test_audiograph_waveform_uses_linear_peak_derived_from_normalized_pcm16():
    contracts = read("apps/recorder-agent/AudioContracts.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "PeakLinear" in contracts
    assert "Math.Pow(10d, peakDb / 20d)" in contracts
    assert "liveTelemetry.IsStale ? telemetry.PeakLinear : liveTelemetry.PeakLinear" in runtime
    assert "LiveTelemetryAsync" in runtime


def test_desktop_audio_graph_probe_is_rendered_by_microphone_test_and_selection_keeps_error_code():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "response.AudioGraphProbe is { } graph" in view_model
    assert "FormatAgentError(response)" in view_model
    assert "ErrorDetail: probe.ErrorDetail" in runtime
    assert "AudioGraphProbe: probe" in runtime


def test_audiograph_writer_propagates_failures_and_keeps_encoding_off_capture_consumer():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    engine = read("apps/recorder-host/AudioGraphCaptureEngine.cs")
    contracts = read("apps/recorder-agent/AudioContracts.cs")
    probe = read("scripts/probe-audiograph-runtime.ps1")

    writer = runtime.split("internal sealed class AudioGraphSessionWriter", 1)[1]
    append = writer.split("private async Task AppendAsync", 1)[1].split("private Task EnsureChunkAsync", 1)[0]
    complete = writer.split("private async Task CompleteChunkAsync", 1)[1].split("private async Task EncodeChunkAsync", 1)[0]

    assert "catch (Exception ex)" in writer
    assert "throw;" in writer
    assert "ProcessEncodingAsync" in writer
    assert "RawChunkWorkItem" in writer
    assert "FlacEncoder.Encode" not in complete
    assert "Stopwatch.GetTimestamp() - _lastDurabilityCheckpointTimestamp" in append
    assert "FileOptions.WriteThrough" not in writer
    assert "Flush(flushToDisk: true)" in writer
    assert "await writer.StartAsync(cancellationToken)" in runtime
    assert "Prepare the first durable raw chunk before AudioGraph" in writer
    assert "QueueCapacity = 256" in engine
    assert 'message.Contains("AUDIO_DEVICE_UNAVAILABLE"' in engine
    assert 'return "AUDIO_DEVICE_UNAVAILABLE"' in engine
    assert '"PCM_S16LE", 16, "WRITING"' in writer
    assert '"PCM_S16LE", 16, "RAW_READY"' in writer
    encoder = read("apps/recorder-agent/FlacEncoder.cs")
    assert 'string.Equals(encoding, "PCM16"' in encoder
    assert 'var trackId = $"room-microphone-{sessionId}"' in runtime
    assert 'UNIQUE(track_id, sequence)' in runtime
    assert "RequestedSamplesPerQuantum" in contracts
    assert "_attempt.RequestedSamplesPerQuantum" in engine
    assert "_graph.SamplesPerQuantum" in engine
    assert "requestedSamplesPerQuantum" in probe
    assert "EncoderQueueCapacity = 8" in writer
    assert "Channel.CreateBounded<RawChunkWorkItem>" in writer
    assert "FullMode = BoundedChannelFullMode.Wait" in writer


def test_audiograph_stop_is_local_first_and_delivery_is_backgrounded():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    delivery = read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    acceptance = read("scripts/acceptance-audiograph-local-recording.ps1")

    stop = runtime.split("public async Task<AgentIpcResponse> StopAsync", 1)[1].split("public async Task<AgentIpcResponse> ProbeAsync", 1)[0]
    assert "FinalizeLocalAsync" in stop
    assert "_delivery.RunAsync(sessionId" not in stop
    assert "LOCAL_READY" in delivery
    assert "PENDING_SERVER" in delivery
    assert 'Invoke-HostCommand "SELECT_AUDIO_DEVICE"' in acceptance
    assert "[switch]$StopHost" in acceptance
    assert "if ($hostWasStarted -and $StopHost)" in acceptance


def test_recorder_host_syncs_real_pinned_ffmpeg_over_stale_stub():
    start_script = read("scripts/start-recorder-host.ps1")
    assert "function Sync-PinnedTool" in start_script
    assert "$destinationInfo.Length -ne $sourceInfo.Length" in start_script
    assert "Sync-PinnedTool $sourceFfmpeg $bundledFfmpeg" in start_script


def test_shared_spool_acl_is_granted_to_installing_user():
    installer = read("apps/desktop/Installer/Install-Service.ps1")
    assert "AGENT_DATA_ROOT_ACL_FAILED" in installer
    assert "(OI)(CI)M" in installer
    assert "icacls.exe" in installer


def test_installer_keeps_the_existing_user_host_installation_id():
    installer = read("apps/desktop/Installer/Install-Service.ps1")
    assert "Get-UserAgentConfigPathForSid" in installer
    assert "Get-InstallationIdFromConfig -Paths @($userAgentConfigPath, $machineConfigPath, $agentConfigPath)" in installer
    assert "ProfileList\\$Sid" in installer


def test_runtime_config_and_legacy_standby_preserve_the_audiograph_default():
    resolver = read("apps/recorder-agent/RecorderRuntimeResolver.cs")
    config_writer = read("scripts/write-client-config.ps1")
    bootstrap = read("apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs")
    service = read("apps/recorder-agent/Program.cs")
    script_resolver = read("scripts/Resolve-RecorderRuntime.ps1")

    assert "environmentOverride ?? Environment.GetEnvironmentVariable(EnvironmentVariable)" in resolver
    assert "var config = MachineServerConfig.Load(path)" in resolver
    assert "config.SchemaVersion != 2" in resolver
    assert "schemaVersion = 2" in config_writer
    assert "installationId = $installationId" in config_writer
    assert "audioConfigurationVersion = 2" in config_writer
    assert "services.RecorderService.StartAsync" in bootstrap
    assert "Start/verify the current-user Host before asking" in bootstrap
    assert "Legacy Recorder Service is in standby" in service
    assert "RELEASE_DEFAULT" in script_resolver
    assert "WhisperXAtomRecorderHost" in script_resolver
    assert "127.0.0.1:0" in config_writer
    assert "192.168.2.194" not in config_writer


def test_recorder_host_health_exposes_identity_for_desktop_bootstrap():
    host = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "InstallationId: _api.InstallationId" in host
    assert "AgentId: _api.AgentId" in host
    assert "ServerConnectionState: _api.ServerConnectionState" in host
    assert "LastHeartbeatAtUtc: _api.LastHeartbeatAtUtc" in host


def test_desktop_opens_repairable_shell_when_recorder_host_is_unavailable():
    login = read("apps/desktop/WhisperX.Atom.Desktop/LoginWindow.xaml.cs")
    app = read("apps/desktop/WhisperX.Atom.Desktop/App.xaml.cs")
    assert "await _authenticated(bootstrap)" in login
    assert "Settings can recover it" in login
    assert "if (!bootstrap.RecorderAvailable)" in app
    assert "ShowMainWindow();" in app.split("if (!bootstrap.RecorderAvailable)", 1)[1]


def test_summary_rebuild_returns_a_full_job_for_terminal_tracking():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")

    rebuild = api.split('app.MapPost("/api/meetings/{id:guid}/summary/rebuild"', 1)[1].split('app.MapGet("/api/meetings/{id:guid}/decisions"', 1)[0]
    assert "database.GetJobAsync(jobId.Value)" in rebuild
    assert "Results.Accepted($\"/api/jobs/{job.Id}\", job)" in rebuild
    assert "new { jobId }" not in rebuild
    assert "string? ErrorCode = null" in api
    assert "pipeline_correlation_id" in api
    assert "string? ErrorCode = null" in client
    assert "bool Retryable" in client


def test_host_process_guard_uses_canonical_spool_identity_and_script_rejects_stale_hosts():
    guard = read("apps/recorder-host/RecorderHostProcessGuard.cs")
    launcher = read("scripts/start-recorder-host.ps1")

    assert "ATOM_AGENT_DATA_ROOT" in guard
    assert "SHA256.HashData" in guard
    assert "$anyHosts" in launcher
    assert "RECORDER_HOST_BUILD_MISMATCH" in launcher
    assert "RECORDER_HOST_DUPLICATE" in launcher

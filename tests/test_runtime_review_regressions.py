from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_host_releases_installation_lease_when_start_fails_before_capture():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    start = runtime.split("public async Task<AgentIpcResponse> StartAsync", 1)[1].split(
        "public async Task<AgentIpcResponse> StopAsync", 1
    )[0]
    assert "await StopCoreAsync(CancellationToken.None)" in start
    assert "_runtimeLease?.Dispose()" in start
    assert "RECORDER_RUNTIME_LEASE_HELD" in read("apps/recorder-agent/RecorderRuntimeLease.cs")


def test_host_ipc_maps_acl_denial_and_uses_exact_configured_user_sid():
    client = read("apps/desktop/WhisperX.Atom.Desktop/AgentPipeClient.cs")
    controller = read("apps/desktop/WhisperX.Atom.Desktop/Services/RecorderServiceController.cs")
    security = read("apps/recorder-host/RecorderHostPipeSecurity.cs")
    assert client.count("UnauthorizedAccessException") >= 2
    assert "RECORDER_IPC_ACCESS_DENIED" in client
    assert "RECORDER_IPC_ACCESS_DENIED" in controller
    assert "ATOM_AGENT_ALLOWED_SID" in security
    assert "allowed-user.sid" in security


def test_host_health_advertises_build_identity_and_concurrent_ipc_capabilities():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "CurrentBuildIdentity" in protocol
    assert "CONCURRENT_REQUESTS" in protocol
    assert "DEVICE_EVENT_STREAM" in protocol
    assert "RuntimeBuildIdentity: AgentIpcProtocol.CurrentBuildIdentity" in runtime
    assert "AgentIpcProtocol.ConcurrentRequestsCapability" in runtime
    assert "AgentIpcProtocol.DeviceEventStreamCapability" in runtime


def test_desktop_does_not_open_device_subscription_without_host_capability():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    assert "_deviceEventStreamSupported" in view_model
    assert "RecorderRuntimeMode.IsAudioGraph && _deviceEventStreamSupported" in view_model
    assert "RECORDER_HOST_UPDATE_REQUIRED" in view_model


def test_desktop_maps_legacy_device_id_to_default_for_audiograph():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    assert "old NAudio identity" in view_model
    assert "_microphoneDeviceId = null" in view_model
    assert "health.CaptureDevices" in view_model


def test_installer_requires_safe_idle_host_shutdown_before_update():
    installer = read("apps/desktop/Installer/Install-Service.ps1")
    assert "RECORDER_HOST_UPDATE_BLOCKED_ACTIVE_RECORDING" in installer
    assert "RECORDER_HOST_UPDATE_RESTART_REQUIRED" in installer
    assert "SHUTDOWN" in installer
    assert "Prepare-RecorderHostUpdate" in installer


def test_desktop_distinguishes_running_host_from_unresponsive_pipe():
    controller = read("apps/desktop/WhisperX.Atom.Desktop/Services/RecorderServiceController.cs")
    assert "InspectHostProcess" in controller
    assert "RECORDER_HOST_PIPE_UNRESPONSIVE" in controller
    assert "RECORDER_HOST_NOT_RUNNING" in controller
    assert "before.Exists" in controller
    assert "RECORDER_HOST_UPDATE_REQUIRED" in controller
    assert "RECORDER_HOST_UPDATE_RESTART_REQUIRED" in controller
    assert "staleBuild" in controller


def test_host_launcher_fails_when_existing_process_owns_unresponsive_pipe():
    launcher = read("scripts/start-recorder-host.ps1")
    assert "Get-ProductVersion" in launcher
    assert "expectedBuild" in launcher
    assert "RECORDER_HOST_UPDATE_RESTART_REQUIRED" in launcher
    assert "RECORDER_HOST_PIPE_UNRESPONSIVE" in launcher
    assert "No duplicate Host was started" in launcher


def test_host_file_logging_cannot_block_ipc_startup_when_log_directory_is_denied():
    logger = read("apps/recorder-host/RecorderHostFileLoggerProvider.cs")
    assert "private readonly bool _enabled" in logger
    assert "_enabled = false" in logger
    assert "if (!_enabled) return;" in logger


def test_failed_device_selection_restores_engine_and_confirmed_settings():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    selection = runtime.split("public async Task<AgentIpcResponse> SelectDeviceAsync", 1)[1].split(
        "public async Task<AgentIpcResponse> PauseAsync", 1
    )[0]
    assert "previousReselectRequired" in selection
    assert "configurationCommitted" in selection
    assert "await RestoreEngineSelectionAsync(previous)" in selection
    assert "_storage.SetAudioDevices(previous, null)" in selection


def test_runtime_snapshot_requires_authentication_and_preflight_uses_connected_state():
    snapshot = read("apps/desktop/WhisperX.Atom.Desktop/Services/ProductRuntimeSnapshot.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    can_record = snapshot.split("var canRecord", 1)[1].split("var canUpload", 1)[0]
    assert 'map["Authentication"].Status' in can_record
    assert '"CONNECTED"' in runtime
    assert 'ServerConnectionState, "READY"' not in runtime


def test_reselect_requirement_is_not_cleared_during_host_initialization():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    initialize = runtime.split("public async Task InitializeAsync", 1)[1].split(
        "public async Task<AgentIpcResponse> HealthAsync", 1
    )[0]
    assert "var userReselectRequired = _storage.UserReselectRequired" in initialize
    assert "if (!userReselectRequired)" in initialize


def test_device_event_subscription_does_not_own_the_only_control_pipe_slot():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    pipe_security = read("apps/recorder-host/RecorderHostPipeSecurity.cs")
    assert "HandleConnectionAsync(pipe, stoppingToken)" in runtime
    assert "await HandleAsync(pipe, stoppingToken)" not in runtime.split(
        "protected override async Task ExecuteAsync", 1
    )[1].split("private async Task HandleConnectionAsync", 1)[0]
    assert "MaxServerInstances" in pipe_security


def test_configure_without_device_preserves_pending_reselection():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    configure = runtime.split("public async Task<AgentIpcResponse> ConfigureAsync", 1)[1].split(
        "public async Task<AgentIpcResponse> UpdateServerUrlAsync", 1
    )[0]
    assert "if (!_storage.UserReselectRequired)" in configure
    assert "_storage.SetUserReselectRequired(false);" in configure


def test_legacy_service_owns_the_same_installation_runtime_lease():
    program = read("apps/recorder-agent/Program.cs")
    assert "RecorderRuntimeLease.Acquire(api.InstallationId)" in program
    assert "RECORDER_RUNTIME_LEASE_HELD" in program
    assert "if (_runtimeLease is not null)" in program


def test_host_recovery_initialization_is_inside_the_installation_lease():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    initialize = runtime.split("public async Task InitializeAsync", 1)[1].split(
        "public async Task<AgentIpcResponse> HealthAsync", 1
    )[0]
    assert "RecorderRuntimeLease.Acquire(_api.InstallationId)" in initialize
    assert "await _recovery.RecoverAsync" in initialize
    assert "using var initializationLease" in initialize


def test_v1_machine_config_cannot_select_recorder_engine():
    resolver = read("apps/recorder-agent/RecorderRuntimeResolver.cs")
    script = read("scripts/Resolve-RecorderRuntime.ps1")
    assert "config.SchemaVersion != 2" in resolver
    assert "config.AudioConfiguration is null" in resolver
    assert "$config.schemaVersion -eq 2" in script
    assert "audioVersion -eq 2" in script


def test_offline_startup_restores_bootstrap_before_showing_main_window():
    app = read("apps/desktop/WhisperX.Atom.Desktop/App.xaml.cs")
    offline = app.split("else if (_services.Backend.CanUseOffline)", 1)[1].split(
        "else\n", 1
    )[0]
    assert "AgentBootstrap.EnsureAgentReadyAsync" in offline
    assert offline.index("EnsureAgentReadyAsync") < offline.index("ShowMainWindow")
    coordinator = read("apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs")
    assert "services.Backend.AuthState == DesktopAuthState.Offline" in coordinator
    assert "offlineEligible" in coordinator


def test_legacy_service_mode_is_explicit_and_pipe_is_gated_until_lease():
    program = read("apps/recorder-agent/Program.cs")
    pipe = read("apps/recorder-agent/AgentPipeHost.cs")
    installer = read("apps/desktop/Installer/Install-Service.ps1")
    assert "--runtime LEGACY_WASAPI" in installer
    assert "RecorderServiceRuntime" in program
    assert "SetActive(true)" in program and "SetActive(false)" in program
    assert "!RecorderServiceRuntime.IsActive" in pipe


def test_agent_activity_uses_heartbeat_freshness_not_stored_online_status():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/AgentsViewModel.cs")
    assert "ActiveHeartbeatWindow = TimeSpan.FromSeconds(90)" in store
    assert "public bool IsActive" in store
    assert "public string EffectiveStatus" in store
    assert "agent.IsActive" in desktop
    assert "agent.Status" not in desktop.split("OnlineCountText", 1)[1].split(";", 1)[0]


def test_installation_id_is_persisted_before_first_bootstrap():
    client = read("apps/recorder-agent/AgentApiClient.cs")
    assert "installationIdWasGenerated" in client
    assert "PersistInstallationIdentity(config)" in client
    assert 'throw new InvalidOperationException("INSTALLATION_ID_PERSIST_FAILED"' in client


def test_gpu_worker_runtime_identity_and_heartbeat_healthcheck_are_pinned_in_compose():
    compose = read("compose.dev.yml")
    healthcheck = read("workers/ml_worker/healthcheck.py")
    assert "WORKER_INSTANCE_ID: ${GPU_WORKER_INSTANCE_ID:-gpu-worker}" in compose
    assert "APP_VERSION: ${WHISPERX_RELEASE_VERSION:-dev}" in compose
    assert "last_seen_at" in healthcheck
    assert "cudaAvailable" in healthcheck

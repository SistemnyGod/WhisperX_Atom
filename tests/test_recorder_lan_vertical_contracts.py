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
    assert '"server_binding_pending"' in start
    assert "MaxServerInstances" in security
    assert "MaxServerInstances = 8" in protocol
    assert "SemaphoreSlim _commandGate" in host


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
    assert "AudioSampleFormatResolver.Resolve(format).FfmpegInput" in encoder
    assert "AudioSampleFormatResolver.Resolve(capture.WaveFormat)" in coordinator


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


def test_lan_start_guards_inherited_runnable_work_before_gpu_workers():
    startup = read("scripts/start-whisperx-lan-server.ps1")
    assert "STARTUP_QUEUE_GUARD_BLOCKED" in startup
    assert "outbox_messages WHERE published_at IS NULL" in startup
    assert 'postgres", "nats", "api", "tusd", "lan-gateway' in startup
    assert 'media-worker", "gpu-worker' in startup


def test_existing_bootstrap_does_not_rotate_agent_token():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    bootstrap = store.split("public async Task<AgentBootstrapResult?> BootstrapAgentAsync", 1)[1].split("public async Task<bool> AgentUserLinkedAsync", 1)[0]
    assert "return new AgentBootstrapResult(result, existing is null ? token : null)" in bootstrap
    assert "enrollment_hash=excluded.enrollment_hash" not in bootstrap


def test_desktop_blocks_deterministic_bootstrap_errors_but_allows_network_fallback():
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    assert "class DesktopApiException" in client
    assert "retryable" in client
    assert 'ex.StatusCode != 0 && !ex.Retryable' in view_model
    assert '"AGENT_USER_LINK_REQUIRED"' in view_model

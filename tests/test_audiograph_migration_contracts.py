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
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/AgentPipeClient.cs")
    api = read("apps/recorder-agent/AgentApiClient.cs")
    script = read("scripts/start-recorder-host.ps1")
    assert "AUDIO_CAPTURE_ENGINE" in protocol
    assert "ForCurrentProcess" in desktop
    assert "DataProtectionScope.CurrentUser" in api
    assert "ATOM_AGENT_DPAPI_SCOPE" in script and "CURRENT_USER" in script
    assert "WhisperX.Atom.Recorder.Host.csproj" in script
    assert "WhisperXAtomRecorderHost" in script
    host_runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "RecorderHostPipeSecurity.CreateServer" in host_runtime
    assert "PipeAccessRights.FullControl" in read("apps/recorder-host/RecorderHostPipeSecurity.cs")


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


def test_shared_spool_acl_is_granted_to_installing_user():
    installer = read("apps/desktop/Installer/Install-Service.ps1")
    assert "AGENT_DATA_ROOT_ACL_FAILED" in installer
    assert "(OI)(CI)M" in installer
    assert "icacls.exe" in installer

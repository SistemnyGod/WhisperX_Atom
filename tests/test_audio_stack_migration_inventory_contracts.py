from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_inventory_and_read_only_audit_are_present():
    inventory = read("docs/audio-stack-migration-inventory.md")
    audit = read("scripts/audit-audio-stack-migration.ps1")
    for column in ("FILE", "SYMBOL", "CATEGORY", "OLD_DEPENDENCY", "NEW_OWNER", "ACTION", "GATE"):
        assert column in inventory
    assert "currentHead" in audit
    assert "referenceCount" in audit
    assert "microphoneNaudio" in audit


def test_naudio_isolated_from_neutral_core_and_audiograph_host():
    core_project = read("apps/recorder-agent/WhisperX.Atom.Recorder.Core.csproj")
    service_project = read("apps/recorder-agent/WhisperX.Atom.Recorder.Service.csproj")
    host_project = read("apps/recorder-host/WhisperX.Atom.Recorder.Host.csproj")
    assert "PackageReference Include=\"NAudio\"" not in core_project
    assert "PackageReference Include=\"NAudio\"" not in host_project
    assert "NAudio" in service_project
    assert "LegacyWasapiDeviceProbe" in service_project


def test_core_frame_consumer_and_host_writer_preserve_first_durable_boundary():
    contracts = read("apps/recorder-agent/AudioContracts.cs")
    consumer = read("apps/recorder-agent/AudioFrameDurableConsumer.cs")
    writer = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "AudioStreamFormats" in contracts
    assert "FirstDurableWrite" in consumer
    assert "RunAsync" in consumer
    assert "await _worker.WaitAsync" in writer
    assert "final partial chunk is durable" in writer


def test_audio_frame_continuity_is_validated_before_durable_append():
    validator = read("apps/recorder-agent/AudioFrameContinuityValidator.cs")
    consumer = read("apps/recorder-agent/AudioFrameDurableConsumer.cs")
    host = read("apps/recorder-host/RecorderHostRuntime.cs")
    engine = read("apps/recorder-host/AudioGraphCaptureEngine.cs")

    for code in (
        "AUDIO_FRAME_GAP",
        "AUDIO_FRAME_OVERLAP",
        "AUDIO_FRAME_FORMAT_MISMATCH",
        "AUDIO_FRAME_SIZE_MISMATCH",
    ):
        assert code in validator
        assert code in engine
    assert consumer.index("_continuity.ValidateAndAdvance(frame)") < consumer.index("await durableWrite(frame)")
    assert "writer.CaptureFailed += OnCaptureFailed" in host
    assert "_engine.CaptureFailed += OnCaptureFailed" in host
    assert "ReferenceEquals(_writer, expectedWriter)" in host
    assert "PersistFailedLocalLifecycleAsync(sessionId, exception, failure.ErrorCode)" in host


def test_config_v2_and_exact_device_reselection_are_explicit():
    config = read("apps/recorder-agent/AudioConfigurationV2.cs")
    storage = read("apps/recorder-agent/AgentStorageSettings.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    engine = read("apps/recorder-host/AudioGraphCaptureEngine.cs")
    assert "audioConfigurationVersion" in config
    assert "userReselectRequired" in config
    assert "UserReselectRequired" in storage
    assert "exact DeviceInformation.Id match" in runtime
    assert "AUDIO_DEVICE_UNAVAILABLE" in engine


def test_ipc_v6_reports_compatibility_and_host_is_single_instance():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    legacy_host = read("apps/recorder-agent/AgentPipeHost.cs")
    host_runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    guard = read("apps/recorder-host/RecorderHostProcessGuard.cs")
    lease = read("apps/recorder-agent/RecorderRuntimeLease.cs")
    assert "Version = 6" in protocol
    assert "IPC_VERSION_INCOMPATIBLE" in legacy_host
    assert "MinimumSupportedProtocolVersion" in host_runtime
    assert "RECORDER_HOST_ALREADY_RUNNING" in read("apps/recorder-host/Program.cs")
    assert "ATOM_AGENT_DATA_ROOT" in guard
    assert "SHA256.HashData" in guard
    assert "Global\\\\WhisperXAtomRecorderRuntime" in lease


def test_runtime_diagnostics_are_redacted_and_engine_selectable():
    diagnostics = read("scripts/diagnose-runtime.ps1")
    snapshot = read("apps/desktop/WhisperX.Atom.Desktop/Services/ProductRuntimeSnapshot.cs")
    assert "CaptureEngine" in diagnostics
    assert "tokensIncluded = $false" in diagnostics
    assert "audioContentIncluded = $false" in diagnostics
    assert "CanRecord" in snapshot and "CanTranscribe" in snapshot and "CanSummarize" in snapshot
    assert "QWEN_OPTIONAL_DISABLED" in snapshot


def test_audiograph_does_not_silently_drop_system_audio_profiles():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert '"ONLINE" or "SYSTEM_ONLY"' in runtime
    assert "AUDIO_SYSTEM_AUDIO_DEFERRED" in runtime

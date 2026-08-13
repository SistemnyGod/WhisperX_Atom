from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_capture_state_waits_for_writer_accepted_first_packet():
    source = read("apps/recorder-agent/RecordingCoordinator.cs")
    capture = source.split("private sealed class CaptureTrack", 1)[1].split("internal sealed class PcmFlacChunkWriter", 1)[0]
    assert "_writer.Append" in capture
    assert capture.index("_writer.Append") < capture.index("_firstAudio.TrySetResult")
    assert "capture-start-requested" in source
    assert "first-audio-buffer-accepted" in source
    assert '"AUDIO_NO_DATA"' in read("apps/recorder-agent/AudioRuntimeProbe.cs")


def test_audio_probe_reports_stream_packets_bytes_latency_and_format():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    probe_runtime = read("apps/recorder-agent/AudioRuntimeProbe.cs")
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    probe = read("apps/recorder-agent/AudioDeviceProbe.cs")
    for field in ("StreamOpened", "StreamStarted", "PacketCount", "BytesReceived", "FirstPacketLatencyMs", "RawSampleFormat"):
        assert field in protocol
    for field in ("streamOpened", "streamStarted", "packetCount", "bytesReceived", "firstPacketLatencyMs", "normalizedFormat"):
        assert field in probe_runtime
    for field in ("ProcessUser", "ProcessSid", "WindowsSessionId", "DefaultMultimediaEndpointId", "DefaultCommunicationsEndpointId"):
        assert field in protocol
    for field in ("processUser", "processSid", "sessionId", "defaultMultimediaEndpointId", "defaultCommunicationsEndpointId"):
        assert field in probe_runtime
    assert "AudioDeviceProbe.IsReady" in coordinator
    assert "_storage.MicrophoneDeviceId" in coordinator and "_storage.SystemAudioDeviceId" in coordinator
    assert "READY_NO_SIGNAL" in probe


def test_all_supported_pcm_widths_are_measured_not_silently_ignored():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    probe_runtime = read("apps/recorder-agent/AudioRuntimeProbe.cs")
    for sample_format in ("RawAudioSampleFormat.Pcm16", "RawAudioSampleFormat.Pcm24", "RawAudioSampleFormat.Pcm32", "RawAudioSampleFormat.Float32"):
        assert sample_format in coordinator or sample_format in probe_runtime
    assert "AUDIO_FORMAT_UNSUPPORTED" in probe_runtime


def test_device_discovery_keeps_endpoint_identity_and_format_metadata():
    health = read("apps/recorder-agent/DeviceHealth.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    for field in ("IsDefaultConsole", "IsDefaultMultimedia", "IsDefaultCommunications", "SourceSubFormat", "ValidBitsPerSample", "NormalizedSampleFormat"):
        assert field in protocol
    assert "defaults.Console" in health and "defaults.Multimedia" in health and "defaults.Communications" in health
    assert "GetDefaultAudioEndpoint" in health
    assert "AudioSampleFormatResolver.Resolve(format)" in health


def test_controlled_probe_has_interactive_and_service_modes_without_sensitive_artifacts():
    runner = read("apps/recorder-agent/AudioRuntimeProbeRunner.cs")
    script = read("scripts/probe-audio-runtime.ps1")
    for text in ("mode == \"service\"", "mode = Read(args, \"mode\")", "interactive-user.json", "recorder-service.json", "comparison.json"):
        assert text in runner or text in script
    for text in ("secretsIncluded", "audioBytesIncluded", "transcriptIncluded"):
        assert text in script
    assert "UNDECIDED" in script


def test_desktop_reconciliation_does_not_clear_device_collection_or_write_back_selection():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/RecordingPage.xaml.cs")
    update = view_model.split("private static bool UpdateDevices", 1)[1].split("private static bool IsRequiredAudioReady", 1)[0]
    assert "target.Clear" not in update
    assert "IsDeviceListRefreshInProgress" in view_model
    assert "ViewModel?.IsDeviceListRefreshInProgress == true" in page
    assert "_confirmedMicrophoneDeviceId" in view_model
    assert "_confirmedSystemAudioDeviceId" in view_model


def test_standalone_recorder_install_stages_pinned_ffmpeg_payload():
    script = read("scripts/install-recorder-runtime.ps1")
    assert "vendor\\ffmpeg\\win-x64" in script
    assert "ffmpeg-manifest.json" in script
    assert "FFMPEG_CHECKSUM_MISMATCH" in script
    assert "Join-Path $publishRoot $name" in script

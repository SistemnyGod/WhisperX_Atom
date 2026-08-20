from pathlib import Path
from workers.media_worker import recording_assembly


ROOT = Path(__file__).resolve().parents[1]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def test_host_has_independent_render_loopback_source_and_track_writer():
    engine = read("apps/recorder-host/SystemAudioCaptureEngine.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    project = read("apps/recorder-host/WhisperX.Atom.Recorder.Host.csproj")

    assert "WasapiLoopbackCapture" in engine
    assert "DataFlow.Render" in engine
    assert "WASAPI_LOOPBACK_TO_PCM16_MONO" in engine
    assert '<PackageReference Include="NAudio" Version="2.2.1" />' in project
    assert 'trackId = $"system-audio-{sessionId}"' in runtime
    assert 'new AudioGraphSessionWriter(sessionId, trackId, "system-audio"' in runtime


def test_online_profile_starts_two_sources_without_mixing_channels():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    writer = runtime.split("internal sealed class AudioGraphSessionWriter", 1)[1]

    assert 'var needsSystemAudio = profile is "ONLINE" or "SYSTEM_ONLY";' in runtime
    assert "_engine.PrepareFrameChannel()" in runtime
    assert "_systemEngine.PrepareFrameChannel()" in runtime
    assert "await _engine.StartAsync" in runtime
    assert "await _systemEngine.StartAsync" in runtime
    assert "private readonly string _trackType" in writer
    assert "_trackType, \"PCM_S16LE\"" in writer


def test_system_audio_selection_is_persisted_and_fixed_endpoint_does_not_fallback():
    engine = read("apps/recorder-host/SystemAudioCaptureEngine.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")

    assert "SelectSystemDeviceAsync" in runtime
    assert "selectionMode == AudioSelectionMode.Fixed" in engine
    assert '"AUDIO_SYSTEM_AUDIO_UNAVAILABLE"' in engine
    initialization = runtime.split("var systemSelectionMode", 1)[1].split("_initialized = true", 1)[0]
    assert "Configured fixed system-audio endpoint is unavailable" in initialization
    assert "_storage.SetAudioDevices(_storage.MicrophoneDeviceId, null)" not in initialization
    assert "_systemAudioDeviceId = settings.SystemAudioDeviceId" in view_model
    assert "new { microphoneDeviceId, systemAudioDeviceId }" in read(
        "apps/desktop/WhisperX.Atom.Desktop/Services/RecorderPipeService.cs"
    )
    assert "_api.SetAudioDevicesAsync(" in runtime
    assert "_storage.SystemAudioDeviceId" in runtime


def test_system_audio_never_reuses_microphone_track_type():
    engine = read("apps/recorder-host/SystemAudioCaptureEngine.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")

    assert '"system-audio"' in runtime
    assert '"room-microphone"' in runtime
    assert "ToPcm16Mono48k" in engine
    assert "_trackType" in runtime


def test_host_advertises_the_additive_system_audio_capability_without_changing_ipc_v6():
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "public const int Version = 6" in protocol
    assert "IndependentSystemAudioTrackCapability" in protocol
    assert "IndependentSystemAudioTrackCapability" in runtime


def test_loopback_start_does_not_complete_the_writer_bound_channel():
    engine = read("apps/recorder-host/SystemAudioCaptureEngine.cs")
    start = engine.split("public async Task StartAsync", 1)[1].split("public Task PauseAsync", 1)[0]
    assert "Do not call StopAsync here" in start
    assert "if (!_frameChannelPrepared)" in start
    assert "_frames.Writer.TryComplete()" in start


def test_loopback_normalizer_resolves_extensible_subformat_instead_of_assuming_float32():
    engine = read("apps/recorder-host/SystemAudioCaptureEngine.cs")
    assert "AudioSampleFormatResolver.Resolve(format)" in engine
    assert "resolved.Kind == RawAudioSampleFormat.Float32" in engine
    assert "format.Encoding == WaveFormatEncoding.Extensible && bits == 32" not in engine


def test_media_manifest_keeps_room_and_system_tracks_separate_until_asr_selection():
    assembly = read("workers/media_worker/recording_assembly.py")
    media = read("workers/media_worker/media_worker.py")

    assert '"tracks_are_independent": True' in assembly
    assert '"track_files": _build_track_files(' in assembly
    assert '"relative_path": _relative_output_name(path, output_dir)' in assembly
    assert '"storage_key": _media_storage_key(path, output_dir)' in assembly
    assert '"source_kind": "derived_mix" if source.name == "mixed.flac" else "independent_track"' in assembly
    assert 'item["selected_for_asr"] = False' in assembly
    assert '"independent_tracks": assembly_result.get("track_files", [])' in media
    assert '"tracks_are_independent": bool(assembly_result.get("tracks_are_independent", False))' in media


def test_track_manifest_builder_emits_logical_keys_without_host_paths():
    tracks = [
        recording_assembly.Track("room", "room-microphone", (), device_name="Room"),
        recording_assembly.Track("system", "system-audio", (), device_name="Render"),
    ]
    paths = [Path("/data/assembled/job/track-room.flac"), Path("/data/assembled/job/track-system.flac")]
    timeline = [
        {"actual_duration_ms": 1000, "start_offset_ms": 0, "drift_ms": 0},
        {"actual_duration_ms": 980, "start_offset_ms": 5, "drift_ms": -20},
    ]
    methods = [{"method": "STREAM_COPY"}, {"method": "REENCODE_FALLBACK"}]
    result = recording_assembly._build_track_files(
        tracks, paths, timeline, methods, Path("/data/assembled/job"), paths[0]
    )

    assert [item["track_role"] for item in result] == ["room_microphone", "system_audio"]
    assert [item["storage_key"] for item in result] == [
        "assembled/job/track-room.flac",
        "assembled/job/track-system.flac",
    ]
    assert result[0]["selected_for_asr"] is True
    assert result[1]["selected_for_asr"] is False
    assert all(not Path(item["storage_key"]).is_absolute() for item in result)

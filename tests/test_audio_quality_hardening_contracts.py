from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_quality_analyzer_contains_release_thresholds_and_non_mutating_contract():
    text = read("apps/recorder-agent/AudioQualityAnalyzer.cs")
    assert "AudioQualityAssessment" in text
    assert "0.01" in text  # 1% critical clipping boundary
    assert "0.001" in text  # 0.1% bad clipping boundary
    assert "0.0001" in text  # 0.01% warning clipping boundary
    assert "snrDb < 12d" in text
    assert "Math.Abs(dcOffset)" in text or "dcOffset > 0.03" in text


def test_probe_and_ab_capability_are_additive_and_ab_is_explicit():
    contracts = read("apps/recorder-agent/AudioContracts.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "AudioQualityAssessment? Quality = null" in contracts
    assert "AUDIO_CAPTURE_AB_V1" in protocol
    assert '"RUN_AUDIO_CAPTURE_AB"' in runtime
    assert "AUDIO_AB_RECORDING_ACTIVE" in runtime
    assert "AUDIO_SYSTEM_AUDIO_DEVICE_LOST" in read("apps/recorder-host/SystemAudioCaptureEngine.cs")
    assert "SYSTEM_AUDIO_DEVICE_LOST" in runtime
    assert "NoiseWindowConfirmed" in contracts
    assert "LastProbePcm16" in read("apps/recorder-host/AudioGraphCaptureEngine.cs")
    assert "AudioGraphSha256 = graphHash" in runtime


def test_raw_diagnostic_capture_uses_polling_without_unbound_event_callback():
    raw = read("apps/recorder-host/WasapiRawDiagnosticCaptureEngine.cs")
    assert "AudioClientStreamFlags.None" in raw
    assert "AudioClientStreamFlags.EventCallback" not in raw


def test_ab_runtime_normalizes_duration_for_both_capture_engines():
    runtime = read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "minimumDurationSeconds" in runtime
    assert "Math.Max(durationSeconds, minimumDurationSeconds)" in runtime


def test_system_audio_stopped_callback_maps_invalidated_endpoint_to_device_loss():
    source = read("apps/recorder-host/SystemAudioCaptureEngine.cs")
    assert "AUDCLNT_E_DEVICE_INVALIDATED" in source
    assert 'RaiseFailure("AUDIO_SYSTEM_AUDIO_DEVICE_LOST"' in source


def test_system_audio_monitors_selected_endpoint_during_capture_without_event_storm():
    source = read("apps/recorder-host/SystemAudioCaptureEngine.cs")
    assert "StartSelectedDeviceMonitor" in source
    assert "MonitorSelectedDeviceAsync" in source
    assert "SameDevice" in source
    assert "LastSeenAtUtc" not in source.split("private static bool SameDevice", 1)[1].split("}", 1)[0]


def test_raw_capture_handles_wasapi_silent_packets_and_extensible_float():
    source = read("apps/recorder-host/WasapiRawDiagnosticCaptureEngine.cs")
    assert "AudioClientBufferFlags.Silent" in source
    assert "AudioSampleFormatResolver.Resolve(format)" in source
    assert "RawAudioSampleFormat.Float32" in source
    assert "sourceFrames <= 0" in source


def test_storage_watchdog_has_emergency_exactly_once_boundary():
    storage = read("apps/recorder-agent/StorageWatermark.cs")
    watchdog = read("apps/recorder-host/StorageCaptureWatchdog.cs")
    assert "PostProcessingReserveBytes" in storage
    assert "EvaluateDuringRecording" in storage
    assert "LOW_DISK_EMERGENCY" in watchdog or "LOW_DISK_EMERGENCY" in read("apps/recorder-host/RecorderHostRuntime.cs")
    assert "StopForLowDiskAsync" in watchdog
    assert "_lastLowDiskStoppedSessionId" in read("apps/recorder-host/RecorderHostRuntime.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")
    assert "stop_reason" in spool
    assert "stopped_automatically" in spool


def test_doctor_and_hardware_runner_are_fail_closed():
    doctor = read("scripts/doctor-server-bundle.ps1")
    runner = read("scripts/hardware-release-acceptance.ps1")
    assert "[ValidateSet('Quick','Release')]" in doctor
    assert "SERVER_DOCTOR_READY" in doctor
    assert "SUPERVISOR_HEALTH_TOKEN_MISSING" in doctor
    assert "RUNTIME_IDENTITY_MISSING" in doctor
    assert "Add-VerifiedModelCheck 'whisperx'" in doctor
    assert "Add-VerifiedModelCheck 'diarization'" in doctor
    assert "WHISPERX_MODEL_SHA256" in doctor and "DIARIZATION_MODEL_SHA256" in doctor
    assert "qwen3-8b\\Qwen3-8B-Q5_K_M.gguf" in doctor
    assert "[ValidateSet('Server','Client','Aggregate')]" in runner
    assert "schemaVersion = 1" in runner
    assert "sameIdentityEvidence" in runner
    assert "status = if ($failed.Count -eq 0) { 'PASSED' }" in runner
    ab = read("scripts/run-audio-capture-ab.ps1")
    assert "RUN_AUDIO_CAPTURE_AB" in ab
    assert "transcriptIncluded = $false" in ab

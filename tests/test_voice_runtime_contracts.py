from pathlib import Path

from workers.ml_worker.technical_events import build_technical_intervals, segment_technical_flags


ROOT = Path(__file__).resolve().parents[1]
VOICE_RUNTIME = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs").read_text(encoding="utf-8")
VOICE_CAPTURE = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceAudioCapture.cs").read_text(encoding="utf-8")
VOICE_CONTROLLER = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/VoiceHostController.cs").read_text(encoding="utf-8")
VOICE_LEASE = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntimeLease.cs").read_text(encoding="utf-8")
RECORDER_HOST_RUNTIME = (ROOT / "apps/recorder-host/RecorderHostRuntime.cs").read_text(encoding="utf-8")
LEGACY_AGENT_PIPE = (ROOT / "apps/recorder-agent/AgentPipeHost.cs").read_text(encoding="utf-8")
STATE_MACHINE = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceStateMachine.cs").read_text(encoding="utf-8")
BROKER = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs").read_text(encoding="utf-8")
BROKER_CLIENT = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceIntentBrokerClient.cs").read_text(encoding="utf-8")
SETTINGS_PAGE = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Pages/SettingsPage.xaml.cs").read_text(encoding="utf-8")
SETTINGS_VM = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/ViewModels/SettingsViewModel.cs").read_text(encoding="utf-8")
ML_PERSISTENCE = (ROOT / "workers/ml_worker/persistence.py").read_text(encoding="utf-8")
TECHNICAL_EVENTS = (ROOT / "workers/ml_worker/technical_events.py").read_text(encoding="utf-8")


def test_voice_host_lifecycle_and_startup_guard_are_explicit():
    assert "VoiceHostState.Starting" in VOICE_RUNTIME
    assert '"RECORDER_HOST_NOT_INITIALIZED"' in VOICE_RUNTIME
    assert "Environment.ProcessId" in VOICE_RUNTIME
    assert "BuildIdentity" in VOICE_RUNTIME


def test_voice_command_has_durable_pre_session_outbox_and_idempotent_replay():
    spool = (ROOT / "apps/recorder-agent/SpoolStore.cs").read_text(encoding="utf-8")
    assert "recording_pending_events" in RECORDER_HOST_RUNTIME
    assert "AddPendingEventAsync" in RECORDER_HOST_RUNTIME
    assert "AttachPendingEventAsync" in RECORDER_HOST_RUNTIME
    assert '"VOICE_EVENT" => await _runtime.RecordEventAsync' in RECORDER_HOST_RUNTIME
    assert "eventId" in VOICE_RUNTIME
    assert "response.LocalSessionId" in VOICE_RUNTIME
    assert "INSERT OR IGNORE INTO recording_events" in spool
    assert "AddPendingEventAsync" in LEGACY_AGENT_PIPE
    assert "targetSessionId" in LEGACY_AGENT_PIPE


def test_voice_host_uses_selected_endpoint_and_latest_audio_metrics():
    assert "GetDevice(deviceId)" in VOICE_CAPTURE
    assert "VoiceAudioTelemetry" in VOICE_CAPTURE
    assert "Rms" in VOICE_CAPTURE and "Clipping" in VOICE_CAPTURE
    assert "ArrayPool<byte>" in VOICE_CAPTURE
    assert "EffectiveMicrophoneDeviceId" in VOICE_CONTROLLER


def test_managed_host_is_program_files_only_and_build_checked():
    assert 'Environment.SpecialFolder.ProgramFiles' in VOICE_CONTROLLER
    assert "VOICE_HOST_BUILD_MISMATCH" in VOICE_CONTROLLER
    assert "CurrentBuildIdentity" in VOICE_CONTROLLER
    assert "_lifecycleGate" in VOICE_CONTROLLER
    assert "GetHealthAsync" in VOICE_CONTROLLER


def test_voice_host_lease_and_command_safety_are_explicit():
    assert "Global\\WhisperXAtomVoiceHost" in VOICE_LEASE
    assert "TryAcquire" in VOICE_LEASE
    assert 'normalizedCommand is not ("STATUS" or "DOCTOR" or "SHUTDOWN")' in VOICE_RUNTIME
    assert "command.Confidence < 0.70" in VOICE_RUNTIME
    assert "TimeSpan.FromSeconds(10)" in VOICE_RUNTIME
    assert "_speech.IsBusy" in VOICE_RUNTIME
    assert "TouchHeartbeat" in STATE_MACHINE


def test_broker_test_mode_does_not_call_recorder_and_returns_trace():
    assert '"EXECUTE_INTENT"' in BROKER
    assert 'if (testMode)' in BROKER
    assert 'RecorderState: "TEST_ONLY"' in BROKER
    assert "TraceId" in BROKER
    assert "ConnectWithRetryAsync" in BROKER_CLIENT


def test_server_builds_bounded_system_response_intervals():
    assert "def _technical_intervals" in ML_PERSISTENCE
    assert '"SYSTEM_RESPONSE_STARTED"' in TECHNICAL_EVENTS
    assert '"SYSTEM_RESPONSE_FINISHED"' in TECHNICAL_EVENTS
    assert "VOICE_TECHNICAL_EVENT_MAX_MS" in TECHNICAL_EVENTS
    assert '"TECHNICAL"' in TECHNICAL_EVENTS
    assert "is_hidden" in ML_PERSISTENCE


def test_ui_exposes_live_telemetry_timeout_and_trace():
    assert "TimeSpan.FromMilliseconds(750)" in SETTINGS_VM
    assert "VoiceLastTraceId" in SETTINGS_VM
    assert "VoiceBarsPanel" in SETTINGS_PAGE
    assert "FromMilliseconds(33)" in SETTINGS_PAGE


def test_system_response_intervals_hide_only_their_media_window():
    intervals = build_technical_intervals(
        [("VOICE_COMMAND", 1000), ("SYSTEM_RESPONSE_STARTED", 1200), ("SYSTEM_RESPONSE_FINISHED", 2400)],
        max_open_ms=5000,
    )
    assert segment_technical_flags(1000, 1100, intervals) == ("TECHNICAL", True)
    assert segment_technical_flags(1300, 2000, intervals) == ("TECHNICAL", True)
    assert segment_technical_flags(3000, 4000, intervals) == ("SPEECH", False)


def test_orphaned_tts_start_is_bounded():
    intervals = build_technical_intervals([("SYSTEM_RESPONSE_STARTED", 1000)], max_open_ms=1500)
    assert intervals == [(1000, 2500, "TECHNICAL")]

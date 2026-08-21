from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
AGENT = ROOT / "apps" / "recorder-agent"
HOST = ROOT / "apps" / "recorder-host"


def test_playable_audio_is_background_and_ffmpeg_independent():
    writer = (AGENT / "LocalPlayableAudioWriter.cs").read_text(encoding="utf-8")
    runtime = (HOST / "RecorderHostRuntime.cs").read_text(encoding="utf-8")
    assert "*.wav.part" in writer or 'finalPath + ".part"' in writer
    assert "File.Move(part, finalPath, true)" in writer
    assert "WriteHeader" in writer and "RF64" in writer
    assert "FlacEncoder" not in writer
    assert "_playableWake.Signal()" in runtime


def test_playable_state_is_durable_and_legacy_compatible():
    spool = (AGENT / "SpoolStore.cs").read_text(encoding="utf-8")
    protocol = (AGENT / "AgentIpcProtocol.cs").read_text(encoding="utf-8")
    assert "playable_audio_state TEXT NOT NULL DEFAULT 'NOT_REQUIRED'" in spool
    assert "recording_playable_files" in spool
    assert "playable_audio_state='PENDING'" in spool
    assert 'string PlayableAudioState = "NOT_REQUIRED"' in protocol


def test_delivery_wakes_on_reconnect_and_encoder_completion():
    api = (AGENT / "AgentApiClient.cs").read_text(encoding="utf-8")
    encoder = (AGENT / "GlobalRawEncoderWorker.cs").read_text(encoding="utf-8")
    assert "if (!wasConnected)" in api
    assert "MakeRetryableDeliveriesDueAsync" in api
    assert "_deliveryWake.Signal();" in api
    assert encoder.count("deliveryWake.Signal();") >= 2


def test_local_sessions_command_is_additive():
    host = (AGENT / "AgentPipeHost.cs").read_text(encoding="utf-8")
    desktop = (ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop" / "Services" / "RecorderPipeService.cs").read_text(encoding="utf-8")
    assert 'case "LIST_LOCAL_SESSIONS"' in host
    assert '"LIST_LOCAL_SESSIONS"' in desktop


def test_installed_audiograph_gate_waits_for_and_validates_playable_files():
    script = (ROOT / "scripts" / "acceptance-audiograph-local-recording.ps1").read_text(encoding="utf-8")
    assert "playableAudioState" in script
    assert "playableAudioFiles" in script
    assert "Test-WaveFile" in script
    assert "playableReady" in script


def test_recording_fault_e2e_keeps_playable_gate_for_non_capture_failures():
    script = (ROOT / "scripts" / "e2e-recording.ps1").read_text(encoding="utf-8")
    assert "playableAudioState" in script
    assert "E2E_PLAYABLE_AUDIO_NOT_READY" in script
    assert '"usb-loss", "host-crash"' in script


def test_desktop_audio_playback_prefers_master_and_exposes_separate_tracks():
    view_model = (ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop" / "ViewModels" / "RecordingViewModel.cs").read_text(encoding="utf-8")
    page = (ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop" / "Pages" / "RecordingPage.xaml").read_text(encoding="utf-8")
    code_behind = (ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop" / "Pages" / "RecordingPage.xaml.cs").read_text(encoding="utf-8")
    assert "MasterAudioPath" in view_model
    assert "PlayableAudioFilePath => MasterAudioPath ?? MicrophoneAudioPath ?? SystemAudioPath" in view_model
    assert "CanOpenMicrophoneAudio" in page and 'Tag="microphone"' in page
    assert "CanOpenSystemAudio" in page and 'Tag="system"' in page
    assert 'Tag="master"' in page
    assert "OpenPlayableTrackButton_Click" in code_behind

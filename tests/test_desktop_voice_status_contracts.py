from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_desktop_separates_voice_host_auth_and_telemetry_failures():
    recording = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    settings = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/SettingsViewModel.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/SettingsPage.xaml.cs")
    mapper = read("apps/desktop/WhisperX.Atom.Desktop/Services/UiStatusMapper.cs")
    assert "Voice Host недоступен" in recording
    assert "VOICE_ASSISTANT_AUTH_REQUIRED" in recording
    assert "MarkVoiceTelemetryDisconnected" in settings
    assert "ViewModel?.MarkVoiceTelemetryDisconnected()" in page
    assert "VOICE_TELEMETRY_DISCONNECTED" in mapper


def test_recorder_health_failure_does_not_overwrite_voice_status():
    recording = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    catch_block = recording.split("catch (Exception)", 1)[1].split("OnPropertyChanged(nameof(AgentStatus))", 1)[0]
    assert 'VoiceStatus = "Мифодий недоступен"' not in catch_block

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host"
DESKTOP = ROOT / "apps/desktop/WhisperX.Atom.Desktop"


def test_russian_technology_profile_is_bounded_and_uses_eugene():
    router = (HOST / "Tts/TtsEngineRouter.cs").read_text(encoding="utf-8")
    profile = (HOST / "Tts/TtsVoiceProfile.cs").read_text(encoding="utf-8")
    assert 'MifodiyTech = "MIFODIY_TECH"' in profile
    assert 'Clean = "CLEAN"' in profile
    assert 'AidarClean = "AIDAR_CLEAN"' in profile
    assert 'configuredVoice = "eugene"' in router
    assert 'FxEnabled => TtsVoiceProfiles.IsTechnologyProfile' in router


def test_fx_is_playback_only_and_snapshot_is_additive():
    player = (HOST / "Tts/SpeechAudioPlayer.cs").read_text(encoding="utf-8")
    fx = (HOST / "Tts/MifodiyVoiceFxSampleProvider.cs").read_text(encoding="utf-8")
    contracts = (HOST / "../WhisperX.Atom.Voice.Core/VoiceContracts.cs").resolve().read_text(encoding="utf-8")
    assert "applyVoiceFx = false" in player
    assert "MifodiyVoiceFxSampleProvider" in player
    assert "Math.Clamp" in fx and "Limiter" in fx
    assert "VoiceProfile" in contracts and "TtsFxApplied" in contracts


def test_main_ui_exposes_russian_profiles_and_all_silero_voices():
    xaml = (DESKTOP / "Pages/SettingsPage.xaml").read_text(encoding="utf-8")
    assert "Мифодий — технологичный" in xaml
    assert "Евгений — чистый" in xaml
    assert "Русский Windows fallback" in xaml
    for voice in ('Tag="aidar"', 'Tag="eugene"', 'Tag="baya"', 'Tag="kseniya"', 'Tag="xenia"'):
        assert voice in xaml
    assert "JARVIS" not in xaml.upper()
    assert "PIPER" not in xaml.upper()


def test_tts_keeps_all_russian_silero_voices_and_no_piper_runtime():
    router = (HOST / "Tts/TtsEngineRouter.cs").read_text(encoding="utf-8")
    for voice in ('id = "aidar"', 'id = "eugene"', 'id = "baya"', 'id = "kseniya"', 'id = "xenia"'):
        assert voice in router
    assert "PIPER" not in router.upper()
    assert "JARVIS" not in router.upper()
    assert 'await _silero.EnsureReadyAsync' in router

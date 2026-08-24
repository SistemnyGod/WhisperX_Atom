from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
HOST = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs").read_text(encoding="utf-8")
REFINER = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/WhisperCppVoiceRefiner.cs").read_text(encoding="utf-8")
CONTRACTS = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceRefinementContracts.cs").read_text(encoding="utf-8")


def test_shadow_is_optional_and_never_part_of_readiness_failure():
    assert 'VOICE_ASR_REFINER_MODE' in HOST
    assert 'ParseVoiceRefinerMode' in HOST
    assert 'VoiceRefinerMode.Shadow' in HOST
    assert 'VoiceRefinerMode.Off' in HOST
    assert "IVoiceAsrRefiner" in HOST
    assert "QueueShadowRefinement" in HOST
    assert 'VoiceRefinerState' in HOST


def test_shadow_requires_verified_model_and_is_bounded():
    client = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/ResidentVoiceRefinerClient.cs").read_text(encoding="utf-8")
    host = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Refiner.Host/Program.cs").read_text(encoding="utf-8")
    assert 'VOICE_REFINER_ASSETS_UNAVAILABLE' in client
    assert 'VOICE_REFINER_ASSET_INTEGRITY_FAILED' in host
    assert 'VoiceRefinerProtocol.QueueCapacity' in host
    assert 'PipeOptions.CurrentUserOnly' in host
    assert 'voice-refiner.manifest.json' in client
    assert 'ReadManifestHashes' in client
    assert 'voice-shadow-*.wav' not in REFINER
    assert 'whisper-cli' not in REFINER.lower()


def test_envelope_does_not_define_serialized_or_ledger_payload():
    assert 'byte[] Pcm16kMono' in CONTRACTS
    assert 'IVoiceAsrRefiner' in CONTRACTS
    assert 'Serialize' not in CONTRACTS
    assert 'VoiceRefinementState' in CONTRACTS


def test_shadow_protocol_is_private_and_bounded():
    protocol = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceRefinerProtocol.cs").read_text(encoding="utf-8")
    buffer = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/UtteranceCaptureBuffer.cs").read_text(encoding="utf-8")
    assert "CurrentUserOnly" in (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Refiner.Host/Program.cs").read_text(encoding="utf-8")
    assert "QueueCapacity = 2" in protocol
    assert "MaxPcmBytes" in protocol
    assert "preRollSeconds = 2" in buffer
    assert "maxSeconds = 20" in buffer
    assert "Pcm16kMono" in CONTRACTS
    assert "VoiceRefinerProtocol.MaxPcmBytes - (int)_pttBuffer.Length" in HOST


def test_recorder_thresholds_are_strict_and_question_guard_is_preserved():
    assert 'VoiceIntent.StartRecording or VoiceIntent.PauseRecording or VoiceIntent.ResumeRecording => 0.70' in HOST
    assert 'VoiceIntent.StopRecording => 0.75' in HOST
    parser = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceIntentParser.cs").read_text(encoding="utf-8")
    assert 'if (ContainsQuestionMarker(text) || IsQuestion(withoutWake)) return false;' in parser


def test_release_packaging_cannot_skip_asset_gate():
    installer = (ROOT / "scripts/build-installer.ps1").read_text(encoding="utf-8-sig")
    publish = (ROOT / "scripts/publish-desktop.ps1").read_text(encoding="utf-8-sig")
    assert 'RequireVoiceRefinerAssets' in installer
    assert 'verify-voice-refiner-assets.ps1' in installer
    assert 'VoiceRefinerHost' in publish

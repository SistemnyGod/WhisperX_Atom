from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
HOST = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs").read_text(encoding="utf-8")
REFINER = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/WhisperCppVoiceRefiner.cs").read_text(encoding="utf-8")
CONTRACTS = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceRefinementContracts.cs").read_text(encoding="utf-8")


def test_shadow_is_optional_and_never_part_of_readiness_failure():
    assert 'VOICE_ASR_REFINER_MODE' in HOST
    assert 'Environment.GetEnvironmentVariable("VOICE_ASR_REFINER_MODE"), "OFF"' in HOST
    assert "IVoiceAsrRefiner" in HOST
    assert "QueueShadowRefinement" in HOST
    assert 'VoiceRefinerState' in HOST


def test_shadow_requires_verified_model_and_is_bounded():
    assert 'VOICE_REFINER_MODEL_SHA256_MISSING' in REFINER
    assert 'VOICE_REFINER_MODEL_INTEGRITY_FAILED' in REFINER
    assert 'CancelAfter(_timeout)' in REFINER
    assert 'voice-shadow-*.wav' in REFINER
    assert 'File.Delete(path)' in REFINER


def test_envelope_does_not_define_serialized_or_ledger_payload():
    assert 'byte[] Pcm16kMono' in CONTRACTS
    assert 'IVoiceAsrRefiner' in CONTRACTS
    assert 'Serialize' not in CONTRACTS
    assert 'VoiceRefinementState' in CONTRACTS


def test_recorder_thresholds_are_strict_and_question_guard_is_preserved():
    assert 'VoiceIntent.StartRecording or VoiceIntent.PauseRecording or VoiceIntent.ResumeRecording => 0.70' in HOST
    assert 'VoiceIntent.StopRecording => 0.75' in HOST
    parser = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceIntentParser.cs").read_text(encoding="utf-8")
    assert 'if (ContainsQuestionMarker(text) || IsQuestion(withoutWake)) return false;' in parser

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RUNNER = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceAcceptanceRunner.cs").read_text(encoding="utf-8")
SCRIPT = (ROOT / "scripts/acceptance-voice-to-transcript.ps1").read_text(encoding="utf-8")
FAR_FIELD_SCRIPT = (ROOT / "scripts/e2e-far-field-voice.ps1").read_text(encoding="utf-8")


def test_microphone_acceptance_reports_aliases_and_safety_metrics():
    assert "microphoneDeviceId" in RUNNER
    assert "AcceptedByAlias" in RUNNER
    assert "falseActivations" in RUNNER
    assert "audioQueueDrops" in RUNNER


def test_microphone_acceptance_uses_intent_specific_confidence_policy():
    assert "requiredConfidence = command.Intent switch" in RUNNER
    assert "VoiceIntent.AssistantQuery => VoiceIntentParser.DefaultMinimumConfidence" in RUNNER
    assert "VoiceIntent.StopRecording or VoiceIntent.StopSpeaking => 0.70" in RUNNER
    assert "result.Confidence >= 0.65" not in RUNNER


def test_sanitized_voice_to_transcript_artifact_requires_one_correlated_chain():
    for field in (
        "voiceTraceId",
        "commandId",
        "localSessionId",
        "serverSessionId",
        "meetingId",
        "mediaAssetId",
        "asrJobId",
        "transcriptV1Id",
    ):
        assert field in SCRIPT
    assert "VOICE_TO_TRANSCRIPT_READY" in SCRIPT
    assert "buildIdentity" in SCRIPT
    assert "latencyMs" in SCRIPT
    assert "backgroundFalseActivations" in SCRIPT
    assert "recognized text" in SCRIPT
    assert "audio paths" in SCRIPT


def test_far_field_acceptance_has_distance_condition_matrix_and_fail_closed_gate():
    for distance in ("0.5m", "1m", "2m", "3m"):
        assert f'distance = "{distance}"' in FAR_FIELD_SCRIPT
    for condition in ("quiet", "office", "ventilation", "conversation"):
        assert f'condition = "{condition}"' in FAR_FIELD_SCRIPT
    assert "minimumRecall = 0.98" in FAR_FIELD_SCRIPT
    assert "minimumRecall = 0.95" in FAR_FIELD_SCRIPT
    assert "minimumRecall = 0.85" in FAR_FIELD_SCRIPT
    assert "replay-dry-run" in FAR_FIELD_SCRIPT
    assert "falseActivations" in FAR_FIELD_SCRIPT
    assert "FAR_FIELD_GATE_NOT_READY" in FAR_FIELD_SCRIPT
    assert "VOICE_HOST_BUILD_MISMATCH" in FAR_FIELD_SCRIPT
    assert "recorderInvocations = 0" in FAR_FIELD_SCRIPT
    assert "brokerInvocations = 0" in FAR_FIELD_SCRIPT
    assert "fixturePathsIncluded = $false" in FAR_FIELD_SCRIPT

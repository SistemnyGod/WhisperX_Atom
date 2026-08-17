from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RUNNER = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceAcceptanceRunner.cs").read_text(encoding="utf-8")
SCRIPT = (ROOT / "scripts/acceptance-voice-to-transcript.ps1").read_text(encoding="utf-8")


def test_microphone_acceptance_reports_aliases_and_safety_metrics():
    assert "microphoneDeviceId" in RUNNER
    assert "AcceptedByAlias" in RUNNER
    assert "falseActivations" in RUNNER
    assert "audioQueueDrops" in RUNNER


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

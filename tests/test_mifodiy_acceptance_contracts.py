from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = (ROOT / "scripts/e2e-mifodiy.ps1").read_text(encoding="utf-8")
DOC = (ROOT / "docs/mifodiy-installed-acceptance.md").read_text(encoding="utf-8")
BROKER = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs").read_text(encoding="utf-8")


def test_installed_acceptance_is_explicit_and_has_fifty_voice_gate():
    assert '[ValidateSet("Contract", "Installed")]' in SCRIPT
    assert "RunMicrophone" in SCRIPT
    assert "VoiceTarget = 50" in SCRIPT
    assert "mic-acceptance" in SCRIPT
    assert "VOICE_GATE_NOT_READY" in SCRIPT
    assert "VOICE_ALIAS_COVERAGE_NOT_READY" in SCRIPT
    assert "VOICE_HOST_BUILD_MISMATCH" in SCRIPT
    assert "WhisperXAtomVoiceHost" in SCRIPT
    assert "effectiveVoice" in SCRIPT and "VOICE_RUSSIAN_VOICE_NOT_CONFIRMED" in SCRIPT


def test_production_gate_covers_installed_command_parser_without_recorder_side_effects():
    host_program = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/Program.cs").read_text(encoding="utf-8")
    runner = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceAcceptanceRunner.cs").read_text(encoding="utf-8")
    assert "--command-acceptance" in host_program
    assert 'mode = "command-acceptance"' in runner
    for marker in ('"start"', '"pause"', '"resume"', '"stop"', 'question-repair', 'question-deadline', 'question-pump'):
        assert marker in runner
    assert "recorderInvocations = 0" in runner
    assert "brokerInvocations = 0" in runner
    assert "Get-VoiceCommandGate" in SCRIPT
    assert "requiredRecorderCommands" in SCRIPT
    assert "voiceCommands = $voiceCommandGate" in SCRIPT


def test_production_gate_can_exercise_voice_host_to_desktop_broker_path():
    host_runtime = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs").read_text(encoding="utf-8")
    assert '[switch]$RunBroker' in SCRIPT
    assert "Get-VoiceBrokerGate" in SCRIPT
    assert "command = 'TEXT'" in SCRIPT
    for marker in ("question-repair", "question-deadline", "question-pump"):
        assert marker in SCRIPT
    assert "QueryId: result.QueryId" in host_runtime
    assert "Speak: false" in host_runtime
    assert "AnswerStatus: result.AssistantStatus" in host_runtime
    assert "CURRENT_MEETING" in SCRIPT


def test_assistant_acceptance_covers_scoped_and_negative_questions_without_text_artifacts():
    for marker in ("CURRENT_MEETING", "known-answer", "no-evidence", "number-date", "cross-meeting", "long-question"):
        assert marker in SCRIPT
    assert "evidenceMeetingIds" in SCRIPT
    assert "safeNoConfirmedFact" in SCRIPT
    assert "elapsedMs" in SCRIPT
    assert "NO_EVIDENCE" in SCRIPT and "GROUNDING_REJECTED" in SCRIPT
    assert "answerTextIncluded = $false" in SCRIPT
    assert "questionsIncluded = $false" in SCRIPT


def test_grounding_failures_cannot_forward_worker_text_to_tts():
    assert 'groundingRejected = query.Status is not' in BROKER
    assert "groundingRejected" in BROKER
    assert "AssistantErrorSpeech(query.ErrorCode ?? query.Status)" in BROKER
    assert '"NEEDS_REVIEW" => "Ответ требует проверки по стенограмме."' in BROKER


def test_logout_and_duplicate_delivery_are_fail_closed():
    assert '"/api/auth/logout"' in SCRIPT
    assert "unauthenticatedAfterLogout" in SCRIPT
    assert "DESKTOP_RESTART_NOT_REQUESTED" in SCRIPT
    assert "voice-playback-tombstones.json" in SCRIPT
    assert 'status = if ($Mode -eq "Contract") { "CONTRACT_ONLY" }' in SCRIPT
    assert "duplicatePassed" in SCRIPT


def test_acceptance_document_states_runtime_and_data_safety_boundaries():
    assert "CONTRACT_ONLY" in DOC
    assert "PASSED" in DOC and "BLOCKED" in DOC
    for forbidden in ("credentials", "cookies", "tokens", "аудио", "текст стенограммы"):
        assert forbidden in DOC

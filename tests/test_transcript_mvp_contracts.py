from pathlib import Path

from whisperx_atom.processing import validate_transcript_result


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_transcript_only_runtime_contract_is_explicit():
    compose = read("compose.dev.yml")
    start = read("scripts/start-transcription-mvp.ps1")
    doctor = read("scripts/doctor-transcription-mvp.ps1")
    e2e = read("scripts/e2e-transcript.ps1")
    assert "AUTO_SUMMARY_ENABLED: ${AUTO_SUMMARY_ENABLED:-false}" in compose
    assert "DIARIZATION_MODE: ${DIARIZATION_MODE:-preferred}" in compose
    assert 'ValidateSet("host", "container")' in start
    assert 'if ($GpuMode -eq "container")' in start
    assert 'start-host-gpu-worker.ps1' in start
    assert '"up", "-d", "--pull", "never"' in start
    assert "Rebuild" in start
    assert "stop summary-worker llama-server" in start
    assert "EnableSummary" in start
    assert 'if ($summaryEnabled) { $compose += "--profile"; $compose += "llm" }' in start
    assert 'qwen = if ($summaryEnabled) { "ENABLED" } else { "DISABLED" }' in start
    assert '$env:ASSISTANT_ENABLED = if ($summaryEnabled) { "true" } else { "false" }' in start
    assert "doctor.json" in doctor
    assert "Runs = 5" in e2e
    assert "WithSummary" in e2e
    assert "-ExpectSummary:$WithSummary" in e2e
    assert 'if ($WithLlm) { $env:AUTO_SUMMARY_ENABLED = "true" }' in read("scripts/e2e-core.ps1")


def test_full_launcher_enables_summary_without_changing_transcript_only_mvp():
    run = read("scripts/run-whisperx.ps1")
    doctor = read("scripts/doctor-whisperx.ps1")
    transcript_doctor = read("scripts/doctor-transcription-mvp.ps1")
    assert "TranscriptOnly" in run
    assert "params.EnableSummary = $true" in run
    assert "-ExpectSummary:(!$TranscriptOnly)" in run
    assert "[switch]$ExpectSummary" in doctor
    assert "[switch]$TranscriptOnly" in doctor
    assert "-WarningOnly:(!$ExpectSummary)" in transcript_doctor
    assert 'if ($ExpectSummary -and $qwen.status -notin @("READY", "BUSY"))' in transcript_doctor
    assert "$qwenRequiredFailure = $expectSummary" in doctor
    assert "$failed = $failed -or ($transcriptExit -ne 0)" in doctor
    assert "-TranscriptOnly:$TranscriptOnly" in run
    env_example = read(".env.example")
    assert "AUTO_SUMMARY_ENABLED=true" in env_example


def test_processing_quality_gate_and_partial_result_contracts():
    processing = read("whisperx_atom/processing.py")
    persistence = read("workers/ml_worker/persistence.py")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/013_transcript_quality.sql")
    assert "PARTIAL_READY" in processing
    assert "ALIGNMENT_FAILED" in processing
    assert "DIARIZATION_FAILED" in processing
    assert "TRANSCRIPT_EMPTY" in processing
    assert "warnings" in persistence and "quality_metadata" in persistence
    assert "AUTO_SUMMARY_ENABLED" in persistence
    assert "warnings" in migration and "quality_metadata" in migration


def test_quality_gate_accepts_monotonic_segments_and_rejects_invalid_results():
    valid = validate_transcript_result(
        {"segments": [{"start": 0.0, "end": 1.0, "text": "Привет"}, {"start": 0.8, "end": 2.0, "text": "мир"}]}
    )
    assert valid["valid"] is True
    assert valid["segment_count"] == 2

    assert validate_transcript_result({"segments": []})["error_code"] == "TRANSCRIPT_EMPTY"
    assert validate_transcript_result({"segments": [{"start": 2, "end": 1, "text": "ошибка"}]})["error_code"] == "TRANSCRIPT_INVALID_TIMECODE"
    assert validate_transcript_result({"segments": [{"start": 0, "end": 1, "text": ""}]})["error_code"] == "TRANSCRIPT_EMPTY"


def test_recorder_offline_session_does_not_fabricate_meeting_id():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    assert "CreateSessionAsync(sessionId, meetingId, title" in coordinator
    assert "AgentPreflightResult" in protocol
    assert "GET_SESSION_STATUS" in read("apps/recorder-agent/AgentPipeHost.cs")


def test_desktop_uses_chunked_tus_resume_and_partial_transcript():
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    vm = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    tracker = read("apps/desktop/WhisperX.Atom.Desktop/Services/ProcessingJobTracker.cs")
    assert "16 * 1024 * 1024" in client
    assert "GetTusOffsetAsync" in client and "HttpMethod.Head" in client
    assert "PARTIAL_READY" in vm
    assert "WaitForJobEventsAsync" in client
    assert 'api/jobs/{jobId}' in client
    assert "backend.GetJobAsync" in tracker and "AddMinutes(30)" in tracker

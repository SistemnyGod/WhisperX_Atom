from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_local_archive_uses_bomless_concat_and_atomic_audio_outputs():
    archive = read("apps/recorder-agent/LocalArchiveWriter.cs")
    assert "new UTF8Encoding(false)" in archive
    assert 'output + ".part"' in archive
    assert "ValidateAudioFileAsync" in archive
    assert "ffprobe" in archive
    assert "File.Move(outputPart, output, true)" in archive
    assert "manifest.json" in archive


def test_recording_session_has_separate_local_and_delivery_state():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    protocol = read("apps/recorder-agent/AgentIpcProtocol.cs")
    host = read("apps/recorder-agent/AgentPipeHost.cs")
    for field in ("local_finalize_state", "delivery_state", "archive_path", "last_error_code", "retry_count", "next_retry_at"):
        assert field in spool
    for field in ("LocalFinalizeState", "ArchivePath", "ErrorCode", "Retryable", "NextRetryAtUtc"):
        assert field in protocol
    assert "FinalizationResult" in protocol
    assert "LOCAL_ENCODING_FAILED" in host
    assert "SERVER_UNAVAILABLE" in host


def test_desktop_maps_finalize_codes_and_never_displays_raw_finalize_error():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/RecordingPage.xaml")
    assert "MapRecordingError" in view_model
    assert "LOCAL_ARCHIVE_FAILED" in view_model
    assert "SERVER_UNAVAILABLE" in view_model
    assert "Открыть локальный архив" in page
    assert "LocalFinalizeStatusLabel" in page
    assert "DeliveryStatusLabel" in page


def test_recovery_has_bounded_backoff_and_skips_cancelled_sessions():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    worker = read("apps/recorder-agent/Program.cs")
    assert "state NOT IN ('CANCELLED','FINALIZED')" in spool
    assert "'FINALIZED'" in spool
    assert "GetRetryDelay" in worker
    assert "15" in worker and "30" in worker and "FromMinutes(5)" in worker
    assert "PersistFinalizationFailureAsync" in worker

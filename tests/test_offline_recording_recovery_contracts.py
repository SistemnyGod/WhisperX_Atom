from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def test_ffprobe_numeric_fields_accept_string_and_number_values():
    encoder = read("apps/recorder-agent/FlacEncoder.cs")

    assert "TryReadIntValue(bits, out var bitsPerSample)" in encoder
    assert "TryReadLongValue(samples, out var decodedSamples)" in encoder
    assert "ValueKind == JsonValueKind.String" in encoder


def test_archive_and_delivery_use_independent_retry_clocks():
    coordinator = read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")

    assert "SetArchiveRetryAsync" in spool
    assert "current.ArchiveNextRetryAtUtc" in coordinator
    assert "current.NextRetryAtUtc" in coordinator
    assert "var archiveTask = archiveDue" in coordinator
    assert "var deliveryTask = deliveryDue" in coordinator
    # Archive retries must not overwrite the delivery retry timestamp.
    archive_pending = coordinator.split("if (raw.Pending > 0 || chunks.Count == 0)", 1)[1].split("return (\"LOCAL_READY\"", 1)[0]
    assert "nextRetryAtUtc:" not in archive_pending


def test_encoder_has_bounded_transient_retries_and_preserves_terminal_code():
    worker = read("apps/recorder-agent/GlobalRawEncoderWorker.cs")

    assert "MaxTransientEncodeAttempts" in worker
    assert 'terminalCode = "ENCODER_RETRY_EXHAUSTED"' in worker
    assert 'code is not "LOCAL_ENCODER_UNAVAILABLE"' in worker


def test_session_total_samples_is_rebuilt_from_raw_timeline():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    coordinator = read("apps/recorder-agent/RecordingDeliveryCoordinator.cs")

    assert "RefreshSessionTotalSamplesAsync" in spool
    assert "MAX(start_sample + sample_count)" in spool
    assert "await spool.RefreshSessionTotalSamplesAsync" in coordinator


def test_desktop_exposes_encoder_failure_instead_of_waiting_for_ffmpeg():
    host = read("apps/recorder-host/RecorderHostRuntime.cs")
    legacy = read("apps/recorder-agent/AgentPipeHost.cs")
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")

    assert '"ENCODE_FAILED"' in host
    assert '"ENCODE_FAILED"' in legacy
    assert '"ENCODE_FAILED" => "Ошибка кодирования"' in desktop

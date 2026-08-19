from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_legacy_live_capture_uses_one_callback_stream_not_blocking_rec_chunks():
    source = read("live_runtime.py")
    app = read("app.py")

    assert "InputStream" in source
    assert "record_session" in source
    assert "sd.rec(" not in source
    assert "sd.wait(" not in source
    assert "queue.Queue(maxsize=128)" in source
    assert "stream_failed" in source
    assert "record_session(" in app
    assert "stop_event=self.live_stop_event" in app
    assert "pause_event=self.live_pause_event" in app
    assert "cancel_event=cancel" in app


def test_live_offsets_are_sample_based_and_pause_does_not_shift_transcript_clock():
    source = read("live_runtime.py")
    app = read("app.py")

    assert "recorded_samples" in source
    assert "start_sample" in source
    assert "pause_event.is_set()" in source
    assert "start_sample / recorder.sample_rate" in app
    assert "time.monotonic() - recording_started" not in app


def test_live_chunks_are_atomic_and_checkpointed_without_exposing_part_files():
    source = read("live_runtime.py")

    assert ".part" in source
    assert "part_path.replace(final_path)" in source
    assert "_patchheader" in source
    assert "os.fsync" in source
    assert "AUDIO_INPUT_QUEUE_OVERRUN" in source

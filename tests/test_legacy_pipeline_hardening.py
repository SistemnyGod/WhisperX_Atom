from __future__ import annotations

import json
from pathlib import Path

from whisperx_atom.legacy_compat import (
    assign_speaker_result,
    assign_speakers_by_overlap,
    recover_interrupted_jobs,
)


def test_speaker_assignment_falls_back_to_original_segments_after_mutating_failure():
    result = {
        "segments": [
            {"start": 0.0, "end": 1.0, "text": "первый", "words": [{"word": "первый"}]},
            {"start": 1.0, "end": 2.0, "text": "второй", "words": [{"word": "второй"}]},
        ],
        "word_segments": [{"word": "первый"}],
    }
    original = json.loads(json.dumps(result["segments"], ensure_ascii=False))

    def mutating_failure(_diar, payload, _embeddings):
        payload["segments"] = []
        raise RuntimeError("fake assign failure")

    assigned = assign_speaker_result(
        result,
        lambda payload: mutating_failure(None, payload, {}),
        [
            {"start": 0.0, "end": 1.0, "speaker": "SPEAKER_00"},
            {"start": 1.0, "end": 2.0, "speaker": "SPEAKER_01"},
        ],
    )

    assert [segment["text"] for segment in assigned["segments"]] == ["первый", "второй"]
    assert [segment["speaker"] for segment in assigned["segments"]] == ["SPEAKER_00", "SPEAKER_01"]
    assert [{key: value for key, value in segment.items() if key != "speaker"} for segment in assigned["segments"]] == original


def test_interval_sweep_preserves_asr_order_and_unknown_gaps():
    segments = [
        {"start": 3.0, "end": 4.0, "text": "третий"},
        {"start": 0.0, "end": 1.0, "text": "первый"},
        {"start": 1.5, "end": 2.0, "text": "пропуск"},
    ]
    diarization = [
        {"start": 2.5, "end": 5.0, "speaker": "SPEAKER_01"},
        {"start": 0.0, "end": 1.2, "speaker": "SPEAKER_00"},
    ]

    assigned = assign_speakers_by_overlap(segments, diarization)

    assert [segment["text"] for segment in assigned] == ["третий", "первый", "пропуск"]
    assert [segment["speaker"] for segment in assigned] == ["SPEAKER_01", "SPEAKER_00", "UNKNOWN"]


def test_legacy_queues_have_a_bounded_capacity():
    source = Path("app/transcription_pipeline.py").read_text(encoding="utf-8")
    assert "LEGACY_QUEUE_CAPACITY = 2" in source
    assert "asyncio.Queue(maxsize=self.LEGACY_QUEUE_CAPACITY)" in source


def test_restart_marks_active_legacy_jobs_without_requeueing(tmp_path: Path):
    jobs = {
        "active": {"status": "running", "stage": "asr"},
        "done": {"status": "done", "stage": "done"},
    }
    written: dict[str, dict] = {}
    for name in jobs:
        (tmp_path / f"{name}.json").write_text("{}", encoding="utf-8")
    recovered = recover_interrupted_jobs(
        tmp_path,
        lambda name: jobs[name],
        lambda name, value: written.__setitem__(name, value),
    )

    assert recovered == 1
    assert written["active"]["error"] == "LEGACY_PIPELINE_INTERRUPTED_AFTER_RESTART"
    assert "done" not in written


def test_stop_drains_linear_queues_before_sentinels():
    source = Path("app/transcription_pipeline.py").read_text(encoding="utf-8")
    stop_body = source.split("    async def stop(self) -> None:", 1)[1].split("    async def submit", 1)[0]
    assert "await q.join()" in stop_body
    assert "await q.put(None)" in stop_body
    assert stop_body.index("await q.join()") < stop_body.index("await q.put(None)")


def test_release_payload_keeps_legacy_python_outside_production_runtime():
    publish = Path("scripts/publish-desktop.ps1").read_text(encoding="utf-8")
    verify = Path("scripts/verify-clean-runtime.ps1").read_text(encoding="utf-8")
    assert "app.py" in publish and "PRODUCTION_PAYLOAD_CONTAINS_LEGACY_PYTHON" in publish
    assert "app.py" in verify and "RUNTIME_LEGACY_PYTHON_IN_PAYLOAD" in verify
    assert "Get-ForbiddenPythonPayload" in verify

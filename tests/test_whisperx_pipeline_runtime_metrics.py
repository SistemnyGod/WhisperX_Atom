from __future__ import annotations

import time
from pathlib import Path

from whisperx_atom.contracts import ProcessingRequest
from whisperx_atom.metrics import PipelineMetrics


def test_pipeline_metrics_report_stage_timings_and_checkpoint_hit_rate():
    metrics = PipelineMetrics(queue_wait_ms=12.5, model_load_ms=42.0)
    with metrics.measure("normalize"):
        time.sleep(0.001)
    with metrics.measure("asr"):
        time.sleep(0.001)
    metrics.checkpoint(True)
    metrics.checkpoint(False)

    payload = metrics.to_dict(duration_seconds=10.0)
    assert payload["queue_wait_ms"] == 12.5
    assert payload["model_load_ms"] == 42.0
    assert payload["normalize_ms"] > 0
    assert payload["asr_ms"] > 0
    assert payload["media_prepare_ms"] == 0.0
    assert payload["total_processing_ms"] >= payload["asr_ms"]
    assert payload["audio_duration_ms"] == 10000.0
    assert payload["RTF"] == payload["rtf"]
    assert payload["checkpoint_hit"] is True
    assert "gpu_peak_vram_mb" in payload
    assert "gpu_utilization" in payload
    assert payload["checkpoint_lookups"] == 2
    assert payload["checkpoint_hits"] == 1
    assert payload["checkpoint_hit_rate"] == 0.5
    assert payload["rtf"] is not None


def test_processing_request_queue_wait_is_optional_and_append_only():
    request = ProcessingRequest(job_id="job", media_path=Path("sample.wav"))
    assert request.queue_wait_ms is None


def test_pipeline_metrics_accepts_media_prepare_and_keeps_all_stage_keys():
    metrics = PipelineMetrics(queue_wait_ms=1, media_prepare_ms=345, model_load_ms=2)
    payload = metrics.to_dict(duration_seconds=60 * 60)
    for key in (
        "queue_wait_ms",
        "media_prepare_ms",
        "model_load_ms",
        "normalize_ms",
        "asr_ms",
        "alignment_ms",
        "diarization_ms",
        "postprocess_ms",
        "total_processing_ms",
        "audio_duration_ms",
        "rtf",
        "gpu_peak_vram_mb",
        "gpu_utilization",
        "checkpoint_hit",
    ):
        assert key in payload
    assert payload["media_prepare_ms"] == 345.0
    assert payload["audio_duration_ms"] == 3_600_000.0

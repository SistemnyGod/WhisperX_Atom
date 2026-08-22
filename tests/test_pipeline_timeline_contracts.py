from pathlib import Path


ROOT = Path(__file__).parents[1]


def test_timeline_has_required_events_and_first_write_semantics():
    source = (ROOT / "workers/pipeline_timeline.py").read_text(encoding="utf-8")
    for event in (
        "STOP", "LOCAL_READY", "FLAC_READY", "UPLOAD_START", "DELIVERY_CONFIRMED",
        "MEDIA_READY", "ASR_QUEUED", "GPU_CLAIMED", "V1_READY", "V2_READY", "SUMMARY_READY",
    ):
        assert f'"{event}"' in source
    assert "pipelineEvents" in source
    assert "pipelineDurations" in source
    assert "IS NOT NULL" in source
    assert "GREATEST(0" in source


def test_workers_record_terminal_timeline_markers_and_images_ship_helper():
    ml = (ROOT / "workers/ml_worker/persistence.py").read_text(encoding="utf-8")
    ml_worker = (ROOT / "workers/ml_worker/worker.py").read_text(encoding="utf-8")
    summary = (ROOT / "workers/summary_worker/worker.py").read_text(encoding="utf-8")
    media = (ROOT / "workers/media_worker/persistence.py").read_text(encoding="utf-8")
    assert "V1_READY" in ml and "V2_READY" in ml and "GPU_CLAIMED" in ml_worker
    assert "SUMMARY_READY" in summary
    assert "MEDIA_READY" in media
    assert "pipeline_timeline.py" in (ROOT / "workers/media_worker/Dockerfile").read_text(encoding="utf-8")
    assert "pipeline_timeline.py" in (ROOT / "workers/summary_worker/Dockerfile").read_text(encoding="utf-8")


def test_hardware_runner_contains_fail_closed_twenty_run_gate():
    source = (ROOT / "scripts/hardware-release-acceptance.ps1").read_text(encoding="utf-8")
    assert "no-console-20x10s" in source
    assert "20" in source and "ServerDelivery" in source
    assert "duplicate" in source.lower()
    assert "BLOCKED" in source
    ab = (ROOT / "scripts/audio-quality-ab-gate.ps1").read_text(encoding="utf-8")
    assert "RAW_PROMOTION_RECOMMENDED" in ab
    assert "AUDIOGRAPH_RETAIN" in ab
    assert "0.8" in ab and "0.7" in ab


def test_tts_wheelhouse_is_offline_and_pinned():
    publisher = (ROOT / "scripts/publish-tts-host.ps1").read_text(encoding="utf-8")
    preparer = (ROOT / "scripts/prepare-tts-wheelhouse.ps1").read_text(encoding="utf-8")
    assert "--no-index" in publisher
    assert "--find-links" in publisher
    assert "Get-FileHash" in publisher and "wheelhouse-manifest.json" in publisher
    lock = (ROOT / "apps/tts-host/requirements.cpu.lock.txt").read_text(encoding="utf-8")
    assert "2.8.0" in lock and "2.8.0" in preparer

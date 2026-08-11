import json
from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_worker_runtime_migration_and_readiness_contract_are_present():
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/015_worker_runtime.sql")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "worker_instances" in migration
    assert "last_seen_at" in migration and "current_job_id" in migration
    assert '"/api/system/readiness"' in api
    for component in ("outbox-relay", "import-worker", "media-worker", "gpu-worker"):
        assert component in api
    assert "cudaAvailable" in api and "hfDiarization" in api


def test_long_job_heartbeat_contract_and_unique_e2e_identity_are_explicit():
    nats_utils = read("workers/nats_utils.py")
    media_worker = read("workers/media_worker/worker.py")
    gpu_worker = read("workers/ml_worker/worker.py")
    e2e = read("scripts/e2e-core.ps1")
    assert "message.in_progress()" in nats_utils
    assert "maintain_message" in media_worker and "renew_lease" in media_worker
    assert "maintain_message" in gpu_worker and "renew_lease" in gpu_worker
    assert "$runId" in e2e and "createdAt" in e2e and "ResultPath" in e2e


def test_regression_manifest_points_to_local_full_fixture_without_audio_blob():
    manifest = json.loads((ROOT / "tests/regression-audio/manifest.json").read_text(encoding="utf-8"))
    assert len(manifest) == 1
    fixture = manifest[0]
    assert fixture["audioNotCommitted"] is True
    assert fixture["sha256"] == "29fa011349d30e81bf04cc0dbf00460ccf5d45b45121cbd20b683343ead07a2f"
    assert fixture["minimum"]["segments"] >= 5
    assert fixture["minimum"]["qualityScore"] >= 65


def test_unknown_speakers_are_not_persisted_as_registry_members():
    persistence = read("workers/ml_worker/persistence.py")
    quality = read("diarization_quality.py")
    assert "normalize_speaker_label" in persistence
    assert "if not label" in persistence
    assert "return None" in quality


def test_host_gpu_runtime_uses_local_cuda_and_maps_container_storage_paths():
    compose = read("compose.dev.yml")
    worker = read("workers/ml_worker/worker.py")
    start = read("scripts/start-host-gpu-worker.ps1")
    stop = read("scripts/stop-host-gpu-worker.ps1")
    doctor = read("scripts/doctor-host-gpu-worker.ps1")
    probe = read("scripts/probe_host_gpu_worker.py")
    transcript_start = read("scripts/start-transcription-mvp.ps1")
    e2e = read("scripts/e2e-transcript.ps1")

    assert "POSTGRES_HOST_PORT" in compose and "NATS_HOST_PORT" in compose
    assert "WHISPERX_DATA_HOST" in worker and "resolve_storage_path" in worker
    assert "torch.cuda.is_available" in start and "workers.ml_worker.worker" in start
    assert "TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD" in start
    assert 'LLM_HEALTH_PORT = "18080"' in start
    assert "taskkill.exe" in stop and "/T" in stop
    assert "cudaAvailable" in doctor and "gpu-worker" in doctor
    assert "worker_instances" in probe and "last_seen_at" in probe
    assert 'ValidateSet("host", "container")' in transcript_start
    assert "-SkipRegistry" in e2e and 'Join-Path $repo ".env"' in e2e


def test_processing_pipeline_queues_are_created_only_by_async_start():
    processing = read("whisperx_atom/processing.py")
    assert "from app.transcription_pipeline import" in processing
    assert "def process" in processing
    pipeline = read("app/transcription_pipeline.py")
    assert "created lazily by start()" in pipeline
    assert "self.audio_queue = asyncio.Queue()" in pipeline


def test_pre_alignment_quality_warnings_are_not_persisted_after_repair():
    processing = read("whisperx_atom/processing.py")
    assert "warnings: list[str] = []" in processing
    assert "for reason in final_report.reasons" in processing

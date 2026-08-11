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

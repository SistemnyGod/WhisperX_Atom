from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_production_pipeline_uses_dedicated_atomic_runtime_state_boundary():
    pipeline = (ROOT / "app" / "transcription_pipeline.py").read_text(encoding="utf-8")
    state = (ROOT / "whisperx_atom" / "runtime_state.py").read_text(encoding="utf-8")
    assert "from whisperx_atom.runtime_state import" in pipeline
    assert "from app.storage import" not in pipeline
    assert "os.replace(temporary_path, destination)" in state
    assert "os.fsync" in state
    assert "invalid_job_id" in state


def test_legacy_compatibility_storage_reuses_atomic_runtime_state_boundary():
    storage = (ROOT / "app" / "storage.py").read_text(encoding="utf-8")
    assert "from whisperx_atom.runtime_state import" in storage
    assert "with job_path(job_id).open(\"w\"" not in storage

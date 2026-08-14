from pathlib import Path

ROOT = Path(__file__).parents[1]

def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")

def test_summary_worker_reports_lightweight_runtime_capabilities():
    worker = read("workers/summary_worker/worker.py")
    for text in ("AsyncHeartbeat(\"summary-worker\"", "modelAvailable", "modelManifestAvailable", "modelManifestValid", "modelValidationReason", "llamaRuntimeAvailable", "heartbeat.set_state(\"BUSY\")"):
        assert text in worker
    assert "LocalLlamaServer(" not in worker.split("def summary_capabilities", 1)[1].split("heartbeat =", 1)[0]

def test_readiness_resolves_disabled_ready_busy_missing_and_stale_without_loading_model():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    for state in ("DISABLED", "READY", "BUSY", "DEGRADED", "UNAVAILABLE"):
        assert f'"{state}"' in api
    for reason in ("auto_summary_disabled", "model_missing", "gpu_lease_busy", "summary_worker_stale"):
        assert reason in api
    assert "model_manifest_mismatch" in api
    assert "LocalLlamaServer" not in api.split('app.MapGet("/api/system/readiness"', 1)[1].split('app.MapGet("/api/system/status"', 1)[0]

def test_doctor_surfaces_summary_qwen_llama_and_gpu_lease_reason():
    doctor = read("scripts/doctor-transcription-mvp.ps1")
    for field in ("summaryWorker", "qwenModel", "llamaCpp", "gpuLease"):
        assert field in doctor

def test_llama_startup_keeps_child_diagnostics_visible():
    source = read("workers/summary_worker/llama_subprocess.py")
    assert "DEVNULL" not in source
    assert "llama_health_probe_failed" in source

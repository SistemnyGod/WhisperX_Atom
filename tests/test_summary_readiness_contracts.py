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
    for reason in ("assistant_and_auto_summary_disabled", "model_missing", "gpu_lease_busy", "summary_worker_stale"):
        assert reason in api
    assert "model_manifest_mismatch" in api
    assert "LocalLlamaServer" not in api.split('app.MapGet("/api/system/readiness"', 1)[1].split('app.MapGet("/api/system/status"', 1)[0]


def test_readiness_requires_a_valid_release_identity_and_treats_assistant_only_as_llm_enabled():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    readiness = api.split('app.MapGet("/api/system/readiness"', 1)[1].split('app.MapGet("/api/system/status"', 1)[0]
    assert 'configuration.GetValue("ASSISTANT_ENABLED", true)' in readiness
    assert "var qwenEnabled = autoSummaryEnabled || assistantEnabled;" in readiness
    assert '"ASSISTANT_ONLY"' in readiness
    # Summary/Qwen is reported independently; it must not make core
    # WhisperX/CUDA readiness false while the optional LLM worker is stale.
    assert 'if (qwenEnabled) requiredWorkerNames.Add("summary-worker");' not in readiness
    assert "Core processing readiness must not depend on the optional Qwen/Summary" in api
    assert "&& releaseIdentityValid;" in readiness
    assert "identityMismatch |= required && !matches;" in readiness

def test_doctor_surfaces_summary_qwen_llama_and_gpu_lease_reason():
    doctor = read("scripts/doctor-transcription-mvp.ps1")
    for field in ("summaryWorker", "qwenModel", "llamaCpp", "gpuLease"):
        assert field in doctor

def test_llama_startup_keeps_child_diagnostics_visible():
    source = read("workers/summary_worker/llama_subprocess.py")
    assert "DEVNULL" not in source
    assert "llama_health_probe_failed" in source


def test_assistant_only_server_startup_includes_llm_profile_and_boot_does_not_migrate():
    launcher = read("scripts/start-server-bundle.ps1")
    runtime = read("scripts/start-runtime.ps1")
    startup = read("scripts/install-server-startup-task.ps1")
    assert "if ($EnableQwen -or $EnableAssistant)" in launcher
    assert "supervise-server-runtime.ps1" in startup
    assert "AtLogOn" in startup and "RestartCount 20" in startup
    assert "docker load" not in runtime
    assert "MIGRATION_ONLY" not in runtime
    assert "ASSISTANT_ENABLED" in runtime


def test_qwen_and_diarization_readiness_use_actual_runtime_probe_state():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    worker = read("workers/ml_worker/worker.py")
    summary = read("workers/summary_worker/worker.py")
    assert "llamaRuntimeState" in api
    assert "llama_runtime_failed" in api
    assert "DiarizationPipeline" in worker
    assert 'local_model_path / "config.yaml"' in worker
    assert 'diarization_model.is_file()' in worker
    pipeline = read("app/transcription_pipeline.py")
    assert 'resolved_path / "config.yaml" if resolved_path.is_dir() else resolved_path' in pipeline
    assert "DIARIZATION_MODEL_CONFIG_NOT_FOUND" in pipeline
    assert "model_name=Path(resolved_pipeline)" in pipeline
    assert "pyannote_model_loaded" in worker
    assert "LLAMA_RUNTIME_FAILED" in summary


def test_system_readiness_separates_core_health_from_product_acceptance():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    readiness = api.split('app.MapGet("/api/system/readiness"', 1)[1].split('app.MapGet("/api/system/status"', 1)[0]
    assert "productReady" in readiness
    assert "productBlockers" in readiness
    assert '"diarization_not_ready"' in readiness
    assert '"memory_projection_incomplete"' in readiness
    assert '"qwen_not_ready"' in readiness


def test_general_chat_nullable_meeting_parameter_is_explicitly_typed():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert 'command.Parameters.Add("meeting", NpgsqlDbType.Uuid)' in store
    assert "meetingId is Guid value ? value : DBNull.Value" in store


def test_shared_llm_runtime_uses_one_coordination_owner():
    summary = read("workers/summary_worker/worker.py")
    assistant = read("workers/summary_worker/assistant.py")
    assert 'LLM_RUNTIME_OWNER = "llm-runtime"' in summary
    assert 'LLM_RUNTIME_OWNER = "llm-runtime"' in assistant
    assert "mark_llm_resident, self._llm_owner" in summary
    assert "mark_llm_resident, self._llm_owner" in assistant
    assert "mark_llm_busy, self._llm_owner" in summary
    assert "mark_llm_busy, self._llm_owner" in assistant

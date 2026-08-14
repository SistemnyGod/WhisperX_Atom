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


def test_gpu_worker_maps_empty_valid_audio_to_no_speech_code():
    worker = read("workers/ml_worker/worker.py")
    assert 'if "transcript_empty" in text:' in worker
    assert 'return "NO_SPEECH_DETECTED"' in worker


def test_host_gpu_runtime_uses_local_cuda_and_maps_container_storage_paths():
    compose = read("compose.dev.yml")
    worker = read("workers/ml_worker/worker.py")
    start = read("scripts/start-host-gpu-worker.ps1")
    common = read("scripts/WhisperX.Runtime.ps1")
    stop = read("scripts/stop-host-gpu-worker.ps1")
    doctor = read("scripts/doctor-host-gpu-worker.ps1")
    probe = read("scripts/probe_host_gpu_worker.py")
    transcript_start = read("scripts/start-transcription-mvp.ps1")
    e2e = read("scripts/e2e-transcript.ps1")

    assert "POSTGRES_HOST_PORT" in compose and "NATS_HOST_PORT" in compose
    assert "WHISPERX_DATA_HOST" in worker and "resolve_storage_path" in worker
    assert "torch.cuda.is_available" in start and "workers.ml_worker.worker" in start
    assert "TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD" in common
    assert 'LLM_HEALTH_PORT = "18080"' in common
    assert "Stop-WhisperXProcessTree" in stop and "taskkill.exe" in common
    assert "cudaAvailable" in doctor and "gpu-worker" in doctor
    assert "worker_instances" in probe and "last_seen_at" in probe
    assert "Get-WhisperXHostWorkerCandidates" in common and "HOST_WORKER_DUPLICATE" in start
    assert 'ValidateSet("host", "container")' in transcript_start
    assert "-SkipRegistry" in e2e and "Set-WhisperXRuntimeEnvironment" in e2e


def test_host_worker_doctor_accepts_exact_python_path_when_commandline_is_unavailable():
    runtime = read("scripts/WhisperX.Runtime.ps1")
    assert "$executablePath = \"\"" in runtime
    assert "[IO.Path]::GetFullPath($executablePath)" in runtime
    assert "executable path is a safe fallback identity check" in runtime


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


def test_host_runtime_automation_and_resumable_tus_contracts_are_explicit():
    common = read("scripts/WhisperX.Runtime.ps1")
    run = read("scripts/run-whisperx.ps1")
    start = read("scripts/start-transcription-mvp.ps1")
    watchdog = read("scripts/watch-host-gpu-worker.ps1")
    doctor = read("scripts/doctor-whisperx.ps1")
    e2e = read("scripts/e2e-core.ps1")
    startup = read("scripts/install-whisperx-startup-task.ps1")

    assert "Import-WhisperXDotEnv" in common and "Write-WhisperXRuntimeState" in common
    assert "StartWatchdog" in run and '"--pull", "never"' in start
    assert "WORKER_RESTART_LIMIT" in watchdog and "MaxRestarts" in watchdog
    assert "hostGpuWorker" in doctor and "hfDiarization" in doctor and "qwen" in doctor
    assert "Upload-TusResumable" in e2e and "16MB" in e2e and "Upload-Offset" in e2e
    assert "Register-ScheduledTask" in startup


def test_release_gate_blocks_without_complete_live_core_evidence():
    gate = read("scripts/release-gate.ps1")
    e2e = read("scripts/e2e-core.ps1")
    assert "BLOCKED_BY_CORE_PIPELINE" in gate
    assert "mvp-release-audit.json" in gate
    assert "mvp-release-gate.json" in gate
    for field in ("localArchiveReady", "deliveryConfirmed", "mediaReady", "summaryStatus"):
        assert field in e2e


def test_silent_valid_audio_becomes_no_speech_partial_transcript_without_summary_job():
    processing = read("whisperx_atom/processing.py")
    persistence = read("workers/ml_worker/persistence.py")
    assert "_is_silent_pcm(ctx.asr_audio_path)" in processing
    assert "NO_SPEECH_DETECTED" in processing
    assert "status=\"PARTIAL_READY\"" in processing
    assert "no_speech_detected" in persistence
    assert 'result_error_code == "NO_SPEECH_DETECTED"' in persistence
    assert "if no_speech_detected and result_error_code is None" in persistence
    assert "and not no_speech_detected" in persistence
    assert '"PARTIAL_READY" if no_speech_detected else "TRANSCRIPT_READY"' in persistence


def test_gpu_worker_delays_redelivery_when_resident_llm_blocks_transcription():
    worker = read("workers/ml_worker/worker.py")
    assert 'RESIDENT_LLM_RETRY_DELAY_SECONDS = max(5, int(os.getenv("GPU_RESIDENT_LLM_RETRY_DELAY_SECONDS", "15")))' in worker
    assert "except ResidentLlmConflict:" in worker
    assert "await message.nak(delay=RESIDENT_LLM_RETRY_DELAY_SECONDS)" in worker


def test_archive_validation_decodes_master_after_ffprobe():
    writer = read("apps/recorder-agent/LocalArchiveWriter.cs")
    assert "ffprobe_invalid_audio" in writer
    assert "ffmpeg_decode_failed" in writer
    assert '"-f", "null", "-"' in writer

from pathlib import Path
import json


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_release_manifest_has_pinned_release_identity_and_models():
    manifest = read("scripts/write-runtime-manifest.ps1")
    pipeline = read("app/transcription_pipeline.py")
    env = read(".env.example")
    for field in ("releaseVersion", "gitCommit", "runtimeProfile", "WHISPERX_MODEL_REVISION", "DIARIZATION_MODEL_REVISION", "LLM_MODEL_REVISION", "LLM_MODEL_SHA256"):
        assert field in manifest or field in env
    assert "WHISPERX_RUNTIME_MANIFEST_DEEP" in manifest
    assert "ModelManifestPath" in manifest
    assert "model-manifest.json" in manifest
    assert "whisperXModelInventoryHash" in manifest
    assert "onnxRuntime = $onnxVersion" in manifest
    assert "snapshot_download" in pipeline
    assert 'label="ASR_MODEL"' in pipeline
    assert 'label="DIARIZATION_MODEL"' in pipeline
    assert "model_name=resolved_model" in pipeline
    assert "WHISPERX_MODEL_LOCAL_ONLY" in env


def test_lan_launcher_fails_closed_for_unpinned_diarization_revision():
    launcher = read("scripts/start-whisperx-lan-server.ps1")
    assert 'Read-EnvValue "DIARIZATION_MODEL_REVISION"' in launcher
    assert "LAN_DIARIZATION_REVISION_REQUIRED" in launcher
    assert "immutable model revision" in launcher


def test_llm_download_fails_before_replacing_mismatched_production_model():
    script = read("scripts/llm-download.ps1")
    assert "refusing to replace existing file" in script
    assert "Write-ModelManifest" in script
    assert "schemaVersion = 1" in script
    assert script.index("Production model checksum mismatch") < script.index("hfPath download")


def test_core_release_images_do_not_use_latest_or_major_only_tags():
    compose = read("compose.dev.yml")
    assert ":latest" not in compose
    assert "postgres:17.5-alpine3.21" in compose
    assert "nats:2.11.6-alpine3.21" in compose
    assert "tusproject/tusd:v2.6.0" in compose
    assert '"rollForward": "disable"' in read("global.json")


def test_release_gate_requires_all_live_acceptance_scenarios():
    gate = read("scripts/release-gate.ps1")
    for scenario in (
        "e2e-5m",
        "server-offline-recovery",
        "recorder-crash-recovery",
        "worker-crash-recovery",
        "windows-reboot-recovery",
        "no-console-start",
        "audio-quality",
        "audio-device-loss",
        "system-audio-device-loss",
        "low-disk-during-recording",
        "cold-model-cache",
        "gpu-oom",
        "delete-locked-media",
        "endurance-30m",
        "endurance-2h",
        "4h-recording",
        "8h-recording",
        "backup-restore",
        "rbac-isolation",
    ):
        assert scenario in gate
    assert "ACCEPTANCE_" in gate
    assert "backupVerified" in gate and "cleanRestore" in gate
    assert 'summaryTerminalUsable = ($summaryStatus -in @("READY", "NEEDS_REVIEW"))' in gate
    assert "summaryQualityGreen" in gate


def test_acceptance_registry_is_single_source_and_uses_container_gpu_release():
    registry = json.loads(read("scripts/acceptance-scenarios.json"))
    names = {item["name"] for item in registry}
    assert "cold-model-runtime" in names
    runtime_entry = next(item for item in registry if item["name"] == "cold-model-runtime")
    assert "cold-model-cache" in runtime_entry["aliases"]
    gate = read("scripts/release-gate.ps1")
    bundle = read("scripts/build-server-bundle.ps1")
    assert "acceptance-scenarios.json" in gate
    assert "acceptance-scenarios.json" in bundle
    assert 'gpuWorkerMode' in gate and "container" in gate


def test_runtime_doctor_publishes_fields_consumed_by_release_gate():
    doctor = read("scripts/doctor-whisperx.ps1")
    gate = read("scripts/release-gate.ps1")
    assert "gpuWorkerMode = $mode" in doctor
    assert "gpuWorker = if ($mode -eq \"container\")" in doctor
    assert "Get-JsonProperty $runtimeReport 'gpuWorkerMode'" in gate
    assert "Get-JsonProperty $runtimeReport $component" in gate

from pathlib import Path


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
        "endurance-30m",
        "endurance-2h",
        "backup-restore",
        "rbac-isolation",
    ):
        assert scenario in gate
    assert "ACCEPTANCE_" in gate
    assert "backupVerified" in gate and "cleanRestore" in gate
    assert 'summaryTerminalUsable = ($summaryStatus -in @("READY", "NEEDS_REVIEW"))' in gate
    assert "summaryQualityGreen" in gate

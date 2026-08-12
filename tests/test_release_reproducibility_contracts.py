from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_release_manifest_has_pinned_release_identity_and_models():
    manifest = read("scripts/write-runtime-manifest.ps1")
    env = read(".env.example")
    for field in ("releaseVersion", "gitCommit", "runtimeProfile", "WHISPERX_MODEL_REVISION", "DIARIZATION_MODEL_REVISION", "LLM_MODEL_REVISION", "LLM_MODEL_SHA256"):
        assert field in manifest or field in env
    assert "WHISPERX_RUNTIME_MANIFEST_DEEP" in manifest


def test_llm_download_fails_before_replacing_mismatched_production_model():
    script = read("scripts/llm-download.ps1")
    assert "refusing to replace existing file" in script
    assert script.index("Production model checksum mismatch") < script.index("hfPath download")


def test_core_release_images_do_not_use_latest_or_major_only_tags():
    compose = read("compose.dev.yml")
    assert ":latest" not in compose
    assert "postgres:17.5-alpine3.21" in compose
    assert "nats:2.11.6-alpine3.21" in compose
    assert "tusproject/tusd:v2.6.0" in compose
    assert '"rollForward": "disable"' in read("global.json")

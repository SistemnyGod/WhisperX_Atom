from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_cleanup_is_allowlisted_and_preserves_root_release_artifacts():
    source = (ROOT / "scripts" / "cleanup-release-artifacts.ps1").read_text(encoding="utf-8")
    assert "CLEANUP_ROOT_ARTIFACTS_FORBIDDEN" in source
    assert "CLEANUP_WEB_LOCKFILE_REQUIRED" in source
    assert "IncludeNodeModules" in source
    assert "Docker volumes" in source and "Patrol360" in source
    assert "KeepIdentities" in source and "protectedIdentities" in source
    assert "git clean" not in source.lower()


def test_server_bundle_rejects_local_documents_before_release():
    source = (ROOT / "scripts" / "build-server-bundle.ps1").read_text(encoding="utf-8")
    assert "RELEASE_FORBIDDEN_DOCUMENT" in source
    assert "Токен.docx" in source


def test_docker_cleanup_is_image_only_and_preserves_patrol360_and_volumes():
    source = (ROOT / "scripts" / "cleanup-docker-images.ps1").read_text(encoding="utf-8-sig")
    assert "whisperx-atom-*" in source
    assert "patrol360" in source.lower()
    assert "volumesChanged = $false" in source
    assert "globalPrune = $false" in source
    assert "docker volume" not in source.lower()
    assert "docker system prune" not in source.lower()

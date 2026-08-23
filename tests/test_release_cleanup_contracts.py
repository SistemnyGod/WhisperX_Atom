from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_cleanup_is_allowlisted_and_preserves_root_release_artifacts():
    source = (ROOT / "scripts" / "cleanup-release-artifacts.ps1").read_text(encoding="utf-8")
    assert "CLEANUP_ROOT_ARTIFACTS_FORBIDDEN" in source
    assert "CLEANUP_WEB_LOCKFILE_REQUIRED" in source
    assert "IncludeNodeModules" in source
    assert "Docker volumes" in source and "Patrol360" in source
    assert "git clean" not in source.lower()


def test_server_bundle_rejects_local_documents_before_release():
    source = (ROOT / "scripts" / "build-server-bundle.ps1").read_text(encoding="utf-8")
    assert "RELEASE_FORBIDDEN_DOCUMENT" in source
    assert "Токен.docx" in source

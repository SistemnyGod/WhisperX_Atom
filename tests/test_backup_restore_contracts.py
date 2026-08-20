from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_backup_manifest_is_traceable_and_excludes_secrets():
    backup = read("scripts/backup.ps1")
    for field in ("createdAt", "release", "gitCommit", "schemaVersion", "audioPolicy", "files", "sha256"):
        assert field in backup
    assert 'secretsIncluded = $false' in backup
    assert "POSTGRES_PASSWORD" in backup


def test_backup_resolves_bundle_root_before_source_tree_parent():
    backup = read("scripts/backup.ps1")
    assert "$repo = $PSScriptRoot" in backup
    assert "Join-Path $repo '.env.example'" in backup
    assert "$repo = Split-Path -Parent $PSScriptRoot" in backup


def test_restore_validates_hash_schema_and_requires_force_for_nonempty_target():
    restore = read("scripts/restore.ps1")
    for guard in ("BACKUP_HASH_MISMATCH", "BACKUP_SCHEMA_NEWER_THAN_RUNTIME", "RESTORE_TARGET_NOT_EMPTY_FORCE_REQUIRED"):
        assert guard in restore
    assert "[switch]$Force" in restore
    assert "-not $Apply" in restore


def test_audio_policy_is_explicit_and_media_copy_is_opt_in():
    backup = read("scripts/backup.ps1")
    restore = read("scripts/restore.ps1")
    assert '"archive-included"' in backup and '"metadata-only"' in backup
    assert "$IncludeMedia" in restore


def test_backup_writes_external_archive_checksum_and_restore_can_require_it():
    backup = read("scripts/backup.ps1")
    restore = read("scripts/restore.ps1")
    assert "ArchiveSha256Path" in backup
    assert "Get-FileHash -LiteralPath $archive -Algorithm SHA256" in backup
    assert '"$archive.sha256"' in backup
    assert "tar.exe" in backup
    assert "RequireArchiveSha256" in restore
    assert "BACKUP_ARCHIVE_HASH_MISMATCH" in restore


def test_restore_uses_zip_slip_safe_extraction_and_bundle_relative_paths():
    restore = read("scripts/restore.ps1")
    assert "Expand-ZipArchiveSafe" in restore
    assert "BACKUP_ARCHIVE_PATH_ESCAPE" in restore
    assert "compose.dev.yml" in restore
    assert "$repo = $PSScriptRoot" in restore


def test_backup_restore_drill_is_plan_first_and_isolates_apply():
    drill = read("scripts/e2e-backup-restore.ps1")
    assert "[ValidateSet('Plan', 'Verify', 'Apply')]" in drill
    assert "RESTORE_EXPLICIT_CONFIRMATION_REQUIRED" in drill
    assert "RESTORE_TEST_CONTAINER_REQUIRED" in drill
    assert "Assert-IsolatedTestRoot" in drill
    assert "contentChecksPassed = $false" in drill
    assert "productionVolumesTouched = $false" in drill
    assert "docker compose down -v" not in drill


def test_release_gate_requires_backup_content_validation_not_only_pg_restore():
    gate = read("scripts/release-gate.ps1")
    assert "$json.contentChecksPassed -eq $true" in gate
    verifier = read("scripts/verify-backend-deployment.ps1")
    assert 'scripts\\e2e-backup-restore.ps1' in verifier

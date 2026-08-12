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

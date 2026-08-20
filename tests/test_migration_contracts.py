from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
MIGRATIONS = ROOT / "apps" / "server" / "WhisperX.Atom.Api" / "Migrations"


def test_migration_ids_are_unique_even_when_order_prefixes_repeat():
    files = sorted(MIGRATIONS.glob("*.sql"))
    assert files
    ids = [path.stem for path in files]
    assert len(ids) == len(set(ids))
    assert all(re.fullmatch(r"\d{3}_[a-z0-9][a-z0-9_-]*", value) for value in ids)
    ordered = sorted(ids, key=lambda value: (int(value[:3]), value))
    assert ids == ordered
    assert any(value.startswith("025_") for value in ids)
    assert any(value.startswith("026_") for value in ids)


def test_migration_runner_uses_immutable_id_and_checksum_history():
    source = (ROOT / "apps" / "server" / "WhisperX.Atom.Api" / "Program.cs").read_text(encoding="utf-8")
    assert "schema_migrations(version text PRIMARY KEY" in source
    assert "schema_migration_checksums(version text PRIMARY KEY" in source
    assert "Path.GetFileNameWithoutExtension(file)" in source
    assert "MIGRATION_CHECKSUM_MISMATCH" in source

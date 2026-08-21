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


def test_live_memory_retention_migration_keeps_stop_to_v1_handoff_bounded():
    migration = (MIGRATIONS / "036_live_meeting_retention.sql").read_text(encoding="utf-8")
    assert "retention_policy" in migration
    assert "UNTIL_V1_READY" in migration
    assert "CANONICALIZED" in migration
    assert "7 days" in migration


def test_gpu_watchdog_and_runtime_coordination_migrations_are_additive():
    watchdog = (MIGRATIONS / "039_job_progress_watchdog.sql").read_text(encoding="utf-8")
    coordination = (MIGRATIONS / "040_gpu_runtime_coordination.sql").read_text(encoding="utf-8")
    assert "stage_changed_at" in watchdog and "progress_changed_at" in watchdog
    assert "timeout_requeue_count" in watchdog and "TG_OP = 'INSERT'" in watchdog
    assert "gpu_runtime_coordination" in coordination
    assert "llm_state" in coordination and "asr_request_id" in coordination
    assert "asr_state" in coordination and "ASR_PENDING" in coordination
    assert "ix_transcript_segments_russian_fts" in coordination

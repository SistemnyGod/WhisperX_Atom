from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
API = ROOT / "apps" / "server" / "WhisperX.Atom.Api" / "Program.cs"
MIGRATION = ROOT / "apps" / "server" / "WhisperX.Atom.Api" / "Migrations" / "059_historical_transcript_imports.sql"
BACKFILL = ROOT / "workers" / "memory_worker" / "backfill.py"


def test_historical_import_endpoint_is_privileged_and_has_no_source_path_contract():
    api = API.read_text(encoding="utf-8")
    assert 'MapPost("/api/admin/historical-imports"' in api
    assert "if (!IsPrivileged(context)) return Results.Forbid();" in api
    assert "ImportHistoricalTranscriptAsync" in api
    assert "HistoricalImportRequest" in api
    assert "string SourcePath" not in api[api.index("HistoricalImportRequest"):api.index("HistoricalImportResult")]


def test_historical_receipt_is_owner_scoped_and_idempotent():
    migration = MIGRATION.read_text(encoding="utf-8")
    assert "UNIQUE(owner_user_id, canonical_content_sha256)" in migration
    assert "source path and transcript text" in migration
    api = API.read_text(encoding="utf-8")
    assert "pg_advisory_xact_lock(hashtextextended(@lock_key, 0))" in api
    assert "ON CONFLICT(transcript_id,transcript_version) DO NOTHING" in api
    assert "HISTORICAL_IMPORT_UNVERIFIED" in api


def test_memory_backfill_includes_historical_import_without_changing_existing_kinds():
    backfill = BACKFILL.read_text(encoding="utf-8")
    assert "('ENRICHED','V2','HISTORICAL_IMPORT')" in backfill


from pathlib import Path

from workers.summary_worker.protocol import normalize_protocol_result

ROOT = Path(__file__).parents[1]

def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")

def test_auto_summary_payload_is_protocol_profile_with_traceability():
    persistence = read("workers/ml_worker/persistence.py")
    for field in ("AUTO_SUMMARY_PROFILE", "MEETING_PROTOCOL_RU", "summary_profile", "prompt_version", "transcript_id", "source_hash", "meeting_context", "correlation_id"):
        assert field in persistence

def test_one_active_summary_job_is_bound_to_one_transcript_version():
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/021_summary_transcript_version.sql")
    persistence = read("workers/ml_worker/persistence.py")
    assert "input_transcript_id" in migration
    assert "ux_active_summary_per_transcript" in migration
    assert "ON CONFLICT DO NOTHING" in persistence

def test_protocol_contract_does_not_assign_responsible_people_and_failure_keeps_transcript():
    contracts = read("workers/summary_worker/contracts.py")
    worker = read("workers/summary_worker/worker.py")
    protocol_schema = contracts.split("MEETING_PROTOCOL_RU_SCHEMA", 1)[1].split("@dataclass", 1)[0]
    assert '"responsible"' not in protocol_schema
    protocol_result = normalize_protocol_result({"tasks": [{"task": "Проверить насос", "responsible": "Иванов"}]})
    assert "responsible" not in protocol_result["tasks"][0]
    assert "UPDATE meetings SET status='PARTIAL_READY'" in worker
    assert "UPDATE transcripts SET status='FAILED'" not in worker

def test_manual_rebuild_defaults_to_protocol_and_selected_transcript():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert 'configuration["AUTO_SUMMARY_PROFILE"] ?? "MEETING_PROTOCOL_RU"' in store
    assert "input_transcript_id" in store

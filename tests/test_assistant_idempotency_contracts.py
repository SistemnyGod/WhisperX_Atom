from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_voice_idempotency_migration_is_additive_and_scoped():
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/048_voice_assistant_idempotency.sql")
    assert "ADD COLUMN IF NOT EXISTS command_id TEXT" in migration
    assert "ADD COLUMN IF NOT EXISTS trace_id TEXT" in migration
    assert "CREATE UNIQUE INDEX IF NOT EXISTS ux_assistant_queries_voice_command" in migration
    assert "WHERE source = 'VOICE' AND command_id IS NOT NULL" in migration


def test_api_replays_by_command_without_creating_a_second_query():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert "GetAssistantQueryByCommandAsync" in api
    assert "IDEMPOTENT_REPLAY" in api
    assert "ASSISTANT_IDEMPOTENCY_CONFLICT" in api
    assert "source='VOICE' AND command_id=@command" in store
    assert "command_id,trace_id" in store


def test_desktop_keeps_command_only_reconciliation_without_question_text():
    ledger = read("apps/desktop/WhisperX.Atom.Desktop/Services/AssistantDeliveryStore.cs")
    broker = read("apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs")
    assert "AssistantDeliveryState.Reconciling" in ledger
    assert "TryAddCommand" in ledger
    assert "Keep command-only Reconciling entries" in ledger
    assert "GetForReconciliation" in broker
    assert "GetAssistantRequestByCommandAsync" in broker


def test_retrieval_has_cpu_onnx_provider_and_raw_cosine_gate():
    retrieval = read("workers/summary_worker/hybrid_retrieval.py")
    requirements = read("workers/summary_worker/requirements.txt")
    assert "CPUExecutionProvider" in retrieval
    assert "embedding_snapshot_sha256_mismatch" in retrieval
    assert "onnxruntime" in requirements
    assert "tokenizers" in requirements
    assert "raw_cosine >= embedding_minimum" in retrieval


def test_qwen_warmup_is_preemptible_and_checks_all_higher_priority_work():
    coordination = read("workers/gpu_runtime_coordination.py")
    worker = read("workers/summary_worker/worker.py")
    assert "higher_priority_work_active" in coordination
    assert "TRANSCRIPT_ENRICH" in coordination
    assert "assistant_queries" in coordination
    assert "higher_priority_work_active" in worker
    assert "shared_runtime.enabled" in worker
    assert "threading.Event" in worker
    assert "warmup_lease" in worker
    assert "mark_llm_stopped" in worker

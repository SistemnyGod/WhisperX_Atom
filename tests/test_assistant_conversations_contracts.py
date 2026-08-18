from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_conversation_migration_is_idempotent_and_preserves_legacy_queries():
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/014_assistant_conversations.sql")
    assert "CREATE TABLE IF NOT EXISTS assistant_conversations" in migration
    assert "CREATE TABLE IF NOT EXISTS assistant_messages" in migration
    assert "ALTER TABLE assistant_queries ADD COLUMN IF NOT EXISTS conversation_id" in migration
    assert "deleted_at" in migration


def test_assistant_context_accepts_ready_and_partial_transcripts():
    worker = read("workers/summary_worker/assistant.py")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert "t.status IN ('READY','PARTIAL_READY')" in worker
    assert "HasUsableTranscriptAsync" in store
    assert "SELECT status FROM meetings WHERE id=@meeting" not in store


def test_assistant_modes_are_additive_and_general_chat_is_transcript_independent():
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/027_assistant_modes.sql")
    worker = read("workers/summary_worker/assistant.py")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "GENERAL_CHAT" in migration and "MEETING_MEMORY" in migration and "CURRENT_MEETING" in migration
    assert "ADD COLUMN IF NOT EXISTS assistant_mode" in migration
    assert 'assistant_mode == "GENERAL_CHAT"' in worker
    assert "assistant_context_empty" in worker  # meeting mode remains source-gated
    assert "request.AssistantMode" in api


def test_assistant_retrieval_uses_russian_fts_and_fails_closed_without_evidence():
    worker = read("workers/summary_worker/assistant.py")
    assert "websearch_to_tsquery('russian'" in worker
    assert "LIMIT 12" in worker and "LIMIT 36" in worker
    assert "LOW_TRANSCRIPT_QUALITY" in worker
    assert "self.repository.persist, query_id, {}, valid" in worker
    assert "claims_are_semantically_grounded" in worker
    assert "snapshot_evidence" in worker
    assert "self.repository.snapshot_evidence, query_id, valid" in worker
    snapshot = read("apps/server/WhisperX.Atom.Api/Migrations/031_assistant_evidence_snapshot.sql")
    assert "snapshot_kind" in snapshot and "RETRIEVED" in snapshot and "CITED" in snapshot


def test_voice_questions_are_blocked_during_capture():
    broker = read("apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs")
    voice = read("apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs")
    assert "ASSISTANT_RECORDING_ACTIVE" in broker
    assert "ASSISTANT_RECORDING_ACTIVE" in voice
    assert "A question must never compete with active capture" in broker


def test_gpu_lease_prioritizes_asr_over_assistant_over_summary():
    lease = read("workers/gpu_lease.py")
    ml = read("workers/ml_worker/worker.py")
    summary = read("workers/summary_worker/worker.py")
    assistant = read("workers/summary_worker/assistant.py")
    assert "Lower values have precedence" in lease
    assert "TRANSCRIBE_ASR" in lease and "assistant_queries" in lease
    assert "priority=10" in ml
    assert "priority=50" in assistant
    assert "priority=100" in summary


def test_conversation_api_is_user_scoped_and_has_message_sse():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'app.MapGet("/api/assistant/conversations"' in api
    assert 'app.MapPost("/api/assistant/conversations/{id:guid}/messages"' in api
    assert 'app.MapGet("/api/assistant/conversations/{conversationId:guid}/messages/{messageId:guid}/events"' in api
    assert "DeleteAssistantConversationAsync" in api
    assert "path.StartsWithSegments(\"/api/assistant/conversations\")" in api


def test_desktop_exposes_persistent_chat_contracts_and_navigation():
    contracts = read("apps/desktop/WhisperX.Atom.Desktop/Services/FrontendContracts.cs")
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    window = read("apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml.cs")
    vm = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/AssistantViewModel.cs")
    assert "DesktopAssistantConversation" in contracts
    assert "GetAssistantConversationsAsync" in client
    assert "assistant" in window
    assert "Conversations" in vm and "Messages" in vm

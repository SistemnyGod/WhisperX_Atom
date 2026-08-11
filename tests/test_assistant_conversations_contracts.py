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

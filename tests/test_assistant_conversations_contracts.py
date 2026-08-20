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


def test_voice_questions_during_capture_use_live_meeting_context():
    broker = read("apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    worker = read("workers/summary_worker/assistant.py")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/033_live_meeting_memory.sql")
    assert 'requestedMode = "LIVE_MEETING"' in broker
    assert '"LIVE_MEETING_NOT_READY"' in broker
    assert '"LIVE_ASR_SEGMENTS"' in broker
    assert '"LIVE_MEETING"' in store and "HasLiveMeetingContextAsync" in store
    assert "def live_context" in worker and '"LIVE_PROVISIONAL"' in worker
    assert "live_meeting_segments" in migration and "assistant_live_query_evidence" in migration


def test_live_mode_never_falls_back_to_canonical_transcript_or_history():
    worker = read("workers/summary_worker/assistant.py")
    assert "self.repository.live_context" in worker
    assert "fresh provisional segments" in worker
    assert "V1/V2" in worker


def test_live_retrieval_fails_closed_without_a_domain_anchor():
    worker = read("workers/summary_worker/assistant.py")
    assert "_LIVE_RETRIEVAL_STOPWORDS" in worker
    assert 'return "", {}, "LIVE_PROVISIONAL", "LIVE_MEETING_NOT_READY"' in worker
    assert "most recent speech as a substitute for evidence" in worker


def test_live_answer_is_explicitly_provisional_in_metadata_and_status():
    worker = read("workers/summary_worker/assistant.py")
    assert 'transcript_kind in {"ASR_DRAFT", "LIVE_PROVISIONAL"}' in worker
    assert '"provisional": transcript_kind == "LIVE_PROVISIONAL"' in worker
    assert '"canonicalTranscript": transcript_kind != "LIVE_PROVISIONAL"' in worker


def test_live_memory_preserves_audio_track_provenance_and_deduplicates_echo():
    worker = read("workers/summary_worker/assistant.py")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/035_live_meeting_track_provenance.sql")
    recorder = read("apps/recorder-host/LiveAudioBroadcaster.cs")
    recorder_contracts = read("apps/recorder-host/LiveAudioContracts.cs")
    voice = read("apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs")
    assert "source_track_type" in migration and "source_track_id" in migration and "channel_role" in migration
    assert "REMOTE_SYSTEM" in worker and "sourceTrackType" in worker
    assert "SequenceMatcher" in worker and "0.85" in worker
    assert "LIVE_AUDIO_V1" in recorder and "QueueCapacityPerTrack" in recorder
    assert "_liveLocalRecognizer" in voice and "_liveRemoteRecognizer" in voice
    live_client = read("apps/voice-host/WhisperX.Atom.Voice.Host/LiveAudioClient.cs")
    assert "WhisperXAtomLiveAudioV1" in live_client
    assert 'JsonPropertyName("localSessionId")' in recorder_contracts
    assert 'JsonPropertyName("localSessionId")' in live_client


def test_live_memory_has_bounded_background_cleanup_and_keeps_inflight_queries_briefly():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "DELETE FROM live_meeting_segments AS segment" in api
    assert "assistant_live_query_evidence" in api
    assert "query.created_at > now()-interval '15 minutes'" in api
    assert "LIVE_MEETING is provisional memory" in api


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

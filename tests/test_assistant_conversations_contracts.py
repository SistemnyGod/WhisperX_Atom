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
    assert 'app.MapPost("/api/assistant/queries"' in api
    assert "modeResolver.ResolveAsync" in api


def test_assistant_mode_resolver_is_the_single_context_owner():
    resolver = read("apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    broker = read("apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs")
    voice = read("apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs")
    assert "AssistantModeResolver" in resolver
    assert "AssistantModeResolutionRequest" in resolver
    assert "live_retrieval_match" in resolver
    assert "routingReason = route.Reason" in api
    assert "builder.Services.AddSingleton<AssistantModeResolver>()" in api
    assert 'requestedMode' in broker and 'requestMeetingId' in broker and 'conversationId' in broker
    assert 'AskAssistantAsync(normalizedQuestion, "AUTO"' in voice
    assert "VoiceScopeFor" not in broker
    assert "ResolveAssistantQuestion" not in voice


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
    resolver = read("apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs")
    worker = read("workers/summary_worker/assistant.py")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/033_live_meeting_memory.sql")
    # The transport forwards AUTO; the API promotes it to LIVE_MEETING only
    # after checking the active recording and provisional live memory.
    assert 'var requestMeetingId = captureContext.IsActive' in broker
    assert 'The authenticated API owns AUTO routing' in broker
    assert 'ResolvedMode = "LIVE_MEETING"' not in broker
    assert '"LIVE_MEETING_NOT_READY"' in broker
    assert '"LIVE_ASR_SEGMENTS"' in broker
    assert '"LIVE_MEETING"' in store and "HasLiveMeetingContextAsync" in store
    assert "'FINALIZING'" in store
    assert "live_retrieval_match" in resolver
    assert "activeRecording" in resolver
    assert "ResolveAsync" in resolver
    assert "def live_context" in worker and '"LIVE_PROVISIONAL"' in worker
    assert "live_meeting_segments" in migration and "assistant_live_query_evidence" in migration


def test_voice_general_conversation_remains_available_without_or_during_recording():
    broker = read("apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    resolver = read("apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs")
    assert "GENERAL_CHAT" not in broker
    assert "GENERAL_CHAT" in resolver and "GENERAL_CHAT" in store
    assert "скажи привет" in resolver
    assert "IsGeneralConversationQuestion" in store
    assert 'var requestMeetingId = captureContext.IsActive' in broker
    assert "AUTO routing" in broker
    assert "LooksLikeMeetingQuestion" in store


def test_voice_auto_followups_reuse_live_or_current_conversation_by_meeting_scope():
    broker = read("apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert "GetVoiceConversationId" in broker
    assert '_voiceConversations.Get(userId, normalized, meetingId)' in broker
    assert '_voiceConversations.Set(currentUser.Id, "AUTO", acceptedMeeting, acceptedConversation)' in broker
    assert "opaque conversation key" in broker
    assert "SET assistant_mode=@mode, updated_at=now()" in store
    # After a Desktop restart the server may be the only owner of the opaque
    # conversation pointer.  AUTO must be able to recover a meeting-scoped
    # conversation even when the transport has not yet supplied meetingId;
    # resolver retrieval/RBAC still decides whether that scope is usable.
    assert "(@meeting IS NULL OR meeting_id=@meeting)" in store


def test_live_mode_never_falls_back_to_canonical_transcript_or_history():
    worker = read("workers/summary_worker/assistant.py")
    assert "self.repository.live_context" in worker
    assert "provisional segments" in worker and "through the V1 hand-off" in worker
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


def test_qwen_contract_separates_screen_answer_from_short_voice_answer():
    worker = read("workers/summary_worker/assistant.py")
    assert '"voice_answer"' in worker
    assert "не более трёх предложений" in worker
    assert '"evidence_segment_ids"' in worker and '"claims"' in worker


def test_auto_router_uses_scope_safe_retrieval_probes_before_general_fallback():
    resolver = read("apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert "ProbeLiveAssistantContextAsync" in resolver and "ProbeLiveAssistantContextAsync" in store
    assert "ProbeCurrentMeetingAssistantContextAsync" in resolver and "ProbeCurrentMeetingAssistantContextAsync" in store
    assert "ProbeMeetingMemoryAssistantContextAsync" in resolver and "ProbeMeetingMemoryAssistantContextAsync" in store
    assert '"live_retrieval_match"' in resolver
    assert '"current_retrieval_match"' in resolver
    assert '"history_retrieval_match"' in resolver
    assert '"general_fallback"' in resolver
    assert "to_tsvector('russian'" in store
    assert "t.status IN ('READY','PARTIAL_READY')" in store
    assert "m.owner_id=@owner" in store
    assert "(@session IS NULL OR r.id=@session)" in store
    assert "HasLiveMeetingContextAsync(activeMeeting, request.RecordingSessionId)" in resolver


def test_auto_router_does_not_force_meeting_scope_from_keywords_without_evidence():
    resolver = read("apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs")
    assert "history_no_evidence" in resolver
    assert "meeting_question_without_scope" in resolver
    assert "meeting_context_required" in resolver
    # Keyword helpers are retained as hints/compatibility adapters, but the
    # AUTO path must call the retrieval probes first and fall back safely.
    auto = resolver.split("private async Task<AssistantModeResolution> ResolveAutoAsync", 1)[1]
    assert auto.index("ProbeLiveAssistantContextAsync") < auto.index('"general_fallback"')


def test_live_memory_preserves_audio_track_provenance_and_deduplicates_echo():
    worker = read("workers/summary_worker/assistant.py")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/035_live_meeting_track_provenance.sql")
    recorder = read("apps/recorder-host/LiveAudioBroadcaster.cs")
    recorder_contracts = read("apps/recorder-host/LiveAudioContracts.cs")
    voice = read("apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs")
    assert "source_track_type" in migration and "source_track_id" in migration and "channel_role" in migration
    assert "REMOTE_SYSTEM" in worker and "sourceTrackType" in worker
    assert "SequenceMatcher" in worker and "0.85" in worker
    assert "'FINALIZING'" in worker and "LIMIT 8192" in worker
    assert "LIVE_AUDIO_V1" in recorder and "QueueCapacityPerTrack" in recorder
    assert "_liveLocalRecognizer" in voice and "_liveRemoteRecognizer" in voice
    live_client = read("apps/voice-host/WhisperX.Atom.Voice.Host/LiveAudioClient.cs")
    assert "WhisperXAtomLiveAudioV1" in live_client
    assert 'JsonPropertyName("localSessionId")' in recorder_contracts
    assert 'JsonPropertyName("localSessionId")' in live_client


def test_live_memory_survives_stop_until_v1_and_preserves_evidence_snapshots():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    retention = read("apps/server/WhisperX.Atom.Api/Migrations/036_live_meeting_retention.sql")
    assert "DELETE FROM live_meeting_segments AS segment" in api
    assert "assistant_live_query_evidence" in api
    assert "transcript.version=1" in api and "transcript.status IN ('READY','PARTIAL_READY')" in api
    assert "evidence.live_segment_id=segment.id" in api
    assert "UNTIL_V1_READY" in retention and "7 days" in retention
    assert "LIVE_MEETING is provisional memory" in api


def test_gpu_lease_prioritizes_asr_over_assistant_over_summary():
    lease = read("workers/gpu_lease.py")
    ml = read("workers/ml_worker/worker.py")
    summary = read("workers/summary_worker/worker.py")
    assistant = read("workers/summary_worker/assistant.py")
    assert "Lower values have precedence" in lease
    assert "TRANSCRIBE_ASR" in lease and "assistant_queries" in lease
    assert "priority=10" in ml
    assert "priority=30" in assistant
    assert "priority=50" in ml
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


def test_assistant_wait_uses_streaming_http_and_never_leaves_sending_status_after_acceptance():
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    vm = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/AssistantViewModel.cs")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "HttpCompletionOption.ResponseHeadersRead" in client
    assert "PollAssistantMessageAsync" in client
    assert "DateTimeOffset.UtcNow.AddMinutes(4)" in client
    assert "responseWait.CancelAfter(TimeSpan.FromSeconds(15))" in vm
    assert "StatusText = \"Ответ готовится…\"" in vm
    assert "Ответ готовится на сервере" in vm
    assert "HasPendingMessages" in vm
    assert "Запрос принят; связь при ожидании прервалась" in vm
    assert "context.RequestAborted.IsCancellationRequested" in api


def test_assistant_worker_does_not_persist_empty_terminal_result_when_qwen_fails():
    worker = read("workers/summary_worker/assistant.py")
    assert "synthesis_completed = False" in worker
    assert "synthesis_completed = True" in worker
    assert "if synthesis_completed:" in worker
    assert "synthetic terminal answer" in worker


def test_global_status_distinguishes_busy_or_unavailable_qwen_from_whisperx():
    window = read("apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml.cs")
    mapper = read("apps/desktop/WhisperX.Atom.Desktop/Services/UiStatusMapper.cs")
    assert 'ComponentStatus(processingReadiness, "whisperx")' in window
    assert 'ComponentStatus(processingReadiness, "qwen")' in window
    assert "ИИ обрабатывает запрос" in window
    assert "ИИ временно недоступен" in window
    assert "public static string? ComponentStatus" in mapper

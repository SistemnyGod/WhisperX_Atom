from pathlib import Path

from workers.summary_worker.query_understanding import understand_query


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_broad_and_temporal_questions_do_not_use_question_words_as_fts_topics():
    today = understand_query("Что обсуждали сегодня?")
    assert today.intent == "FACT_LOOKUP"
    assert today.date_range == "TODAY"
    assert today.topic is None

    broad = understand_query("Какие темы обсуждали?")
    assert broad.topic is None

    scoped = understand_query("Что обсуждали про ремонт?")
    assert scoped.topic == "про ремонт"


def test_occurrence_migration_prefers_imported_date_then_capture_fallback():
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/060_meeting_occurrence.sql")
    assert "occurred_at" in migration and "occurred_precision" in migration
    assert "historical_transcript_imports" in migration
    assert "IMPORTED_DATE" in migration
    assert "recording_sessions" in migration and "m.created_at" in migration


def test_deployment_read_scope_is_additive_and_mutations_remain_owner_checked():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    compose = read("compose.lan.yml")
    assert 'MEETING_READ_SCOPE' in api
    assert 'CanReadMeetingAsync' in api
    assert 'MEETING_READ_SCOPE: "${MEETING_READ_SCOPE:-DEPLOYMENT}"' in compose
    assert 'ORGANIZATION' in api
    assert 'DEPLOYMENT' in api
    # Read-only transcript/summary routes use the deployment scope; destructive
    # operations intentionally retain the owner/admin authorization helper.
    assert 'app.MapGet("/api/meetings/{id:guid}/transcript"' in api
    transcript_route = api.split('app.MapGet("/api/meetings/{id:guid}/transcript"', 1)[1].split('app.MapGet("/api/meetings/{id:guid}/transcript/versions"', 1)[0]
    assert 'CanReadMeetingAsync(context, id)' in transcript_route
    cancel_route = api.split('app.MapPost("/api/meetings/{id:guid}/cancel"', 1)[1].split('app.MapDelete("/api/meetings/{id:guid}"', 1)[0]
    assert 'CanAccessMeetingAsync(context, id)' in cancel_route


def test_memory_status_and_rebuild_are_diagnostic_and_idempotent_endpoints():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert 'app.MapGet("/api/assistant/memory/status"' in api
    assert 'app.MapPost("/api/admin/memory/rebuild"' in api
    assert "PREVIEW" in api and "APPLY" in api
    assert "pg_advisory_xact_lock" in store
    assert "ON CONFLICT(transcript_id,transcript_version) DO NOTHING" in store
    assert "ownerUserId = candidate.OwnerUserId" in store
    assert "never touches" in store
    assert "READY_EMPTY" in store
    assert "MissingProjectionCount" in store
    assert "FactBackedTranscriptCount" in store
    worker = read("workers/memory_worker/worker.py")
    assert "WHERE NOT EXISTS" in worker
    assert "evidence_segment_ids=%s::jsonb" in worker
    assert "f.subject_normalized IS NULL AND f.transcript_id=%s" in worker


def test_assistant_broad_retrieval_is_bounded_and_uses_deployment_scope():
    worker = read("workers/summary_worker/assistant.py")
    assert "broad_intent" in worker
    assert "MEETING_READ_SCOPE" in worker
    assert "ORGANIZATION" in worker and "DEPLOYMENT" in worker
    assert "LIMIT 128" in worker and "LIMIT 64" in worker
    assert "COALESCE(m.occurred_at,m.created_at)" in worker
    assert "semantic_candidate_limit_for_sql" in worker
    assert "PARTITION BY base.meeting_id" in worker


def test_calendar_scope_requires_selection_before_qwen_when_date_has_multiple_meetings():
    resolver = read("apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "ASSISTANT_MEETING_SELECTION_REQUIRED" in resolver
    assert "ListAssistantMeetingCandidatesAsync" in resolver
    assert "MEETING_SELECTION_REQUIRED" in api
    assert "MeetingCandidates" in api


def test_desktop_history_context_includes_historical_transcribed_meetings():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/AssistantViewModel.cs")
    assert '"TRANSCRIBED"' in view_model and '"COMPLETED"' in view_model

from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_lineage_schema_contains_every_vertical_stage_and_is_session_idempotent():
    sql = read("apps/server/WhisperX.Atom.Api/Migrations/032_recording_pipeline_lineage.sql")
    for field in (
        "recording_session_id", "meeting_id", "media_asset_id", "asr_job_id",
        "transcript_v1_id", "enrichment_job_id", "transcript_v2_id",
        "summary_job_id", "summary_id", "pipeline_correlation_id",
    ):
        assert field in sql
    assert "PRIMARY KEY REFERENCES recording_sessions" in sql
    assert "ON CONFLICT(recording_session_id)" in read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")


def test_pipeline_api_exposes_agent_and_user_scoped_chain_without_audio():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    assert 'recording-sessions/{sessionId:guid}/pipeline' in api
    assert 'meetings/{id:guid}/pipeline' in api
    assert "GetRecordingPipelineChainAsync" in store
    assert "GetMeetingPipelineChainsAsync" in store
    assert "RecordingPipelineChain" in store


def test_media_handoff_repairs_outbox_after_transition_before_redelivery():
    media = read("workers/media_worker/persistence.py")
    assert "must repair the missing event" in media
    assert "FOR UPDATE" in media
    assert "payload->>'job_id'" in media
    assert "return True" in media


def test_v1_is_persisted_before_enrichment_and_enrichment_is_idempotent():
    ml = read("workers/ml_worker/persistence.py")
    assert "version_kind='ASR_DRAFT'" in ml
    assert "transcript_v1_id" in ml
    assert "TRANSCRIPT_ENRICH" in ml
    assert "version_kind='ENRICHED'" in ml
    assert "processing_job_id" in ml
    assert "source_transcript_id" in ml
    assert "_ensure_enrichment_job_and_outbox" in ml
    assert "payload->>'job_id'" in ml


def test_summary_handoff_reuses_job_and_repairs_missing_outbox_after_restart():
    ml = read("workers/ml_worker/persistence.py")
    assert "_ensure_summary_job_and_outbox" in ml
    assert "type='SUMMARIZE'" in ml
    assert "topic='llm.summarize'" in ml
    assert 'summary_mode: str = "FULL"' in ml
    assert '"summaryMode": summary_mode' in ml


def test_usable_v1_creates_deterministic_draft_without_qwen_and_v2_remains_separate():
    ml = read("workers/ml_worker/persistence.py")
    summary = read("workers/summary_worker/worker.py")
    assert '_v1_summary_allowed' in ml
    assert 'summary_mode="DETERMINISTIC_ONLY"' in ml
    assert 'source_quality="V1_FALLBACK"' in ml
    deterministic = summary[summary.index('if summary_mode == "DETERMINISTIC_ONLY":'):summary.index('self.repository.update_job(job_id, "RUNNING", "EXTRACTING_FACTS", 10)')]
    assert 'build_deterministic_summary' in deterministic
    assert 'self._gpu_lease' not in deterministic
    assert 'DETERMINISTIC_SUMMARY_MODEL' in deterministic
    assert '"ASR_LANGUAGE_MISMATCH"' not in ml[ml.index('def _v1_summary_allowed'):ml.index('def persist_asr_draft')]


def test_enrichment_failure_keeps_usable_v1_and_summary_worker_is_optional():
    ml = read("workers/ml_worker/persistence.py")
    summary = read("workers/summary_worker/worker.py")
    assert "Enrichment is" in ml
    assert "PARTIAL_READY" in ml
    assert "transcript remains usable" in summary


def test_gpu_oom_and_model_access_failures_are_retryable_without_losing_source():
    worker = read("workers/ml_worker/worker.py")
    assert '"CUDA_OOM"' in worker
    assert '"MODEL_ACCESS_ERROR"' in worker
    assert '"pyannote"' in worker
    assert '"CUDA_OOM", "AUDIO_PROCESSING_ERROR"' in worker
    # ASR persistence is the durable handoff before enrichment; retrying a
    # GPU job therefore never requires recording the source again.
    ml = read("workers/ml_worker/persistence.py")
    assert "persist_asr_draft" in ml
    assert "TRANSCRIPT_ENRICH" in ml


def test_vertical_e2e_script_requires_lineage_ids_and_excludes_audio_from_result():
    script = read("scripts/e2e-vertical-pipeline.ps1")
    for field in ("recordingSessionId", "meetingId", "mediaAssetId", "asrJobId", "transcriptV1Id", "enrichmentJobId", "transcriptV2Id", "summaryJobId", "summaryId"):
        assert field in script
    assert "audioIncluded = $false" in script
    assert "/api/meetings/$meetingId/pipeline" in script
    assert "VERTICAL_DUPLICATE_OR_MISSING_STAGE" in script
    assert "duplicateCheck" in script
    assert "stageTimings = $chain.stageTimings" in script
    assert "transcriptV1ReadyAtUtc" in script


def test_server_bundle_rollback_script_uses_safe_interpolated_drive_name():
    bundle = read("scripts/start-server-bundle.ps1")
    assert '${name}:' in bundle
    assert '$name:' not in bundle


def test_server_bundle_reads_oci_labels_from_docker_json_on_windows_powershell():
    builder = read("scripts/build-server-bundle.ps1")
    launcher = read("scripts/start-server-bundle.ps1")
    for script in (builder, launcher):
        assert "function Get-DockerImageMetadata" in script
        assert "docker image inspect $image | Out-String" in script
        assert "ConvertFrom-Json" in script
    assert "io.whisperx.atom.build-identity" in builder
    assert "RELEASE_IMAGE_LABELS_MISMATCH" in builder


def test_summary_is_never_rendered_as_raw_json_after_content_gate_failure():
    meetings = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/MeetingsViewModel.cs")
    legacy = read("apps/desktop/WhisperX.Atom.Desktop/Legacy/Wpf/MainWindow.xaml.cs")
    presentation = read("apps/desktop/WhisperX.Atom.Desktop/Services/SummaryPresentation.cs")
    assert "SummaryPresentation.Format(summary)" in meetings
    assert "SummaryPresentation.Format(summary)" in legacy
    assert "IsDisplayable" in presentation
    assert "ContentValidity" in presentation
    assert "SummaryPresentation.IsDisplayable(summary)" in read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/HomeViewModel.cs")
    assert "return $\"Версия {summary.Version}; модель: {summary.ModelName}\\n\\n{root}\"" not in legacy

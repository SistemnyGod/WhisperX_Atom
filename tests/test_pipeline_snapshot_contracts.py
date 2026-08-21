from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_server_exposes_canonical_pipeline_snapshot_without_replacing_lineage():
    snapshot = read("apps/server/WhisperX.Atom.Api/PipelineSnapshot.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")

    for field in (
        "RecordingPipelineSnapshot",
        "OverallStatus",
        "BlockedBy",
        "Retryable",
        "StageTimings",
        "PARTIAL_READY",
        "SUMMARY_WORKER",
        "Summary is an optional enrichment",
        "CreatedAt",
        "UpdatedAt",
        "Recording",
    ):
        assert field in snapshot
    assert "public RecordingPipelineSnapshot Snapshot" in store
    assert "public RecordingPipelineSnapshot Snapshot =>" in store
    assert "GetPipelineWorkerReadinessAsync" in store
    assert "SnapshotOverride" in read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'meetings/{id:guid}/pipeline' in api
    assert "RecordingState" in store


def test_desktop_consumes_snapshot_with_job_fallback_for_rolling_compatibility():
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    backend = read("apps/desktop/WhisperX.Atom.Desktop/Services/BackendService.cs")
    contracts = read("apps/desktop/WhisperX.Atom.Desktop/Services/FrontendContracts.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/MeetingsViewModel.cs")

    assert "DesktopPipelineSnapshot" in client
    assert "GetMeetingPipelineAsync" in client
    assert "GetMeetingPipelineAsync" in backend
    assert "GetMeetingPipelineAsync" in contracts
    assert "PipelineSnapshot" in view_model
    assert "FormatPipelineSnapshot" in view_model
    assert "LatestJob is null" in view_model


def test_recording_view_consumes_server_snapshot_for_processing_status():
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")

    assert "GetMeetingPipelineAsync" in view_model
    assert "PipelineSnapshot =" in view_model
    assert "CurrentPipelineProgress" in view_model
    assert "FormatPipelineSnapshot" in view_model
    # Jobs remain available for the legacy SSE/stall path, but must not
    # overwrite the server-owned status when a snapshot is available.
    assert "if (PipelineSnapshot is null)" in view_model


def test_recorder_events_feed_server_owned_pipeline_timeline_without_changing_lineage():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")

    assert "RecordPipelineEventTimingAsync" in store
    assert "pipelineEvents" in store
    assert "pipelineDurations" in store
    for key in (
        "local_ready_to_flac_ms",
        "flac_to_upload_started_ms",
        "upload_ms",
        "finalize_ms",
        "media_assembly_ms",
    ):
        assert key in store
    # Event insertion remains idempotent; timing is only folded after a new
    # event row is accepted, so retries cannot inflate a duration.
    assert "if (inserted > 0)" in store


def test_vertical_gate_persists_snapshot_and_benchmark_supports_warm_runs():
    vertical = read("scripts/e2e-vertical-pipeline.ps1")
    benchmark = read("scripts/e2e-transcription-benchmark.ps1")
    matrix = read("scripts/e2e-transcription-matrix.ps1")
    assert "pipelineSnapshot = $chain.snapshot" in vertical
    assert "pipelineOverallStatus" in vertical
    assert "for ($index = 1; $index -le $Runs; $index++)" in benchmark
    assert "stageTimings = $report.stageTimings" in benchmark
    assert "audioIncluded = $false" in benchmark
    assert '"10x10"' in matrix and '"30x5"' in matrix and '"60x5"' in matrix and '"600x3"' in matrix
    assert "e2e-transcription-benchmark.ps1" in matrix
    assert "credentialsIncluded = $false" in matrix


def test_home_uses_bounded_meeting_metrics_aggregate_with_rolling_fallback():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    home = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/HomeViewModel.cs")
    assert '"/api/meetings/metrics"' in api
    assert "Take(50)" in api
    assert "GetMeetingMetricsAsync" in client and "MEETING_METRICS_UNSUPPORTED" in client
    assert "GetMeetingMetricsAsync" in home and "LoadMeetingMetricsAsync" in home


def test_recorder_upload_context_avoids_binding_and_correlation_n_plus_one():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    client = read("apps/recorder-agent/AgentApiClient.cs")
    assert "PendingChunkUploadContext" in spool
    assert "PendingChunksWithUploadContextAsync" in spool
    assert "JOIN server_bindings" in spool
    assert "pipeline_correlation_id" in spool
    assert "PendingChunksWithUploadContextAsync(localSessionId" in client
    assert "context.PipelineCorrelationId" in client

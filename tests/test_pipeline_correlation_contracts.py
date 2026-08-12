from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_pipeline_correlation_is_created_once_and_trace_is_request_scoped():
    coordinator = read("apps/recorder-agent/RecordingCoordinator.cs")
    agent = read("apps/recorder-agent/AgentApiClient.cs")
    spool = read("apps/recorder-agent/SpoolStore.cs")
    assert 'var pipelineCorrelationId = Guid.NewGuid().ToString("N")' in coordinator
    assert "pipeline_correlation_id" in spool
    assert 'X-Trace-Id", Guid.NewGuid().ToString("N")' in agent
    assert 'X-Correlation-Id", pipelineCorrelationId' in agent


def test_recording_outbox_and_media_handoff_keep_correlation():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    worker = read("workers/media_worker/worker.py")
    assert "correlation_id = await GetPipelineCorrelationIdAsync" in store
    assert "correlation_id" in api
    assert 'next_message = dict(payload)' in worker
    assert 'correlation_id = payload.get("correlation_id")' in worker


def test_diagnostics_returns_real_session_ids_and_persisted_timings():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    media = read("workers/media_worker/persistence.py")
    assert "ListRecordingCorrelationAsync" in api
    assert "PipelineCorrelationId" in api and "stages = item.Timings.RootElement" in api
    assert "stage_timings" in store and "record_stage_timing" in media

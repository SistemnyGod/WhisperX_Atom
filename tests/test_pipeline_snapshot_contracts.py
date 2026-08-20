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
    ):
        assert field in snapshot
    assert "public RecordingPipelineSnapshot Snapshot" in store
    assert "public RecordingPipelineSnapshot Snapshot =>" in store
    assert "GetPipelineWorkerReadinessAsync" in store
    assert "SnapshotOverride" in read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'meetings/{id:guid}/pipeline' in api


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


def test_vertical_gate_persists_snapshot_and_benchmark_supports_warm_runs():
    vertical = read("scripts/e2e-vertical-pipeline.ps1")
    benchmark = read("scripts/e2e-transcription-benchmark.ps1")
    assert "pipelineSnapshot = $chain.snapshot" in vertical
    assert "pipelineOverallStatus" in vertical
    assert "for ($index = 1; $index -le $Runs; $index++)" in benchmark
    assert "stageTimings = $report.stageTimings" in benchmark
    assert "audioIncluded = $false" in benchmark

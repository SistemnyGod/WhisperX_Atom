using Xunit;

public sealed class PipelineSnapshotSemanticsTests
{
    private static RecordingPipelineChain Chain(
        string? mediaStatus = "READY",
        string? asrStatus = "READY",
        string? enrichmentStatus = "READY",
        string? v1Status = "READY",
        string? v2Status = "READY",
        string? summaryJobStatus = null,
        string? summaryStatus = null)
    {
        var session = Guid.NewGuid();
        return new RecordingPipelineChain(
            session,
            Guid.NewGuid(),
            Guid.NewGuid(), mediaStatus,
            Guid.NewGuid(), asrStatus, asrStatus,
            Guid.NewGuid(), v1Status,
            Guid.NewGuid(), enrichmentStatus, enrichmentStatus,
            Guid.NewGuid(), v2Status,
            summaryJobStatus is null ? null : Guid.NewGuid(), summaryJobStatus, summaryJobStatus,
            summaryStatus is null ? null : Guid.NewGuid(), summaryStatus,
            "test-correlation");
    }

    [Fact]
    public void QueuedReadyWorkerIsWaitingButNotBlocked()
    {
        var snapshot = RecordingPipelineSnapshotResolver.Resolve(
            Chain(asrStatus: "QUEUED"),
            new Dictionary<string, bool> { ["gpu-worker"] = true });

        Assert.Equal("WAITING", snapshot.OverallStatus);
        Assert.Equal("ASR", snapshot.CurrentStage);
        Assert.Null(snapshot.BlockedBy);
        Assert.False(snapshot.Retryable);
    }

    [Fact]
    public void QueuedUnavailableWorkerIsDegradedAndRetryable()
    {
        var snapshot = RecordingPipelineSnapshotResolver.Resolve(
            Chain(asrStatus: "QUEUED"),
            new Dictionary<string, bool> { ["gpu-worker"] = false });

        Assert.Equal("DEGRADED", snapshot.OverallStatus);
        Assert.Equal("GPU_WORKER", snapshot.BlockedBy);
        Assert.True(snapshot.Retryable);
        Assert.Equal("GPU_WORKER_UNAVAILABLE", snapshot.ErrorCode);
    }

    [Fact]
    public void RunningJobIsProcessingEvenWhenWorkerReadinessIsFalse()
    {
        var snapshot = RecordingPipelineSnapshotResolver.Resolve(
            Chain(asrStatus: "RUNNING"),
            new Dictionary<string, bool> { ["gpu-worker"] = false });

        Assert.Equal("PROCESSING", snapshot.OverallStatus);
        Assert.Equal("ASR", snapshot.CurrentStage);
        Assert.Null(snapshot.BlockedBy);
        Assert.False(snapshot.Retryable);
    }

    [Fact]
    public void SummaryJobStateWinsOverDraftRow()
    {
        var snapshot = RecordingPipelineSnapshotResolver.Resolve(
            Chain(summaryJobStatus: "RUNNING", summaryStatus: "DRAFT"),
            new Dictionary<string, bool> { ["summary-worker"] = false });

        Assert.Equal("PARTIAL_READY", snapshot.OverallStatus);
        Assert.Equal("SUMMARY", snapshot.CurrentStage);
        Assert.Equal("RUNNING", snapshot.Summary.Status);
        Assert.Null(snapshot.BlockedBy);
        Assert.False(snapshot.Retryable);
    }

    [Fact]
    public void SummaryNeedsReviewIsTerminalPartialResult()
    {
        var snapshot = RecordingPipelineSnapshotResolver.Resolve(
            Chain(summaryStatus: "NEEDS_REVIEW"),
            new Dictionary<string, bool> { ["summary-worker"] = false });

        Assert.Equal("PARTIAL_READY", snapshot.OverallStatus);
        Assert.Equal("SUMMARY_REVIEW", snapshot.CurrentStage);
        Assert.Equal("NEEDS_REVIEW", snapshot.Summary.Status);
        Assert.Null(snapshot.BlockedBy);
        Assert.False(snapshot.Retryable);
    }
}

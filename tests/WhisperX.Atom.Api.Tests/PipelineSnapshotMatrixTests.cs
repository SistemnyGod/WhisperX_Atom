using Xunit;

public sealed class PipelineSnapshotMatrixTests
{
    private static RecordingPipelineChain Chain(
        string media = "READY",
        string asr = "READY",
        string v1 = "READY",
        string enrichment = "READY",
        string v2 = "READY",
        string? summaryJob = null,
        string? summary = null)
    {
        return new RecordingPipelineChain(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), media,
            Guid.NewGuid(), asr, asr,
            Guid.NewGuid(), v1,
            Guid.NewGuid(), enrichment, enrichment,
            Guid.NewGuid(), v2,
            summaryJob is null ? null : Guid.NewGuid(), summaryJob, summaryJob,
            summary is null ? null : Guid.NewGuid(), summary,
            "matrix");
    }

    public static IEnumerable<object[]> States()
    {
        yield return new object[] { "media-running", Chain(media: "RUNNING"), new Dictionary<string, bool>(), "PROCESSING", "MEDIA", null!, false };
        yield return new object[] { "asr-queued-ready", Chain(asr: "QUEUED"), new Dictionary<string, bool> { ["gpu-worker"] = true }, "WAITING", "ASR", null!, false };
        yield return new object[] { "asr-queued-unavailable", Chain(asr: "QUEUED"), new Dictionary<string, bool> { ["gpu-worker"] = false }, "DEGRADED", "ASR", "GPU_WORKER", true };
        yield return new object[] { "asr-running", Chain(asr: "RUNNING"), new Dictionary<string, bool> { ["gpu-worker"] = false }, "PROCESSING", "ASR", null!, false };
        yield return new object[] { "enrichment-running-after-v1", Chain(enrichment: "RUNNING"), new Dictionary<string, bool> { ["gpu-worker"] = false }, "PARTIAL_READY", "ENRICHMENT", null!, false };
        yield return new object[] { "summary-running-after-v2", Chain(summaryJob: "RUNNING", summary: "DRAFT"), new Dictionary<string, bool> { ["summary-worker"] = false }, "PARTIAL_READY", "SUMMARY", null!, false };
        yield return new object[] { "summary-needs-review", Chain(summary: "NEEDS_REVIEW"), new Dictionary<string, bool> { ["summary-worker"] = false }, "PARTIAL_READY", "SUMMARY_REVIEW", null!, false };
        yield return new object[] { "summary-failed-after-v1", Chain(summaryJob: "FAILED", summary: "DRAFT"), new Dictionary<string, bool>(), "PARTIAL_READY", "SUMMARY", "SUMMARY_WORKER", true };
        yield return new object[] { "complete", Chain(), new Dictionary<string, bool>(), "READY", "TRANSCRIPT_V2", null!, false };
    }

    [Theory]
    [MemberData(nameof(States))]
    public void ResolverUsesSemanticStageMatrix(
        string _, RecordingPipelineChain chain, IReadOnlyDictionary<string, bool> readiness,
        string overall, string currentStage, string? blockedBy, bool retryable)
    {
        var snapshot = RecordingPipelineSnapshotResolver.Resolve(chain, readiness);

        Assert.Equal(overall, snapshot.OverallStatus);
        Assert.Equal(currentStage, snapshot.CurrentStage);
        Assert.Equal(blockedBy, snapshot.BlockedBy);
        Assert.Equal(retryable, snapshot.Retryable);
    }

    [Fact]
    public void UnknownStageDoesNotBecomeWorkerBlocked()
    {
        var snapshot = RecordingPipelineSnapshotResolver.Resolve(
            Chain(asr: "NOT_STARTED"),
            new Dictionary<string, bool> { ["gpu-worker"] = false });

        Assert.Equal("WAITING", snapshot.OverallStatus);
        Assert.Null(snapshot.BlockedBy);
        Assert.False(snapshot.Retryable);
    }
}

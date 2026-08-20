using System.Text.Json;

/// <summary>
/// Stable, server-owned view of one recording pipeline.  The underlying
/// lineage row remains the durable source; this DTO is only the normalized
/// state contract consumed by Desktop, diagnostics and future clients.
/// </summary>
public sealed record PipelineStageSnapshot(
    string Status,
    string? Stage = null,
    int? Progress = null,
    Guid? Id = null,
    string? ErrorCode = null);

public sealed record RecordingPipelineSnapshot(
    Guid RecordingSessionId,
    Guid MeetingId,
    string? PipelineCorrelationId,
    string OverallStatus,
    string CurrentStage,
    string? BlockedBy,
    bool Retryable,
    string? ErrorCode,
    PipelineStageSnapshot Delivery,
    PipelineStageSnapshot Media,
    PipelineStageSnapshot Asr,
    PipelineStageSnapshot TranscriptV1,
    PipelineStageSnapshot Enrichment,
    PipelineStageSnapshot TranscriptV2,
    PipelineStageSnapshot Summary,
    JsonDocument? StageTimings = null,
    DateTime? CreatedAt = null,
    DateTime? UpdatedAt = null);

public static class RecordingPipelineSnapshotResolver
{
    public static RecordingPipelineSnapshot Resolve(RecordingPipelineChain chain, IReadOnlyDictionary<string, bool>? workerReadiness = null)
    {
        var delivery = new PipelineStageSnapshot(
            chain.MediaAssetId is null ? "PENDING" : "CONFIRMED",
            chain.MediaAssetId is null ? "WAITING_FOR_UPLOAD" : "UPLOAD_CONFIRMED",
            chain.MediaAssetId is null ? 0 : 100,
            chain.MediaAssetId);
        var media = Stage(chain.MediaStatus, chain.MediaAssetId, "MEDIA_ASSEMBLY");
        var asr = Stage(chain.AsrJobStatus, chain.AsrJobId, chain.AsrJobStage);
        var v1 = Stage(chain.TranscriptV1Status, chain.TranscriptV1Id, "ASR_DRAFT");
        var enrichment = Stage(chain.EnrichmentJobStatus, chain.EnrichmentJobId, chain.EnrichmentJobStage);
        var v2 = Stage(chain.TranscriptV2Status, chain.TranscriptV2Id, "ENRICHED");
        var summary = Stage(chain.SummaryStatus ?? chain.SummaryJobStatus, chain.SummaryId, chain.SummaryJobStage);
        var baseSnapshot = new RecordingPipelineSnapshot(
            chain.RecordingSessionId, chain.MeetingId, chain.PipelineCorrelationId,
            "PROCESSING", "MEDIA", null, false, null,
            delivery, media, asr, v1, enrichment, v2, summary, chain.StageTimings, chain.CreatedAt, chain.UpdatedAt);

        if (!IsReady(media.Status) && !IsWorkerReady(workerReadiness, "media-worker"))
            return Waiting(baseSnapshot, "MEDIA", "MEDIA_WORKER", "MEDIA_WORKER_UNAVAILABLE");
        if (IsFailed(media.Status))
            return FailedOrPartial(baseSnapshot, "MEDIA", "MEDIA_FAILED");
        if (!IsReady(media.Status))
            return Waiting(baseSnapshot, "MEDIA", "MEDIA_ASSEMBLY", "MEDIA_NOT_READY");
        if (!IsReady(asr.Status) && !IsWorkerReady(workerReadiness, "gpu-worker"))
            return Waiting(baseSnapshot, "ASR", "GPU_WORKER", "GPU_WORKER_UNAVAILABLE");
        if (IsFailed(asr.Status))
            return FailedOrPartial(baseSnapshot, "ASR", "ASR_FAILED");
        if (!IsReady(asr.Status))
            return Waiting(baseSnapshot, "ASR", "GPU_WORKER", "ASR_WAITING_FOR_WORKER");
        if (IsFailed(v1.Status))
            return FailedOrPartial(baseSnapshot, "TRANSCRIPT_V1", "TRANSCRIPT_V1_FAILED");
        if (!IsReady(v1.Status))
            return Waiting(baseSnapshot, "TRANSCRIPT_V1", "ASR", "TRANSCRIPT_V1_NOT_READY");
        if (IsFailed(enrichment.Status))
            return FailedOrPartial(baseSnapshot, "ENRICHMENT", "ENRICHMENT_FAILED");
        if (!IsReady(v2.Status) && !IsWorkerReady(workerReadiness, "gpu-worker"))
            return Waiting(baseSnapshot, "ENRICHMENT", "GPU_WORKER", "GPU_WORKER_UNAVAILABLE");
        if (!IsReady(v2.Status) && !IsFailed(enrichment.Status))
            return Waiting(baseSnapshot, "ENRICHMENT", "ENRICHMENT_WORKER", "ENRICHMENT_NOT_READY");
        if (IsFailed(v2.Status))
            return FailedOrPartial(baseSnapshot, "TRANSCRIPT_V2", "TRANSCRIPT_V2_FAILED");
        if (!IsReady(v2.Status))
            return Waiting(baseSnapshot, "TRANSCRIPT_V2", "ENRICHMENT", "TRANSCRIPT_V2_NOT_READY");
        // Summary is an optional enrichment of a usable V2.  When the
        // feature is disabled there is no summary job/row, which must not
        // make the canonical recording pipeline look stuck.
        if (chain.SummaryJobId is null
            && chain.SummaryId is null
            && string.IsNullOrWhiteSpace(chain.SummaryJobStatus)
            && string.IsNullOrWhiteSpace(chain.SummaryStatus))
            return baseSnapshot with { OverallStatus = "READY", CurrentStage = "TRANSCRIPT_V2" };
        if (!IsReady(summary.Status) && !IsWorkerReady(workerReadiness, "summary-worker"))
            return Waiting(baseSnapshot, "SUMMARY", "SUMMARY_WORKER", "SUMMARY_WORKER_UNAVAILABLE");
        if (IsFailed(summary.Status))
            return baseSnapshot with { OverallStatus = "PARTIAL_READY", CurrentStage = "SUMMARY", BlockedBy = "SUMMARY_WORKER", Retryable = true, ErrorCode = "SUMMARY_FAILED" };
        if (!IsReady(summary.Status))
            return Waiting(baseSnapshot, "SUMMARY", "SUMMARY_WORKER", "SUMMARY_NOT_READY");

        return baseSnapshot with { OverallStatus = "READY", CurrentStage = "COMPLETE" };
    }

    private static RecordingPipelineSnapshot Waiting(
        RecordingPipelineSnapshot snapshot,
        string stage,
        string blockedBy,
        string errorCode)
    {
        var v1Usable = IsReady(snapshot.TranscriptV1.Status);
        var partialStages = stage is "ENRICHMENT" or "TRANSCRIPT_V2" or "SUMMARY";
        return snapshot with
        {
            OverallStatus = v1Usable && partialStages ? "PARTIAL_READY" : "WAITING",
            CurrentStage = stage,
            BlockedBy = blockedBy,
            Retryable = true,
            ErrorCode = errorCode
        };
    }

    private static RecordingPipelineSnapshot FailedOrPartial(
        RecordingPipelineSnapshot snapshot,
        string stage,
        string errorCode)
    {
        var usableTranscript = IsReady(snapshot.TranscriptV1.Status);
        return snapshot with
        {
            OverallStatus = usableTranscript ? "PARTIAL_READY" : "FAILED",
            CurrentStage = stage,
            BlockedBy = stage switch
            {
                "ASR" => "GPU_WORKER",
                "ENRICHMENT" or "TRANSCRIPT_V2" => "ENRICHMENT_WORKER",
                "MEDIA" => "MEDIA_WORKER",
                _ => stage
            },
            Retryable = true,
            ErrorCode = errorCode
        };
    }

    private static PipelineStageSnapshot Stage(string? status, Guid? id, string? stage)
    {
        var normalized = string.IsNullOrWhiteSpace(status)
            ? id is null ? "NOT_STARTED" : "PENDING"
            : status.Trim().ToUpperInvariant();
        return new PipelineStageSnapshot(normalized, stage, IsReady(normalized) ? 100 : null, id,
            IsFailed(normalized) ? "PIPELINE_STAGE_FAILED" : null);
    }

    private static PipelineStageSnapshot NotStarted(string stage) => new("NOT_STARTED", stage, 0);

    private static bool IsReady(string? status) => status is not null &&
        status.ToUpperInvariant() is "READY" or "CONFIRMED" or "PARTIAL_READY" or "TRANSCRIPT_READY";

    private static bool IsFailed(string? status) => status is not null &&
        status.ToUpperInvariant() is "FAILED" or "UNAVAILABLE" or "CANCELLED";

    private static bool IsWorkerReady(IReadOnlyDictionary<string, bool>? readiness, string name) =>
        readiness is null || (readiness.TryGetValue(name, out var ready) && ready);
}

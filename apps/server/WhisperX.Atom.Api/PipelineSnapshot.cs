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
    DateTime? UpdatedAt = null,
    PipelineStageSnapshot? Recording = null,
    DateTime? NotBefore = null,
    string? ScheduledReason = null,
    DateTime? QueueEnteredAt = null,
    DateTime? WorkerClaimedAt = null,
    string? DispatchState = null);

/// <summary>
/// Semantic state used by the snapshot resolver.  This deliberately keeps
/// transport/job spellings (RUNNING, INGESTING, DIARIZING, ...) out of the
/// public pipeline status contract.
/// </summary>
public enum PipelineStageClassification
{
    Unknown,
    Queued,
    Running,
    Ready,
    NeedsReview,
    Failed
}

public static class RecordingPipelineSnapshotResolver
{
    public static RecordingPipelineSnapshot Resolve(RecordingPipelineChain chain, IReadOnlyDictionary<string, bool>? workerReadiness = null)
    {
        var delivery = new PipelineStageSnapshot(
            chain.MediaAssetId is null ? "PENDING" : "CONFIRMED",
            chain.MediaAssetId is null ? "WAITING_FOR_UPLOAD" : "UPLOAD_CONFIRMED",
            chain.MediaAssetId is null ? 0 : 100,
            chain.MediaAssetId);
        var recording = Stage(chain.RecordingState, null, chain.RecordingState);
        var media = Stage(chain.MediaStatus, chain.MediaAssetId, "MEDIA_ASSEMBLY");
        var asr = Stage(chain.AsrJobStatus, chain.AsrJobId, chain.AsrJobStage);
        var v1 = Stage(chain.TranscriptV1Status, chain.TranscriptV1Id, "ASR_DRAFT");
        var enrichment = Stage(chain.EnrichmentJobStatus, chain.EnrichmentJobId, chain.EnrichmentJobStage);
        var v2 = Stage(chain.TranscriptV2Status, chain.TranscriptV2Id, "ENRICHED");
        // A draft summary row is not authoritative while its job is running.
        // Prefer the job state until the persisted summary reaches a terminal
        // state, otherwise the row would hide RUNNING as DRAFT.
        var effectiveSummaryStatus = IsSummaryTerminal(chain.SummaryStatus)
            ? chain.SummaryStatus
            : chain.SummaryJobStatus ?? chain.SummaryStatus;
        var summary = Stage(effectiveSummaryStatus, chain.SummaryId, chain.SummaryJobStage, includeNeedsReview: true);
        var baseSnapshot = new RecordingPipelineSnapshot(
            chain.RecordingSessionId, chain.MeetingId, chain.PipelineCorrelationId,
            "PROCESSING", "MEDIA", null, false, null,
            delivery, media, asr, v1, enrichment, v2, summary, chain.StageTimings, chain.CreatedAt, chain.UpdatedAt, recording,
            chain.AsrNotBefore, chain.AsrScheduledReason, chain.AsrQueueEnteredAt, chain.AsrWorkerClaimedAt, chain.AsrDispatchState);

        if (IsFailed(media.Status))
            return FailedOrPartial(baseSnapshot, "MEDIA", "MEDIA_FAILED");
        if (IsRunning(media.Status))
            return Processing(baseSnapshot, "MEDIA");
        if (!IsReady(media.Status))
        {
            if (ClassifyStage(media.Status) == PipelineStageClassification.Queued
                && !IsWorkerReady(workerReadiness, "media-worker"))
                return Waiting(baseSnapshot, "MEDIA", "MEDIA_WORKER", "MEDIA_WORKER_UNAVAILABLE");
            return Waiting(baseSnapshot, "MEDIA");
        }
        if (IsFailed(asr.Status))
            return FailedOrPartial(baseSnapshot, "ASR", "ASR_FAILED");
        if (IsRunning(asr.Status))
            return Processing(baseSnapshot, "ASR");
        if (!IsReady(asr.Status))
        {
            if (chain.AsrNotBefore is DateTime scheduled && scheduled.ToUniversalTime() > DateTime.UtcNow)
                return baseSnapshot with { OverallStatus = "WAITING", CurrentStage = "SCHEDULED", BlockedBy = null, Retryable = false, ErrorCode = null, DispatchState = "SCHEDULED" };
            if (string.Equals(chain.AsrDispatchState, "WAITING_FOR_OUTBOX", StringComparison.OrdinalIgnoreCase))
                return baseSnapshot with { OverallStatus = "WAITING", CurrentStage = "WAITING_FOR_OUTBOX", BlockedBy = null, Retryable = false, ErrorCode = null, DispatchState = "WAITING_FOR_OUTBOX" };
            if (ClassifyStage(asr.Status) == PipelineStageClassification.Queued
                && !IsWorkerReady(workerReadiness, "gpu-worker"))
                return Waiting(baseSnapshot, "ASR", "GPU_WORKER", "GPU_WORKER_UNAVAILABLE");
            return Waiting(baseSnapshot, "ASR");
        }
        if (IsFailed(v1.Status))
            return FailedOrPartial(baseSnapshot, "TRANSCRIPT_V1", "TRANSCRIPT_V1_FAILED");
        if (IsRunning(v1.Status))
            return Processing(baseSnapshot, "TRANSCRIPT_V1");
        if (!IsReady(v1.Status))
            return Waiting(baseSnapshot, "TRANSCRIPT_V1");
        if (IsFailed(enrichment.Status))
            return FailedOrPartial(baseSnapshot, "ENRICHMENT", "ENRICHMENT_FAILED");
        if (IsRunning(enrichment.Status))
            return Processing(baseSnapshot, "ENRICHMENT");
        if (!IsReady(enrichment.Status))
        {
            if (ClassifyStage(enrichment.Status) == PipelineStageClassification.Queued
                && !IsWorkerReady(workerReadiness, "gpu-worker"))
                return Waiting(baseSnapshot, "ENRICHMENT", "GPU_WORKER", "GPU_WORKER_UNAVAILABLE");
            return Waiting(baseSnapshot, "ENRICHMENT");
        }
        if (IsFailed(v2.Status))
            return FailedOrPartial(baseSnapshot, "TRANSCRIPT_V2", "TRANSCRIPT_V2_FAILED");
        if (IsRunning(v2.Status))
            return Processing(baseSnapshot, "TRANSCRIPT_V2");
        if (!IsReady(v2.Status))
            return Waiting(baseSnapshot, "TRANSCRIPT_V2");
        // Summary is an optional enrichment of a usable V2.  When the
        // feature is disabled there is no summary job/row, which must not
        // make the canonical recording pipeline look stuck.
        if (chain.SummaryJobId is null
            && chain.SummaryId is null
            && string.IsNullOrWhiteSpace(chain.SummaryJobStatus)
            && string.IsNullOrWhiteSpace(chain.SummaryStatus))
            return baseSnapshot with { OverallStatus = "READY", CurrentStage = "TRANSCRIPT_V2" };
        if (IsFailed(summary.Status))
            return baseSnapshot with { OverallStatus = "PARTIAL_READY", CurrentStage = "SUMMARY", BlockedBy = "SUMMARY_WORKER", Retryable = true, ErrorCode = "SUMMARY_FAILED" };
        if (IsRunning(summary.Status))
            return Processing(baseSnapshot, "SUMMARY");
        if (!IsReady(summary.Status) && !IsSummaryNeedsReview(summary.Status))
        {
            if (ClassifyStage(summary.Status) == PipelineStageClassification.Queued
                && !IsWorkerReady(workerReadiness, "summary-worker"))
                return Waiting(baseSnapshot, "SUMMARY", "SUMMARY_WORKER", "SUMMARY_WORKER_UNAVAILABLE");
            return Waiting(baseSnapshot, "SUMMARY");
        }
        if (IsSummaryNeedsReview(summary.Status))
            return baseSnapshot with { OverallStatus = "PARTIAL_READY", CurrentStage = "SUMMARY_REVIEW", BlockedBy = null, Retryable = false, ErrorCode = null };

        return baseSnapshot with { OverallStatus = "READY", CurrentStage = "COMPLETE" };
    }

    private static RecordingPipelineSnapshot Waiting(
        RecordingPipelineSnapshot snapshot,
        string stage,
        string? blockedBy = null,
        string? errorCode = null)
    {
        var v1Usable = IsReady(snapshot.TranscriptV1.Status);
        var partialStages = stage is "ENRICHMENT" or "TRANSCRIPT_V2" or "SUMMARY";
        var blocked = !string.IsNullOrWhiteSpace(blockedBy);
        return snapshot with
        {
            OverallStatus = blocked
                ? (v1Usable && partialStages ? "PARTIAL_READY" : "DEGRADED")
                : (v1Usable && partialStages ? "PARTIAL_READY" : "WAITING"),
            CurrentStage = stage,
            BlockedBy = blockedBy,
            Retryable = blocked,
            ErrorCode = errorCode
        };
    }

    private static RecordingPipelineSnapshot Processing(RecordingPipelineSnapshot snapshot, string stage)
    {
        var partial = IsReady(snapshot.TranscriptV1.Status)
            && (stage is "ENRICHMENT" or "TRANSCRIPT_V2" or "SUMMARY");
        return snapshot with
        {
            OverallStatus = partial ? "PARTIAL_READY" : "PROCESSING",
            CurrentStage = stage,
            BlockedBy = null,
            Retryable = false,
            ErrorCode = null
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

    private static PipelineStageSnapshot Stage(string? status, Guid? id, string? stage, bool includeNeedsReview = false)
    {
        var normalized = string.IsNullOrWhiteSpace(status)
            ? id is null ? "NOT_STARTED" : "PENDING"
            : status.Trim().ToUpperInvariant();
        return new PipelineStageSnapshot(normalized, stage, (IsReady(normalized) || (includeNeedsReview && IsSummaryNeedsReview(normalized))) ? 100 : null, id,
            IsFailed(normalized) ? "PIPELINE_STAGE_FAILED" : null);
    }

    private static PipelineStageSnapshot NotStarted(string stage) => new("NOT_STARTED", stage, 0);

    private static PipelineStageClassification ClassifyStage(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return PipelineStageClassification.Unknown;
        return status.Trim().ToUpperInvariant() switch
        {
            "QUEUED" or "PENDING" or "WAITING" => PipelineStageClassification.Queued,
            "RUNNING" or "PROCESSING" or "INGESTING" or "TRANSCRIBING" or
                "ALIGNING" or "DIARIZING" or "PREPARING_CONTEXT" or "EXTRACTING_FACTS" or
                "VALIDATING_EVIDENCE" or "PERSISTING" => PipelineStageClassification.Running,
            "READY" or "CONFIRMED" or "PARTIAL_READY" or "TRANSCRIPT_READY" => PipelineStageClassification.Ready,
            "NEEDS_REVIEW" => PipelineStageClassification.NeedsReview,
            "FAILED" or "UNAVAILABLE" or "CANCELLED" => PipelineStageClassification.Failed,
            _ => PipelineStageClassification.Unknown
        };
    }

    private static bool IsReady(string? status) => ClassifyStage(status) == PipelineStageClassification.Ready;

    private static bool IsSummaryNeedsReview(string? status) =>
        ClassifyStage(status) == PipelineStageClassification.NeedsReview;

    private static bool IsSummaryTerminal(string? status) => IsReady(status) || IsSummaryNeedsReview(status) || IsFailed(status);

    private static bool IsRunning(string? status) => ClassifyStage(status) == PipelineStageClassification.Running;

    private static bool IsFailed(string? status) => ClassifyStage(status) == PipelineStageClassification.Failed;

    private static bool IsWorkerReady(IReadOnlyDictionary<string, bool>? readiness, string name) =>
        readiness is null || (readiness.TryGetValue(name, out var ready) && ready);
}

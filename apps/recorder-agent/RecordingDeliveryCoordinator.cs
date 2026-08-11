using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

public sealed class RecordingDeliveryCoordinator(
    SpoolStore spool,
    AgentApiClient api,
    LocalArchiveWriter archive,
    SessionFinalizationCoordinator sessionLocks,
    ILogger<RecordingDeliveryCoordinator> logger)
{
    public Task<FinalizationResult> RunAsync(string? localSessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localSessionId))
            return Task.FromResult(new FinalizationResult(false, "DELIVERY", "LOCAL_SESSION_REQUIRED", false));
        return sessionLocks.RunAsync(localSessionId, ct => RunCoreAsync(localSessionId, ct), cancellationToken);
    }

    private async Task<FinalizationResult> RunCoreAsync(string localSessionId, CancellationToken cancellationToken)
    {
        string? archivePath = null;
        try
        {
            archivePath = await archive.CreateAsync(localSessionId, cancellationToken);
            await spool.SetFinalizationStateAsync(localSessionId,
                localFinalizeState: "LOCAL_READY",
                archivePath: archivePath,
                errorCode: null,
                errorDetail: null,
                retryCount: 0,
                nextRetryAtUtc: null,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            return await PersistFailureAsync(localSessionId,
                new FinalizationResult(false, "LOCAL_ARCHIVE", ClassifyLocalArchiveError(ex), false, archivePath),
                ex.Message,
                cancellationToken);
        }

        if (!api.IsConfigured)
        {
            await archive.SetUploadStateAsync(localSessionId, "WAITING_FOR_API", "API is not configured", cancellationToken);
            return await PersistFailureAsync(localSessionId,
                new FinalizationResult(false, "DELIVERY", "SERVER_UNAVAILABLE", true, archivePath),
                "API is not configured",
                cancellationToken);
        }

        try
        {
            var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
            if (serverSessionId is not Guid server)
            {
                await spool.SetFinalizationStateAsync(localSessionId, deliveryState: "BINDING", cancellationToken: cancellationToken);
                var tracks = await spool.GetTrackInfosAsync(localSessionId, cancellationToken);
                if (tracks.Count == 0)
                    return await PersistFailureAsync(localSessionId,
                        new FinalizationResult(false, "LOCAL_ARCHIVE", "LOCAL_CHUNK_MISSING", false, archivePath),
                        "No recording tracks are ready",
                        cancellationToken);

                server = await api.BindSessionAsync(
                    localSessionId,
                    await spool.GetMeetingIdAsync(localSessionId, cancellationToken),
                    await spool.GetTitleAsync(localSessionId, cancellationToken),
                    tracks,
                    spool,
                    cancellationToken);
            }

            await archive.SetUploadStateAsync(localSessionId, "UPLOADING", null, cancellationToken);
            await spool.SetFinalizationStateAsync(localSessionId,
                localFinalizeState: "LOCAL_READY",
                deliveryState: "UPLOADING",
                archivePath: archivePath,
                cancellationToken: cancellationToken);
            await api.UploadPendingChunksAsync(spool, cancellationToken);
            await api.UploadPendingEventsAsync(spool, localSessionId, cancellationToken);

            await spool.SetFinalizationStateAsync(localSessionId, deliveryState: "RECONCILING", cancellationToken: cancellationToken);
            var finalized = await api.FinalizeServerSessionAsync(server, localSessionId, spool, cancellationToken);
            if (!finalized.Accepted)
            {
                await archive.SetUploadStateAsync(localSessionId, "WAITING_FOR_CONFIRMATION", finalized.ErrorCode, cancellationToken);
                return await PersistFailureAsync(localSessionId,
                    new FinalizationResult(false, "SERVER_FINALIZE", finalized.ErrorCode ?? "SERVER_FINALIZE_REJECTED", finalized.Retryable, archivePath, server, finalized.MissingChunks, finalized.ErrorCode, finalized.MeetingId, finalized.MediaAssetId, finalized.JobId, finalized.TraceId),
                    finalized.ErrorCode ?? "Server did not confirm the session",
                    cancellationToken);
            }

            await archive.SetUploadStateAsync(localSessionId, "WAITING_SERVER_ASSEMBLY", null, cancellationToken);
            await spool.SetFinalizationStateAsync(localSessionId,
                localFinalizeState: "LOCAL_READY",
                deliveryState: "WAITING_SERVER_ASSEMBLY",
                archivePath: archivePath,
                errorCode: null,
                errorDetail: null,
                retryCount: 0,
                nextRetryAtUtc: DateTimeOffset.UtcNow.AddSeconds(10),
                cancellationToken: cancellationToken);

            var mediaStatus = await api.GetServerMediaStatusAsync(server, cancellationToken);
            if (!mediaStatus.Ready && mediaStatus.TerminalFailure)
            {
                return await PersistFailureAsync(localSessionId,
                    new FinalizationResult(false, "SERVER_ASSEMBLY", mediaStatus.ErrorCode ?? "SERVER_ASSEMBLY_FAILED", false, archivePath, server, null, mediaStatus.ErrorCode, finalized.MeetingId, finalized.MediaAssetId, finalized.JobId, finalized.TraceId),
                    mediaStatus.JobStage ?? mediaStatus.JobStatus ?? mediaStatus.MediaStatus ?? "server assembly failed",
                    cancellationToken);
            }

            if (!mediaStatus.Ready)
                return new FinalizationResult(true, "SERVER_FINALIZE", null, true, archivePath, server, null, null, finalized.MeetingId, finalized.MediaAssetId, finalized.JobId, finalized.TraceId);

            await spool.MarkMediaValidatedAsync(localSessionId, cancellationToken);
            await archive.SetUploadStateAsync(localSessionId, "CONFIRMED", null, cancellationToken);
            await spool.SetFinalizationStateAsync(localSessionId,
                localFinalizeState: "LOCAL_READY",
                deliveryState: "CONFIRMED",
                archivePath: archivePath,
                errorCode: null,
                errorDetail: null,
                retryCount: 0,
                nextRetryAtUtc: null,
                cancellationToken: cancellationToken);
            await spool.PurgeFinalizedSessionAsync(localSessionId, cancellationToken);
            return new FinalizationResult(true, "SERVER_ASSEMBLY", null, false, archivePath, server, null, null, finalized.MeetingId, finalized.MediaAssetId, finalized.JobId, finalized.TraceId);
        }
        catch (Exception ex)
        {
            var result = IsLocalArchiveError(ex)
                ? new FinalizationResult(false, "LOCAL_ARCHIVE", ClassifyLocalArchiveError(ex), false, archivePath)
                : new FinalizationResult(false, "DELIVERY", ClassifyDeliveryError(ex), IsRetryableDeliveryError(ex), archivePath);
            try { await archive.SetUploadStateAsync(localSessionId, result.Stage == "LOCAL_ARCHIVE" ? "ERROR" : "DELIVERY_ERROR", ex.Message, cancellationToken); }
            catch (Exception stateError) { logger.LogDebug(stateError, "Could not persist delivery state. Session={SessionId}", localSessionId); }
            return await PersistFailureAsync(localSessionId, result, ex.Message, cancellationToken);
        }
    }

    private async Task<FinalizationResult> PersistFailureAsync(string sessionId, FinalizationResult result, string detail, CancellationToken cancellationToken)
    {
        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken);
        var retryCount = result.Retryable ? (info?.RetryCount ?? 0) + 1 : info?.RetryCount ?? 0;
        var nextRetry = result.Retryable ? DateTimeOffset.UtcNow.Add(GetRetryDelay(retryCount)) : (DateTimeOffset?)null;
        await spool.SetFinalizationStateAsync(sessionId,
            localFinalizeState: result.Stage == "LOCAL_ARCHIVE" ? "LOCAL_FAILED" : "LOCAL_READY",
            deliveryState: result.Stage == "LOCAL_ARCHIVE" ? "NOT_STARTED" : "DELIVERY_FAILED",
            archivePath: result.ArchivePath,
            errorCode: result.ErrorCode,
            errorDetail: detail,
            retryCount: retryCount,
            nextRetryAtUtc: nextRetry,
            cancellationToken: cancellationToken);
        if (result.Stage == "LOCAL_ARCHIVE") await spool.SetSessionStateAsync(sessionId, "FAILED", cancellationToken);
        return result with { NextRetryAtUtc = nextRetry };
    }

    private static TimeSpan GetRetryDelay(int retryCount)
    {
        var baseSeconds = retryCount switch { <= 1 => 2, 2 => 5, 3 => 15, 4 => 30, _ => 60 };
        return TimeSpan.FromSeconds(Math.Min(300, baseSeconds) + Random.Shared.NextDouble() * Math.Max(1, baseSeconds * 0.2));
    }

    private static bool IsLocalArchiveError(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase)
            || message.Contains("audio_output", StringComparison.OrdinalIgnoreCase)
            || message.Contains("recording_chunk", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyLocalArchiveError(Exception ex)
    {
        var message = ex.ToString();
        if (message.Contains("missing", StringComparison.OrdinalIgnoreCase) || message.Contains("sequence_gap", StringComparison.OrdinalIgnoreCase) || message.Contains("checksum", StringComparison.OrdinalIgnoreCase) || message.Contains("mismatch", StringComparison.OrdinalIgnoreCase)) return "LOCAL_CHUNK_INVALID";
        if (message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase) || message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) return "LOCAL_ARCHIVE_FAILED";
        return "LOCAL_ENCODING_FAILED";
    }

    private static bool IsRetryableDeliveryError(Exception ex) => !ex.ToString().Contains("401", StringComparison.OrdinalIgnoreCase) && !ex.ToString().Contains("403", StringComparison.OrdinalIgnoreCase);
    private static string ClassifyDeliveryError(Exception ex) => ex.ToString().Contains("401", StringComparison.OrdinalIgnoreCase) || ex.ToString().Contains("403", StringComparison.OrdinalIgnoreCase) ? "AGENT_AUTH_REJECTED" : "SERVER_UNAVAILABLE";
}

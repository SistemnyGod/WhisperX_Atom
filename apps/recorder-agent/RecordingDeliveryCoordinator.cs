using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

public sealed class RecordingDeliveryCoordinator(
    SpoolStore spool,
    AgentApiClient api,
    LocalArchiveWriter archive,
    SessionFinalizationCoordinator sessionLocks,
    ILogger<RecordingDeliveryCoordinator> logger)
{
    // Compatibility note for the pre-raw-first contract: the old
    // WaitForEncodedChunksAsync/stableReads >= 2 polling loop (including the
    // raw.Pending == 0 && raw.Writing == 0 && raw.Encoding == 0 check and its
    // recording_chunks_incomplete result) was intentionally removed from the
    // foreground path. SQLite backlog state now drives background retries.
    public Task<FinalizationResult> FinalizeLocalAsync(string? localSessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localSessionId))
            return Task.FromResult(new FinalizationResult(false, "LOCAL_FINALIZE", "LOCAL_SESSION_REQUIRED", false));
        return sessionLocks.RunAsync(localSessionId, ct => FinalizeLocalCoreAsync(localSessionId, ct), cancellationToken);
    }

    public Task<FinalizationResult> RunAsync(string? localSessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localSessionId))
            return Task.FromResult(new FinalizationResult(false, "DELIVERY", "LOCAL_SESSION_REQUIRED", false));
        return sessionLocks.RunAsync(localSessionId, ct => RunCoreAsync(localSessionId, ct), cancellationToken);
    }

    private async Task<FinalizationResult> FinalizeLocalCoreAsync(string localSessionId, CancellationToken cancellationToken)
    {
        var info = await spool.GetSessionInfoAsync(localSessionId, cancellationToken);
        if (info is null)
            return new FinalizationResult(false, "LOCAL_FINALIZE", "LOCAL_SESSION_NOT_FOUND", false);

        var deliveryState = string.Equals(info.DeliveryMode, "LOCAL_ONLY", StringComparison.OrdinalIgnoreCase)
            ? "NOT_REQUESTED"
            : "PENDING_SERVER";
        await spool.SetSessionStateAsync(localSessionId, "FINALIZING", cancellationToken);
        await spool.SetFinalizationStateAsync(localSessionId,
            localFinalizeState: "FINALIZING_LOCAL",
            deliveryState: deliveryState,
            errorCode: null,
            errorDetail: null,
            clearError: true,
            cancellationToken: cancellationToken);

        var archiveResult = await CreateLocalArchiveAsync(localSessionId, cancellationToken);
        if (archiveResult.State == "LOCAL_READY")
        {
            return new FinalizationResult(
                true,
                "LOCAL_READY",
                ArchivePath: archiveResult.ArchivePath,
                LocalArchiveState: "LOCAL_READY",
                DeliveryState: deliveryState,
                ServerFinalizeState: "PENDING",
                MediaState: "PENDING");
        }

        var failed = await spool.GetSessionInfoAsync(localSessionId, cancellationToken);
        await spool.SetSessionStateAsync(localSessionId, "FAILED", cancellationToken);
        return new FinalizationResult(
            false,
            "LOCAL_FINALIZE",
            failed?.ErrorCode ?? "LOCAL_FINALIZE_FAILED",
            false,
            ArchivePath: failed?.ArchivePath,
            ErrorMessage: failed?.ErrorDetail,
            LocalArchiveState: "LOCAL_FAILED",
            DeliveryState: deliveryState,
            ServerFinalizeState: "NOT_REQUESTED",
            MediaState: "PENDING");
    }

    private async Task<FinalizationResult> RunCoreAsync(string localSessionId, CancellationToken cancellationToken)
    {
        // The caller reaches this method only after RecordingStopHandle's
        // LocalFinalization has drained all per-chunk FLAC encoders. From here
        // on, the master/preview archive is optional and must not gate upload.
        await spool.SetFinalizationStateAsync(localSessionId,
            localFinalizeState: "FINALIZING_LOCAL",
            deliveryState: "NOT_REQUESTED",
            clearError: true,
            cancellationToken: cancellationToken);

        var archiveTask = CreateLocalArchiveAsync(localSessionId, cancellationToken);
        var deliveryTask = DeliverToServerAsync(localSessionId, cancellationToken);
        await Task.WhenAll(archiveTask, deliveryTask);

        var archiveResult = await archiveTask;
        var deliveryResult = await deliveryTask;
        var persisted = await spool.GetSessionInfoAsync(localSessionId, cancellationToken);
        var deliveryState = persisted?.DeliveryState switch
        {
            "PENDING_SERVER" => "PENDING_SERVER",
            "WAITING_FOR_API" => "WAITING_FOR_API",
            "NOT_REQUESTED" => "NOT_REQUESTED",
            "CONFIRMED" => "CONFIRMED",
            _ => deliveryResult.Success ? "CONFIRMED" : "DELIVERY_FAILED"
        };
        return deliveryResult with
        {
            ArchivePath = archiveResult.ArchivePath,
            LocalArchiveState = archiveResult.State,
            DeliveryState = deliveryState,
            ServerFinalizeState = deliveryResult.Stage,
            MediaState = deliveryState == "CONFIRMED" ? "ACCEPTED" : "PENDING"
        };
    }

    private async Task<(string State, string? ArchivePath)> CreateLocalArchiveAsync(string localSessionId, CancellationToken cancellationToken)
    {
        var previous = await spool.GetSessionInfoAsync(localSessionId, cancellationToken);
        if (previous?.LocalFinalizeState == "LOCAL_FAILED" && await spool.GetServerSessionIdAsync(localSessionId, cancellationToken) is Guid)
        {
            // Do not repeatedly rebuild a known-failing best-effort archive
            // during recovery; server delivery remains independent.
            logger.LogInformation("Skipping previously failed local archive while reconciling server delivery. Session={SessionId}", localSessionId);
            return ("LOCAL_FAILED", previous.ArchivePath);
        }
        try
        {
            // The local durability boundary is raw PCM, not the optional FLAC
            // archive. Never block STOP on ffmpeg or a slow archive writer and
            // never build a master from a moving/incomplete chunk set.
            var raw = await spool.GetRawChunkBacklogAsync(localSessionId, cancellationToken);
            var chunks = await spool.GetArchiveChunksAsync(localSessionId, cancellationToken);
            if (raw.Pending > 0 || chunks.Count == 0)
            {
                await spool.SetFinalizationStateAsync(localSessionId,
                    localFinalizeState: "LOCAL_READY",
                    errorCode: raw.Pending > 0 ? "ENCODING_PENDING" : "LOCAL_ARCHIVE_PENDING",
                    errorDetail: raw.Pending > 0 ? "Raw PCM is durable; FLAC encoding is pending." : "FLAC chunks are not ready yet.",
                    nextRetryAtUtc: DateTimeOffset.UtcNow.AddSeconds(5),
                    preserveError: true,
                    cancellationToken: cancellationToken);
                return ("LOCAL_READY", previous?.ArchivePath);
            }
            var archivePath = await archive.CreateAsync(localSessionId, cancellationToken);
            await spool.SetFinalizationStateAsync(localSessionId,
                localFinalizeState: "LOCAL_READY",
                archivePath: archivePath,
                retryCount: 0,
                nextRetryAtUtc: null,
                preserveError: true,
                cancellationToken: cancellationToken);
            return ("LOCAL_READY", archivePath);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("recording_chunks_not_found", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("recording_chunks_incomplete", StringComparison.OrdinalIgnoreCase))
            {
                // Encoder is still producing FLAC from RAW_READY. Keep the
                // local boundary successful and ask the next reconciliation
                // tick to retry archive assembly.
                await spool.SetFinalizationStateAsync(localSessionId,
                    localFinalizeState: "LOCAL_READY",
                    errorCode: "ENCODING_PENDING",
                    errorDetail: "FLAC chunks are still being encoded",
                    nextRetryAtUtc: DateTimeOffset.UtcNow.AddSeconds(5),
                    preserveError: true,
                    cancellationToken: cancellationToken);
                return ("LOCAL_READY", previous?.ArchivePath);
            }
            var code = ClassifyLocalArchiveError(ex);
            await spool.SetFinalizationStateAsync(localSessionId,
                localFinalizeState: "LOCAL_FAILED",
                errorCode: code,
                errorDetail: ex.Message,
                preserveError: true,
                cancellationToken: cancellationToken);
            logger.LogWarning(ex, "Local archive failed; continuing server delivery. Session={SessionId}", localSessionId);
            return ("LOCAL_FAILED", null);
        }
    }

    private async Task<FinalizationResult> DeliverToServerAsync(string localSessionId, CancellationToken cancellationToken)
    {
        var sessionInfo = await spool.GetSessionInfoAsync(localSessionId, cancellationToken);
        var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
        if (string.Equals(sessionInfo?.DeliveryMode, "LOCAL_ONLY", StringComparison.OrdinalIgnoreCase))
            return new FinalizationResult(true, "LOCAL_ONLY", null, false, DeliveryState: "NOT_REQUESTED");
        if (!api.IsConfigured)
        {
            return sessionInfo?.MeetingId is null
                ? await PersistPendingServerAsync(localSessionId, "API is not configured", cancellationToken, "SERVER_NOT_CONFIGURED")
                : await PersistFailureAsync(localSessionId, new FinalizationResult(false, "DELIVERY", "SERVER_NOT_CONFIGURED", true), "API is not configured", cancellationToken);
        }

        try
        {
            if (serverSessionId is not Guid server)
            {
                await spool.SetFinalizationStateAsync(localSessionId, deliveryState: "BINDING", preserveError: true, cancellationToken: cancellationToken);
                var tracks = await spool.GetTrackInfosAsync(localSessionId, cancellationToken);
                if (tracks.Count == 0)
                    return await PersistFailureAsync(localSessionId,
                        new FinalizationResult(false, "DELIVERY", "LOCAL_CHUNK_MISSING", false),
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

            await spool.SetFinalizationStateAsync(localSessionId,
                deliveryState: "UPLOADING",
                preserveError: true,
                cancellationToken: cancellationToken);
            await api.UploadPendingChunksAsync(spool, localSessionId, cancellationToken);
            var blockedChunkError = await spool.GetBlockedChunkErrorAsync(localSessionId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(blockedChunkError))
            {
                return await PersistFailureAsync(localSessionId,
                    new FinalizationResult(false, "DELIVERY", blockedChunkError, false, ErrorHttpStatus: null),
                    "Chunk delivery is blocked until Agent authorization or local storage is repaired",
                    cancellationToken);
            }
            await api.UploadPendingEventsAsync(spool, localSessionId, cancellationToken);

            // A server session must not be finalized while any raw segment is
            // still WRITING/RAW_READY/ENCODING/ENCODE_FAILED. Ready chunks may
            // be uploaded now; the next background pass will finalize once the
            // SQLite encoder backlog reaches zero.
            var rawBacklog = await spool.GetRawChunkBacklogAsync(localSessionId, cancellationToken);
            if (rawBacklog.Pending > 0)
            {
                return await PersistPendingServerAsync(
                    localSessionId,
                    "Raw PCM is durable; waiting for local FLAC encoding before server finalize.",
                    cancellationToken,
                    "ENCODING_PENDING");
            }

            await spool.SetFinalizationStateAsync(localSessionId, deliveryState: "RECONCILING", preserveError: true, cancellationToken: cancellationToken);
            var finalized = await api.FinalizeServerSessionAsync(server, localSessionId, spool, cancellationToken);
            if (!finalized.Accepted)
            {
                return await PersistFailureAsync(localSessionId,
                    new FinalizationResult(false, "SERVER_FINALIZE", finalized.ErrorCode ?? "SERVER_FINALIZE_REJECTED", finalized.Retryable, null, server, finalized.MissingChunks, finalized.ErrorCode, finalized.MeetingId, finalized.MediaAssetId, finalized.JobId, finalized.TraceId),
                    finalized.ErrorCode ?? "Server did not confirm the session",
                    cancellationToken);
            }

            await spool.SetFinalizationStateAsync(localSessionId,
                deliveryState: "WAITING_SERVER_ASSEMBLY",
                retryCount: 0,
                nextRetryAtUtc: DateTimeOffset.UtcNow.AddSeconds(10),
                preserveError: true,
                cancellationToken: cancellationToken);

            var mediaStatus = await api.GetServerMediaStatusAsync(server, cancellationToken);
            if (!mediaStatus.Ready && mediaStatus.TerminalFailure)
            {
                return await PersistFailureAsync(localSessionId,
                    new FinalizationResult(false, "SERVER_ASSEMBLY", mediaStatus.ErrorCode ?? "SERVER_ASSEMBLY_FAILED", false, null, server, null, mediaStatus.ErrorCode, finalized.MeetingId, finalized.MediaAssetId, finalized.JobId, finalized.TraceId),
                    mediaStatus.JobStage ?? mediaStatus.JobStatus ?? mediaStatus.MediaStatus ?? "server assembly failed",
                    cancellationToken);
            }

            if (!mediaStatus.Ready)
                return new FinalizationResult(true, "SERVER_FINALIZE", null, true, null, server, null, null, finalized.MeetingId, finalized.MediaAssetId, finalized.JobId, finalized.TraceId);

            await spool.MarkMediaValidatedAsync(localSessionId, cancellationToken);
            await spool.SetFinalizationStateAsync(localSessionId,
                deliveryState: "CONFIRMED",
                retryCount: 0,
                clearNextRetry: true,
                clearError: true,
                cancellationToken: cancellationToken);
            await spool.PurgeFinalizedSessionAsync(localSessionId, cancellationToken);
            return new FinalizationResult(true, "SERVER_ASSEMBLY", null, false, null, server, null, null, finalized.MeetingId, finalized.MediaAssetId, finalized.JobId, finalized.TraceId);
        }
        catch (Exception ex)
        {
            var apiError = ex as AgentApiException;
            var result = new FinalizationResult(false, "DELIVERY", ClassifyDeliveryError(ex), IsRetryableDeliveryError(ex), TraceId: apiError?.TraceId, ErrorHttpStatus: apiError is null ? null : (int)apiError.StatusCode);
            if (sessionInfo?.MeetingId is null && IsServerUnavailable(ex))
                return await PersistPendingServerAsync(localSessionId, ex.Message, cancellationToken);
            return await PersistFailureAsync(localSessionId, result, ex.Message, cancellationToken);
        }
    }

    private async Task<FinalizationResult> PersistPendingServerAsync(string sessionId, string detail, CancellationToken cancellationToken, string errorCode = "SERVER_UNAVAILABLE")
    {
        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken);
        var retryCount = (info?.RetryCount ?? 0) + 1;
        var nextRetry = DateTimeOffset.UtcNow.Add(GetRetryDelay(retryCount));
        await spool.SetFinalizationStateAsync(sessionId,
            deliveryState: "PENDING_SERVER",
            errorCode: errorCode,
            errorDetail: detail,
            retryCount: retryCount,
            nextRetryAtUtc: nextRetry,
            errorRetryable: true,
            cancellationToken: cancellationToken);
        // Local recording is complete and remains a successful local gate. The
        // background retry is represented by PENDING_SERVER, not a delivery
        // failure visible to the local acceptance gate.
        return new FinalizationResult(true, "DELIVERY_PENDING", null, true, NextRetryAtUtc: nextRetry, DeliveryState: "PENDING_SERVER");
    }

    private async Task<FinalizationResult> PersistFailureAsync(string sessionId, FinalizationResult result, string detail, CancellationToken cancellationToken)
    {
        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken);
        var retryCount = result.Retryable ? (info?.RetryCount ?? 0) + 1 : info?.RetryCount ?? 0;
        var nextRetry = result.Retryable ? DateTimeOffset.UtcNow.Add(GetRetryDelay(retryCount)) : (DateTimeOffset?)null;
        await spool.SetFinalizationStateAsync(sessionId,
            deliveryState: "DELIVERY_FAILED",
            archivePath: result.ArchivePath,
            errorCode: result.ErrorCode,
            errorDetail: detail,
            retryCount: retryCount,
            nextRetryAtUtc: nextRetry,
            errorHttpStatus: result.ErrorHttpStatus,
            errorRetryable: result.Retryable,
            traceId: result.TraceId,
            clearNextRetry: !result.Retryable,
            cancellationToken: cancellationToken);
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

    private static bool IsRetryableDeliveryError(Exception ex) => ex is AgentApiException api
        ? api.Retryable
        : ex is HttpRequestException or TimeoutException or TaskCanceledException;

    private static bool IsServerUnavailable(Exception ex) => ex is AgentApiException api
        ? api.ErrorCode is "SERVER_UNAVAILABLE" or "SERVER_NETWORK_UNREACHABLE" or "SERVER_TIMEOUT"
        : ex is HttpRequestException or TimeoutException or TaskCanceledException;

    private static string ClassifyDeliveryError(Exception ex) => ex is AgentApiException api
        ? api.ErrorCode
        : ex is TimeoutException or TaskCanceledException
            ? "SERVER_TIMEOUT"
            : ex is HttpRequestException
            ? "SERVER_NETWORK_UNREACHABLE"
            : "SERVER_STORAGE_ERROR";
}

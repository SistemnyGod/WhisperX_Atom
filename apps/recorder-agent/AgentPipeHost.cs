using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

public sealed class AgentPipeHost(
    RecordingCoordinator recorder,
    AgentStateMachine state,
    SpoolStore spool,
    AgentApiClient api,
    AgentStorageSettings storage,
    LocalArchiveWriter archive,
    ILogger<AgentPipeHost> logger) : BackgroundService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Task> _finalizations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _finalizationErrors = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Desktop IPC listening on named pipe {PipeName}", AgentIpcProtocol.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = AgentPipeSecurity.CreateServer();

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(pipe);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var line = await reader.ReadLineAsync(stoppingToken);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var request = JsonSerializer.Deserialize<AgentIpcRequest>(line, _json);
                var response = request is null
                    ? Error("invalid_request")
                    : await ExecuteAsync(request, stoppingToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Desktop IPC request failed.");
            }
        }
    }

    private async Task<AgentIpcResponse> ExecuteAsync(AgentIpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Command.Trim().ToUpperInvariant())
            {
                case "STATUS":
                    return Status();
                case "HEALTH":
                    return await StatusAsync(cancellationToken);
                case "PREFLIGHT":
                    return await PreflightAsync(cancellationToken);
                case "GET_SESSION_STATUS":
                    var statusSessionId = ReadString(request.Payload, "sessionId");
                    if (string.IsNullOrWhiteSpace(statusSessionId)) return Error("session_required");
                    return await SessionStatusAsync(statusSessionId, cancellationToken);
                case "CONFIGURE":
                    var serverUrl = ReadString(request.Payload, "serverUrl");
                    var agentToken = ReadString(request.Payload, "token");
                    var agentId = ReadGuid(request.Payload, "agentId");
                    if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(agentToken) || agentId is not Guid configuredAgent)
                        return Error("agent_configuration_invalid");
                    await api.ConfigureAsync(serverUrl, configuredAgent, agentToken, cancellationToken);
                    var configuredArchiveRoot = ReadString(request.Payload, "archiveRoot");
                    if (!string.IsNullOrWhiteSpace(configuredArchiveRoot)) await api.SetArchiveRootAsync(configuredArchiveRoot, cancellationToken);
                    if (HasProperty(request.Payload, "microphoneDeviceId") || HasProperty(request.Payload, "systemAudioDeviceId"))
                        await api.SetAudioDevicesAsync(ReadString(request.Payload, "microphoneDeviceId"), ReadString(request.Payload, "systemAudioDeviceId"), cancellationToken);
                    return Status();
                case "SET_ARCHIVE_ROOT":
                    var archiveRoot = ReadString(request.Payload, "archiveRoot");
                    if (string.IsNullOrWhiteSpace(archiveRoot)) return Error("archive_root_required");
                    await api.SetArchiveRootAsync(archiveRoot, cancellationToken);
                    return Status();
                case "SET_AUDIO_DEVICES":
                    var microphoneDeviceId = ReadString(request.Payload, "microphoneDeviceId");
                    var systemAudioDeviceId = ReadString(request.Payload, "systemAudioDeviceId");
                    await api.SetAudioDevicesAsync(microphoneDeviceId, systemAudioDeviceId, cancellationToken);
                    return await StatusAsync(cancellationToken);
                case "RETRY_UPLOAD":
                    var retrySessionId = ReadString(request.Payload, "sessionId");
                    if (string.IsNullOrWhiteSpace(retrySessionId)) return Error("session_required");
                    var retryResult = await FinalizeAsync(retrySessionId, cancellationToken);
                    if (retryResult.Success) _finalizationErrors.TryRemove(retrySessionId, out _);
                    else _finalizationErrors[retrySessionId] = retryResult.ErrorCode ?? "server_finalize_pending";
                    return new AgentIpcResponse(retryResult.Success, retryResult.Success ? RecorderState.Idle.ToString() : RecorderState.Error.ToString(), retrySessionId,
                        retryResult.Success ? null : retryResult.ErrorCode, null, null, null, AgentIpcProtocol.Version, null,
                        await BuildSessionStatusAsync(retrySessionId, cancellationToken));
                case "START":
                    var meetingId = ReadGuid(request.Payload, "meetingId");
                    var title = ReadString(request.Payload, "title");
                    var sessionId = await recorder.StartAsync(meetingId, title, cancellationToken);
                    Guid? serverSessionId = null;
                    string? bindingWarning = null;
                    if (api.IsConfigured)
                    {
                        try { serverSessionId = await api.BindSessionAsync(sessionId, meetingId, title, recorder.ActiveTracks, spool, cancellationToken); }
                        catch (Exception ex)
                        {
                            bindingWarning = "server_binding_pending";
                            logger.LogWarning(ex, "Server session binding failed; recording continues locally. Session={SessionId}", sessionId);
                        }
                    }
                    var boundMeetingId = await spool.GetMeetingIdAsync(sessionId, cancellationToken);
                    return new AgentIpcResponse(true, state.State.ToString(), sessionId, bindingWarning, null, boundMeetingId);
                case "PAUSE":
                    await recorder.PauseAsync(cancellationToken);
                    return Status();
                case "RESUME":
                    await recorder.ResumeAsync(cancellationToken);
                    return Status();
                case "MARKER":
                    return await RecordEventAsync("MARKER", request.Payload, cancellationToken);
                case "DECISION":
                    return await RecordEventAsync("DECISION", request.Payload, cancellationToken);
                case "ACTION_ITEM":
                    return await RecordEventAsync("ACTION_ITEM", request.Payload, cancellationToken);
                case "VOICE_EVENT":
                    var eventType = ReadString(request.Payload, "eventType") ?? "VOICE_COMMAND";
                    return await RecordEventAsync(eventType, request.Payload, cancellationToken);
                case "STOP":
                    var localSessionId = recorder.SessionId;
                    var stoppedMeetingId = string.IsNullOrWhiteSpace(localSessionId)
                        ? null
                        : await spool.GetMeetingIdAsync(localSessionId, cancellationToken);
                    var stop = await recorder.RequestStopAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(stop.SessionId)) TrackFinalization(stop, stoppedMeetingId);
                    return new AgentIpcResponse(true, RecorderState.Finalizing.ToString(), localSessionId, "server_finalize_pending", null, stoppedMeetingId);
                default:
                    return Error("unsupported_command");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Desktop command {Command} failed", request.Command);
            return Error(MapError(ex));
        }
    }

    private void TrackFinalization(RecordingStopHandle stop, Guid? meetingId)
    {
        if (string.IsNullOrWhiteSpace(stop.SessionId)) return;
        _finalizationErrors.TryRemove(stop.SessionId, out _);
        var task = CompleteFinalizationAsync(stop, meetingId);
        _finalizations[stop.SessionId] = task;
        _ = task.ContinueWith(completed => _finalizations.TryRemove(stop.SessionId, out _), TaskScheduler.Default);
    }

    private async Task CompleteFinalizationAsync(RecordingStopHandle stop, Guid? meetingId)
    {
        var stage = "LOCAL_FINALIZATION";
        try
        {
            await spool.SetFinalizationStateAsync(stop.SessionId!, localFinalizeState: "FINALIZING_LOCAL", deliveryState: "NOT_STARTED");
            await stop.LocalFinalization;
            stage = "LOCAL_ARCHIVE";
            var result = await FinalizeAsync(stop.SessionId, CancellationToken.None);
            if (!result.Success)
                _finalizationErrors[stop.SessionId!] = result.ErrorCode ?? "server_finalize_pending";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording finalization failed after local stop. Session={SessionId}, Meeting={MeetingId}", stop.SessionId, meetingId);
            var code = stage == "LOCAL_FINALIZATION" ? "LOCAL_ENCODING_FAILED" : "LOCAL_ARCHIVE_FAILED";
            await spool.SetFinalizationStateAsync(stop.SessionId!, localFinalizeState: "LOCAL_FAILED", deliveryState: "NOT_STARTED", errorCode: code, errorDetail: ex.Message, retryCount: 0, nextRetryAtUtc: null, cancellationToken: CancellationToken.None);
            _finalizationErrors[stop.SessionId!] = code;
        }
    }
    private async Task<FinalizationResult> FinalizeAsync(string? localSessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(localSessionId)) return new FinalizationResult(false, "LOCAL_ARCHIVE", "LOCAL_ARCHIVE_FAILED", false);
        string? archivePath = null;
        if (!api.IsConfigured)
        {
            try { archivePath = await archive.CreateAsync(localSessionId, cancellationToken); }
            catch (Exception ex) { return await PersistFailureAsync(localSessionId, new FinalizationResult(false, "LOCAL_ARCHIVE", "LOCAL_ARCHIVE_FAILED", false, null), ex.Message, cancellationToken); }
            await archive.SetUploadStateAsync(localSessionId, "WAITING_FOR_API", "API is not configured", cancellationToken);
            return await PersistFailureAsync(localSessionId, new FinalizationResult(false, "DELIVERY", "SERVER_UNAVAILABLE", true, archivePath), "API is not configured", cancellationToken);
        }
        try
        {
            archivePath = await archive.CreateAsync(localSessionId, cancellationToken);
            await spool.SetFinalizationStateAsync(localSessionId, localFinalizeState: "LOCAL_READY", archivePath: archivePath, errorCode: null, errorDetail: null, retryCount: 0, nextRetryAtUtc: null, cancellationToken: cancellationToken);
            await archive.SetUploadStateAsync(localSessionId, "UPLOADING", null, cancellationToken);
            await spool.SetFinalizationStateAsync(localSessionId, deliveryState: "UPLOADING", cancellationToken: cancellationToken);
            await api.UploadPendingChunksAsync(spool, cancellationToken);
            await api.UploadPendingEventsAsync(spool, localSessionId, cancellationToken);
            var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
            if (serverSessionId is not Guid server)
            {
                await spool.SetFinalizationStateAsync(localSessionId, deliveryState: "BINDING", cancellationToken: cancellationToken);
                var meetingId = await spool.GetMeetingIdAsync(localSessionId, cancellationToken);
                var tracks = await spool.GetTrackInfosAsync(localSessionId, cancellationToken);
                if (tracks.Count == 0)
                {
                    await archive.SetUploadStateAsync(localSessionId, "WAITING_FOR_BINDING", "No recording tracks are ready", cancellationToken);
                    return await PersistFailureAsync(localSessionId, new FinalizationResult(false, "LOCAL_ARCHIVE", "LOCAL_CHUNK_MISSING", false, archivePath), "No recording tracks are ready", cancellationToken);
                }
                server = await api.BindSessionAsync(localSessionId, meetingId, await spool.GetTitleAsync(localSessionId, cancellationToken), tracks, spool, cancellationToken);
            }
            await spool.SetFinalizationStateAsync(localSessionId, deliveryState: "RECONCILING", cancellationToken: cancellationToken);
            var finalized = await api.FinalizeServerSessionAsync(server, localSessionId, spool, cancellationToken);
            if (finalized)
            {
                await archive.SetUploadStateAsync(localSessionId, "CONFIRMED", null, cancellationToken);
                await spool.SetFinalizationStateAsync(localSessionId, localFinalizeState: "LOCAL_READY", deliveryState: "CONFIRMED", archivePath: archivePath, errorCode: null, errorDetail: null, retryCount: 0, nextRetryAtUtc: null, cancellationToken: cancellationToken);
                await spool.PurgeFinalizedSessionAsync(localSessionId, cancellationToken);
                _finalizationErrors.TryRemove(localSessionId, out _);
                return new FinalizationResult(true, "SERVER_FINALIZE", null, false, archivePath, server);
            }
            await archive.SetUploadStateAsync(localSessionId, "WAITING_FOR_CONFIRMATION", "Server did not confirm the session", cancellationToken);
            return await PersistFailureAsync(localSessionId, new FinalizationResult(false, "SERVER_FINALIZE", "SERVER_FINALIZE_REJECTED", true, archivePath, server), "Server did not confirm the session", cancellationToken);
        }
        catch (Exception ex)
        {
            var result = IsLocalArchiveError(ex)
                ? new FinalizationResult(false, "LOCAL_ARCHIVE", ClassifyLocalArchiveError(ex), false, archivePath)
                : new FinalizationResult(false, "DELIVERY", ClassifyDeliveryError(ex), IsRetryableDeliveryError(ex), archivePath);
            try { await archive.SetUploadStateAsync(localSessionId, result.Stage == "LOCAL_ARCHIVE" ? "ERROR" : "DELIVERY_ERROR", ex.Message, cancellationToken); } catch (Exception stateError) { logger.LogDebug(stateError, "Could not persist archive error. Session={SessionId}", localSessionId); }
            return await PersistFailureAsync(localSessionId, result, ex.Message, cancellationToken);
        }
    }

    private async Task<FinalizationResult> PersistFailureAsync(string sessionId, FinalizationResult result, string detail, CancellationToken cancellationToken)
    {
        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken);
        var retryCount = result.Retryable ? (info?.RetryCount ?? 0) + 1 : info?.RetryCount ?? 0;
        DateTimeOffset? nextRetry = result.Retryable ? DateTimeOffset.UtcNow.Add(GetRetryDelay(retryCount)) : null;
        await spool.SetFinalizationStateAsync(sessionId,
            localFinalizeState: result.Stage == "LOCAL_ARCHIVE" ? "LOCAL_FAILED" : "LOCAL_READY",
            deliveryState: result.Stage == "LOCAL_ARCHIVE" ? "NOT_STARTED" : "DELIVERY_ERROR",
            archivePath: result.ArchivePath,
            errorCode: result.ErrorCode,
            errorDetail: detail,
            retryCount: retryCount,
            nextRetryAtUtc: nextRetry,
            cancellationToken: cancellationToken);
        return result;
    }

    private static TimeSpan GetRetryDelay(int retryCount) => retryCount switch
    {
        <= 1 => TimeSpan.FromSeconds(15),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(1),
        4 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5)
    };

    private static bool IsLocalArchiveError(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("recording_chunk_", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ffmpeg_", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ffprobe_", StringComparison.OrdinalIgnoreCase)
            || message.Contains("audio_output", StringComparison.OrdinalIgnoreCase);
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

    private AgentIpcResponse Status()
    {
        var visible = VisibleStatus();
        return new AgentIpcResponse(true, visible.State.ToString(), visible.SessionId, visible.Error, null, null, recorder.CurrentMediaTimeMs);
    }

    private async Task<AgentIpcResponse> StatusAsync(CancellationToken cancellationToken)
    {
        var health = DeviceHealthSnapshot.Collect(DataRoot(), storage);
        var pendingUploadSessions = 0;
        try { pendingUploadSessions = await spool.PendingUploadSessionCountAsync(cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "Spool database is not initialized while reporting health."); }
        var rawBacklog = new RawChunkBacklog(0, 0, 0, 0, 0);
        try { rawBacklog = await spool.GetRawChunkBacklogAsync(cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "Raw chunk backlog is not available while reporting health."); }
        var visible = VisibleStatus();
        var peaks = recorder.CurrentAudioPeaks;
        return new AgentIpcResponse(true, visible.State.ToString(), visible.SessionId, visible.Error, new AgentIpcHealth(
            health.Microphone, health.SystemAudio, health.CaptureDeviceCount, health.RenderDeviceCount, health.FreeBytes, health.TotalBytes, health.Error,
            storage.ArchiveRoot, pendingUploadSessions, health.CaptureDevices, health.RenderDevices,
            storage.MicrophoneDeviceId, storage.SystemAudioDeviceId,
            rawBacklog.Pending, rawBacklog.Writing, rawBacklog.Encoding, rawBacklog.Failed, rawBacklog.Bytes,
            peaks.MicrophonePeak, peaks.SystemAudioPeak, peaks.MicrophoneDb, peaks.SystemAudioDb,
            api.InstallationId, api.AgentId, api.ServerConnectionState, api.LastHeartbeatAtUtc, api.LastServerError), null, recorder.CurrentMediaTimeMs);
    }

    private async Task<AgentIpcResponse> PreflightAsync(CancellationToken cancellationToken)
    {
        var health = DeviceHealthSnapshot.Collect(DataRoot(), storage);
        var warnings = new List<string>();
        var errors = new List<string>();
        var ffmpeg = true;
        var archive = true;
        var spoolReady = true;
        try { recorder.ValidatePreflight(); }
        catch (Exception ex)
        {
            ffmpeg = !ex.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase);
            archive = !ex.Message.Contains("archive", StringComparison.OrdinalIgnoreCase);
            errors.Add(ex.Message);
        }
        try { _ = await spool.PendingUploadSessionCountAsync(cancellationToken); }
        catch (Exception ex) { spoolReady = false; errors.Add("spool_unavailable"); logger.LogDebug(ex, "Recorder spool preflight failed."); }
        if (!health.Microphone && !health.SystemAudio) errors.Add("no_audio_source_available");
        if (!string.IsNullOrWhiteSpace(health.Error)) warnings.Add(health.Error);
        if (!api.IsConfigured) warnings.Add("backend_not_configured_recording_can_start_offline");
        var minimumText = Environment.GetEnvironmentVariable("ATOM_AGENT_MIN_FREE_BYTES");
        var minimumBytes = long.TryParse(minimumText, out var configured) && configured > 0 ? configured : 512L * 1024 * 1024;
        var ready = (health.Microphone || health.SystemAudio) && ffmpeg && archive && spoolReady && health.FreeBytes >= minimumBytes;
        return new AgentIpcResponse(true, state.State.ToString(), recorder.SessionId, null, null, null, recorder.CurrentMediaTimeMs,
            AgentIpcProtocol.Version, new AgentPreflightResult(ready, health.Microphone, health.SystemAudio, ffmpeg, spoolReady, archive,
                health.FreeBytes, minimumBytes, api.ServerConnectionState, warnings, errors));
    }

    private async Task<AgentIpcResponse> SessionStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        var status = await BuildSessionStatusAsync(sessionId, cancellationToken);
        if (status is null) return Error("session_not_found");
        return new AgentIpcResponse(true, status.CaptureState, sessionId, status.ErrorCode ?? status.Error, null, status.MeetingId, recorder.CurrentMediaTimeMs,
            AgentIpcProtocol.Version, null, status);
    }

    private async Task<RecordingSessionStatus?> BuildSessionStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken);
        if (info is null) return null;
        var counts = await spool.GetChunkCountsAsync(sessionId, cancellationToken);
        var serverSessionId = await spool.GetServerSessionIdAsync(sessionId, cancellationToken);
        var capture = string.Equals(recorder.SessionId, sessionId, StringComparison.Ordinal)
            ? state.State.ToString().ToUpperInvariant()
            : info.LocalFinalizeState switch
            {
                "FINALIZING_LOCAL" => "FINALIZING_LOCAL",
                "LOCAL_READY" => "LOCAL_READY",
                "LOCAL_FAILED" => "LOCAL_FAILED",
                _ => info.State
            };
        var delivery = info.DeliveryState switch
        {
            "NOT_STARTED" when info.LocalFinalizeState == "LOCAL_READY" && !api.IsConfigured => "WAITING_FOR_API",
            "NOT_STARTED" when info.LocalFinalizeState == "LOCAL_READY" => "BINDING",
            "CONFIRMED" => "COMPLETED",
            _ => info.DeliveryState
        };
        if (string.IsNullOrWhiteSpace(delivery)) delivery = serverSessionId is null ? (api.IsConfigured ? "BINDING" : "WAITING_FOR_API") : counts.Pending > 0 ? "SYNCING" : "WAITING_SERVER";
        var errorCode = _finalizationErrors.TryGetValue(sessionId, out var finalizationError) ? finalizationError : info.ErrorCode;
        var error = errorCode is null ? null : SafeErrorText(errorCode);
        return new RecordingSessionStatus(sessionId, info.MeetingId, capture, delivery,
            counts.Total, counts.Confirmed, counts.Pending, error, serverSessionId,
            info.LocalFinalizeState, info.ArchivePath, errorCode, IsRetryableCode(errorCode), info.NextRetryAtUtc);
    }

    private static bool IsRetryableCode(string? code) => code is "SERVER_UNAVAILABLE" or "SERVER_FINALIZE_REJECTED" or "CHUNK_UPLOAD_FAILED";

    private static string SafeErrorText(string code) => code switch
    {
        "LOCAL_ENCODING_FAILED" or "LOCAL_ARCHIVE_FAILED" => "Не удалось собрать локальный master-файл. Исходные аудиочанки сохранены.",
        "LOCAL_CHUNK_MISSING" or "LOCAL_CHUNK_INVALID" => "Локальные аудиочанки неполные или повреждены. Исходные файлы сохранены для диагностики.",
        "SERVER_UNAVAILABLE" or "CHUNK_UPLOAD_FAILED" => "Запись сохранена локально. Сервер пока не подтвердил получение. Повторная отправка выполняется автоматически.",
        "SERVER_FINALIZE_REJECTED" => "Запись сохранена локально. Сервер не подтвердил завершение, повторная отправка выполняется автоматически.",
        "AGENT_AUTH_REJECTED" => "Сервер отклонил авторизацию Agent. Переподключите Agent в настройках.",
        _ => "Запись сохранена локально, но требует повторной отправки."
    };

    private (RecorderState State, string? SessionId, string? Error) VisibleStatus()
    {
        var finalizingSession = _finalizations.Keys.OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(finalizingSession))
            return (RecorderState.Finalizing, finalizingSession, "server_finalize_pending");

        var failedSession = _finalizationErrors.Keys.OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(failedSession) && _finalizationErrors.TryGetValue(failedSession, out var error))
            return (RecorderState.Error, failedSession, error);

        return (state.State, recorder.SessionId, null);
    }

    private async Task<AgentIpcResponse> RecordEventAsync(string eventType, JsonElement payload, CancellationToken cancellationToken)
    {
        var sessionId = recorder.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return Error("recording_not_active");
        var mediaTimeMs = recorder.CurrentMediaTimeMs ?? 0;
        await spool.AddEventAsync(sessionId, eventType, mediaTimeMs, JsonSerializer.Serialize(payload), cancellationToken);
        var meetingId = await spool.GetMeetingIdAsync(sessionId, cancellationToken);
        return new AgentIpcResponse(true, state.State.ToString(), sessionId, null, null, meetingId, mediaTimeMs);
    }

    private static AgentIpcResponse Error(string error) => new(false, RecorderState.Idle.ToString(), null, error, null);

    private static string MapError(Exception exception) => exception switch
    {
        InvalidOperationException when exception.Message.Contains("Cannot pause", StringComparison.OrdinalIgnoreCase) => "recording_cannot_pause",
        InvalidOperationException when exception.Message.Contains("Cannot resume", StringComparison.OrdinalIgnoreCase) => "recording_cannot_resume",
        InvalidOperationException when exception.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) => "ffmpeg_unavailable",
        InvalidOperationException when exception.Message.Contains("already", StringComparison.OrdinalIgnoreCase) => "recording_already_active",
        FileNotFoundException when exception.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) => "ffmpeg_unavailable",
        IOException when exception.Message.Contains("storage", StringComparison.OrdinalIgnoreCase) => "recording_storage_unavailable",
        UnauthorizedAccessException => "recording_archive_access_denied",
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException => "recording_archive_path_unavailable",
        _ => "recorder_command_failed"
    };

    private static Guid? ReadGuid(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool HasProperty(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out _);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var pending = _finalizations.Values.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            catch (TimeoutException) { logger.LogWarning("Timed out waiting for {Count} recording finalizations during service shutdown.", pending.Length); }
        }
        await base.StopAsync(cancellationToken);
    }
    private static string DataRoot() => Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text.Json;
using WhisperX.Atom.Recorder;

var dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
Directory.CreateDirectory(dataRoot);

Log.Logger = new LoggerConfiguration()
    .WriteTo.File(Path.Combine(dataRoot, "agent-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();
    builder.Services.AddSingleton(new SpoolStore(dataRoot));
    builder.Services.AddSingleton<AgentStateMachine>();
    builder.Services.AddSingleton<RecordingCoordinator>();
    builder.Services.AddSingleton<AgentApiClient>();
    builder.Services.AddHostedService<AgentPipeHost>();
    builder.Services.AddHostedService<RecorderWorker>();
    builder.Services.AddWindowsService(options => options.ServiceName = "WhisperX Atom Recorder Agent");
    await builder.Build().RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

public sealed class RecorderWorker(SpoolStore spool, AgentStateMachine state, RecordingCoordinator recorder, AgentApiClient api, ILogger<RecorderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await spool.InitializeAsync(stoppingToken);
        logger.LogInformation("Recorder Agent initialized. State={State}, chunkSeconds={ChunkSeconds}, commandChannel={CommandChannel}", state.State, RecordingContract.ChunkDurationSeconds, api.IsConfigured);
        var recoveryCompleted = false;
        if (api.IsConfigured)
        {
            recoveryCompleted = await RecoverPendingSessionsAsync(stoppingToken);
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_AUTORECORD_SECONDS"), out var seconds) && seconds > 0)
        {
            logger.LogInformation("Automatic recording smoke is enabled for {Seconds} seconds.", seconds);
            var localSession = await recorder.StartAsync(ReadEnvironmentMeetingId(), null, stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
            finally
            {
                await recorder.StopAsync(CancellationToken.None);
                await UploadAndFinalizeAsync(localSession, CancellationToken.None);
            }
        }

        var persistedCursor = await spool.GetCursorAsync(stoppingToken);
        var configuredCursor = long.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_COMMAND_CURSOR"), out var initialCursor) ? initialCursor : 0;
        var cursor = Math.Max(persistedCursor, configuredCursor);
        var lastHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!api.IsConfigured)
            {
                recoveryCompleted = false;
                var pending = await spool.PendingChunksAsync(1, stoppingToken);
                logger.LogDebug("Spool pending chunks: {Count}", pending.Count);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            try
            {
                if (!recoveryCompleted)
                {
                    recoveryCompleted = await RecoverPendingSessionsAsync(stoppingToken);
                }
                if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(30))
                {
                    var health = DeviceHealthSnapshot.Collect(Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent"));
                    if (await api.HeartbeatAsync(health, stoppingToken)) lastHeartbeat = DateTimeOffset.UtcNow;
                    logger.LogDebug("Device health: microphone={Microphone}, captureDevices={CaptureDevices}, systemAudio={SystemAudio}, renderDevices={RenderDevices}, freeBytes={FreeBytes}, error={Error}",
                        health.Microphone, health.CaptureDeviceCount, health.SystemAudio, health.RenderDeviceCount, health.FreeBytes, health.Error);
                }
                using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cursor = await FlushPendingCommandResultsAsync(cursor, stoppingToken);
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                IReadOnlyList<AgentCommandEnvelope> commands;
                try { commands = await api.ReadCommandsAsync(cursor, pollTimeout.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { commands = Array.Empty<AgentCommandEnvelope>(); }
                foreach (var command in commands)
                {
                    var saved = await spool.GetCommandResultAsync(command.Id, stoppingToken);
                    if (saved is null)
                    {
                        var executed = await ExecuteCommandAsync(command, stoppingToken);
                        await spool.SaveCommandResultAsync(command.Id, command.Cursor, executed.Status, executed.Payload, stoppingToken);
                        saved = await spool.GetCommandResultAsync(command.Id, stoppingToken);
                    }
                    if (saved is null || !await api.CompleteCommandAsync(command.Id, saved.Status, saved.Result, stoppingToken))
                    {
                        logger.LogWarning("Command result was not acknowledged; cursor remains at {Cursor}. Command={CommandId}", cursor, command.Id);
                        break;
                    }
                    await spool.AcknowledgeCommandResultAsync(command.Id, command.Cursor, stoppingToken);
                    cursor = Math.Max(cursor, command.Cursor);
                }
                var uploaded = await api.UploadPendingChunksAsync(spool, stoppingToken);
                if (uploaded > 0) logger.LogInformation("Uploaded {Count} confirmed audio chunks.", uploaded);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent command or upload channel is unavailable; local spool remains authoritative.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<(string Status, object Payload)> ExecuteCommandAsync(AgentCommandEnvelope command, CancellationToken cancellationToken)
    {
        try
        {
            var type = command.CommandType.Trim().ToUpperInvariant();
            switch (type)
            {
                case "START":
                case "START_RECORDING":
                    var meetingId = ReadMeetingId(command.Payload) ?? ReadEnvironmentMeetingId();
                    var title = ReadTitle(command.Payload);
                    var session = await recorder.StartAsync(meetingId, title, cancellationToken);
                    Guid? serverSession = null;
                    try { serverSession = await api.BindSessionAsync(session, meetingId, title, recorder.ActiveTracks, spool, cancellationToken); }
                    catch (Exception ex) { logger.LogWarning(ex, "Server session binding failed; recording continues locally. Session={SessionId}", session); }
                    return ("COMPLETED", new { ok = true, sessionId = session, serverSessionId = serverSession, state = state.State.ToString() });
                case "PAUSE":
                case "PAUSE_RECORDING":
                    await recorder.PauseAsync(cancellationToken);
                    return ("COMPLETED", new { ok = true, state = state.State.ToString() });
                case "RESUME":
                case "RESUME_RECORDING":
                    await recorder.ResumeAsync(cancellationToken);
                    return ("COMPLETED", new { ok = true, state = state.State.ToString() });
                case "STOP":
                case "STOP_RECORDING":
                    var localSession = recorder.SessionId;
                    await recorder.StopAsync(cancellationToken);
                    var finalized = await UploadAndFinalizeAsync(localSession, cancellationToken);
                    return ("COMPLETED", new { ok = true, serverFinalized = finalized, state = state.State.ToString() });
                case "MARKER":
                    return await ExecuteEventAsync("MARKER", command.Payload, cancellationToken);
                case "DECISION":
                    return await ExecuteEventAsync("DECISION", command.Payload, cancellationToken);
                case "ACTION_ITEM":
                    return await ExecuteEventAsync("ACTION_ITEM", command.Payload, cancellationToken);
                case "VOICE_EVENT":
                    return await ExecuteEventAsync(ReadString(command.Payload, "eventType") ?? "VOICE_COMMAND", command.Payload, cancellationToken);                case "STATUS":
                case "GET_STATUS":
                    return ("COMPLETED", new { ok = true, state = state.State.ToString(), sessionId = recorder.SessionId });
                default:
                    return ("REJECTED", new { ok = false, error = "command_not_supported", command = command.CommandType });
            }
        }
        catch (Exception ex)
        {
            return ("FAILED", new { ok = false, error = "command_failed", detail = ex.Message });
        }
    }

    private async Task<(string Status, object Payload)> ExecuteEventAsync(string eventType, JsonElement payload, CancellationToken cancellationToken)
    {
        var sessionId = recorder.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return ("REJECTED", new { ok = false, error = "recording_not_active" });
        var mediaTimeMs = recorder.CurrentMediaTimeMs ?? 0;
        await spool.AddEventAsync(sessionId, eventType, mediaTimeMs, JsonSerializer.Serialize(payload), cancellationToken);
        return ("COMPLETED", new { ok = true, eventType, mediaTimeMs, sessionId });
    }    private async Task<bool> UploadAndFinalizeAsync(string? localSessionId, CancellationToken cancellationToken)
    {
        if (!api.IsConfigured || string.IsNullOrWhiteSpace(localSessionId)) return false;
        try
        {
            await api.UploadPendingChunksAsync(spool, cancellationToken);
            var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
            if (serverSessionId is not Guid server) return false;
            var finalized = await api.FinalizeServerSessionAsync(server, localSessionId, spool, cancellationToken);
            if (finalized)
            {
                await spool.PurgeFinalizedSessionAsync(localSessionId, cancellationToken);
            }
            return finalized;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Server finalize failed; chunks remain in the local spool. Session={SessionId}", localSessionId);
            return false;
        }
    }

    private async Task<bool> RecoverPendingSessionsAsync(CancellationToken cancellationToken)
    {
        var completed = true;
        foreach (var localSessionId in await spool.SessionsNeedingRecoveryAsync(cancellationToken))
        {
            try
            {
                var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
                if (serverSessionId is null)
                {
                    var meetingId = await spool.GetMeetingIdAsync(localSessionId, cancellationToken);
                    var tracks = await spool.GetTrackInfosAsync(localSessionId, cancellationToken);
                    if (meetingId is not Guid meeting || tracks.Count == 0)
                    {
                        logger.LogInformation("Offline session {SessionId} remains local until a meeting and tracks are available.", localSessionId);
                        completed = false;
                        continue;
                    }
                    serverSessionId = await api.BindSessionAsync(localSessionId, meeting, await spool.GetTitleAsync(localSessionId, cancellationToken), tracks, spool, cancellationToken);
                }
                await spool.SetSessionStateAsync(localSessionId, "FINALIZING", cancellationToken);
                await spool.AddEventAsync(localSessionId, "RECOVERED_AFTER_RESTART", cancellationToken: cancellationToken);
                if (!await UploadAndFinalizeAsync(localSessionId, cancellationToken))
                {
                    completed = false;
                    logger.LogWarning("Recovery finalize is still pending. Session={SessionId}", localSessionId);
                }
            }
            catch (Exception ex)
            {
                completed = false;
                logger.LogWarning(ex, "Failed to recover recording session {SessionId}; local spool remains authoritative.", localSessionId);
            }
        }
        return completed;
    }

    private async Task<long> FlushPendingCommandResultsAsync(long cursor, CancellationToken cancellationToken)
    {
        foreach (var pending in await spool.PendingCommandResultsAsync(cancellationToken))
        {
            if (!await api.CompleteCommandAsync(pending.CommandId, pending.Status, pending.Result, cancellationToken))
            {
                logger.LogWarning("Pending command result is still unacknowledged. Command={CommandId}", pending.CommandId);
                break;
            }
            await spool.AcknowledgeCommandResultAsync(pending.CommandId, pending.Cursor, cancellationToken);
            cursor = Math.Max(cursor, pending.Cursor);
        }
        return cursor;
    }
    private static Guid? ReadMeetingId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("meetingId", out var value)) return null;
        return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;
    }

    private static Guid? ReadEnvironmentMeetingId() => Guid.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_MEETING_ID"), out var id) ? id : null;
    private static string? ReadTitle(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("title", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var localSession = recorder.SessionId;
        await recorder.StopAsync(cancellationToken);
        await UploadAndFinalizeAsync(localSession, cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}

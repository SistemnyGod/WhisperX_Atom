using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text.Json;
using WhisperX.Atom.Recorder;

// The legacy Windows Service must remain isolated from the interactive
// AudioGraph Host.  Make its Machine-scope configuration explicit so a
// current-user config cannot be selected accidentally after installation.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATOM_AGENT_CONFIG_PATH")))
{
    var machineConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WhisperXAtom", "Agent", "agent-config.json");
    Environment.SetEnvironmentVariable("ATOM_AGENT_CONFIG_PATH", machineConfigPath);
}

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
    builder.Services.AddSingleton<DeviceHealthMonitor>();
    builder.Services.AddSingleton<AgentStorageSettings>();
    builder.Services.AddSingleton<IAudioDeviceProbe, LegacyWasapiDeviceProbe>();
    builder.Services.AddSingleton<IAudioCaptureEngineFactory, LegacyWasapiCaptureEngineFactory>();
    builder.Services.AddSingleton<AgentStateMachine>();
    builder.Services.AddSingleton<RecordingCoordinator>();
    builder.Services.AddSingleton<AgentApiClient>();
    builder.Services.AddSingleton<LocalArchiveWriter>();
    builder.Services.AddSingleton<SessionFinalizationCoordinator>();
    builder.Services.AddSingleton<RecordingDeliveryCoordinator>();
    builder.Services.AddSingleton<RawChunkRecovery>();
    builder.Services.AddHostedService<AgentPipeHost>();
    builder.Services.AddHostedService<RecorderWorker>();
    builder.Services.AddWindowsService(options => options.ServiceName = "WhisperX Atom Recorder Agent");
    await builder.Build().RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

public sealed class RecorderWorker(SpoolStore spool, AgentStateMachine state, RecordingCoordinator recorder, AgentApiClient api, AgentStorageSettings storage, RecordingDeliveryCoordinator delivery, RawChunkRecovery rawRecovery, DeviceHealthMonitor deviceHealth, ILogger<RecorderWorker> logger) : BackgroundService
{
    private RecorderRuntimeLease? _runtimeLease;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The Service remains installed as an explicit Legacy WASAPI fallback.
        // When the machine/runtime selection is AudioGraph it must not recover,
        // upload or finalize the canonical spool in parallel with Recorder Host.
        if (RecorderServiceRuntime.IsAudioGraph)
        {
            logger.LogInformation("Legacy Recorder Service is in standby because AudioGraph Recorder Host owns the runtime.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            return;
        }

        try
        {
            _runtimeLease = RecorderRuntimeLease.Acquire(api.InstallationId);
            RecorderServiceRuntime.SetActive(true);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("RECORDER_RUNTIME_LEASE_HELD", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Legacy Recorder Service is in standby because another recorder runtime owns the canonical spool.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            return;
        }

        try
        {
        await spool.InitializeAsync(stoppingToken);
        logger.LogInformation("Recorder Agent initialized. State={State}, chunkSeconds={ChunkSeconds}, commandChannel={CommandChannel}", state.State, RecordingContract.ChunkDurationSeconds, api.IsConfigured);
        await rawRecovery.RecoverAsync(recorder.SessionId, stoppingToken);
        var recoveryCompleted = false;
        if (api.IsConfigured)
        {
            // Recover only durable sessions that were interrupted while still
            // pending. FAILED/CANCELLED sessions, including legacy local
            // archive failures, are intentionally not re-enqueued on startup.
            // They remain available for explicit diagnostic/manual retry.
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
        var lastRecovery = DateTimeOffset.MinValue;
        var lastActiveBindingAttempt = DateTimeOffset.MinValue;
        var lastRetention = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await rawRecovery.RecoverAsync(recorder.SessionId, stoppingToken);
            if (DateTimeOffset.UtcNow - lastRetention >= TimeSpan.FromMinutes(5))
            {
                // Eligibility is SQLite state based; this never scans filenames
                // or touches pending/failed transport.
                await spool.PurgeEligibleRawRecoveryAsync(stoppingToken);
                await spool.PurgeEligibleLocalArchivesAsync(stoppingToken);
                foreach (var candidate in await spool.GetTransportPurgeCandidatesAsync(DateTimeOffset.UtcNow, stoppingToken))
                    await spool.PurgeFinalizedSessionAsync(candidate.SessionId, stoppingToken);
                lastRetention = DateTimeOffset.UtcNow;
            }
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
                if (!recoveryCompleted || DateTimeOffset.UtcNow - lastRecovery >= TimeSpan.FromSeconds(10))
                {
                    recoveryCompleted = await RecoverPendingSessionsAsync(stoppingToken);
                    lastRecovery = DateTimeOffset.UtcNow;
                }
                if (DateTimeOffset.UtcNow >= api.NextHeartbeatAtUtc)
                {
                    var health = deviceHealth.Collect(Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent"), storage);
                    await api.HeartbeatAsync(health, stoppingToken);
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
                if (DateTimeOffset.UtcNow - lastActiveBindingAttempt >= TimeSpan.FromSeconds(5))
                {
                    lastActiveBindingAttempt = DateTimeOffset.UtcNow;
                    await EnsureActiveSessionBoundAsync(stoppingToken);
                }
                var uploaded = await api.UploadPendingChunksAsync(spool, stoppingToken);
                foreach (var session in await spool.SessionsWithPendingEventsAsync(stoppingToken)) await api.UploadPendingEventsAsync(spool, session, stoppingToken);
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
        finally
        {
            RecorderServiceRuntime.SetActive(false);
            _runtimeLease?.Dispose();
            _runtimeLease = null;
        }
    }

    private async Task<Guid?> EnsureActiveSessionBoundAsync(CancellationToken cancellationToken)
    {
        if (!api.IsConfigured || state.State is not (RecorderState.Recording or RecorderState.Paused)) return null;

        var localSessionId = recorder.SessionId;
        if (string.IsNullOrWhiteSpace(localSessionId)) return null;

        var tracks = recorder.ActiveTracks;
        if (tracks.Count == 0) return null;

        var existingServerSession = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
        if (existingServerSession is Guid existing)
        {
            var complete = true;
            foreach (var track in tracks)
            {
                if (await spool.GetServerBindingAsync(localSessionId, track.TrackId, cancellationToken) is null)
                {
                    complete = false;
                    break;
                }
            }
            if (complete) return existing;
        }

        try
        {
            var meetingId = await spool.GetMeetingIdAsync(localSessionId, cancellationToken);
            var title = await spool.GetTitleAsync(localSessionId, cancellationToken);
            var serverSessionId = await api.BindSessionAsync(localSessionId, meetingId, title, tracks, spool, cancellationToken);
            logger.LogInformation("Active recording server binding is ready. Session={SessionId}, ServerSession={ServerSessionId}", localSessionId, serverSessionId);
            return serverSessionId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Active recording server binding is unavailable; upload will retry without losing local chunks. Session={SessionId}", localSessionId);
            return null;
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
                    return ("COMPLETED", new { ok = true, sessionId = session, serverSessionId = (Guid?)null, binding = "PENDING", state = state.State.ToString() });
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
                case "CANCEL_SERVER_SESSION":
                    var serverSessionId = ReadServerSessionId(command.Payload);
                    if (serverSessionId is null)
                        return ("REJECTED", new { ok = false, error = "server_session_id_required" });
                    var discardTransport = ReadBoolean(command.Payload, "discardTransport");
                    var discarded = await spool.CancelServerSessionAsync(serverSessionId.Value, discardTransport, cancellationToken);
                    return ("COMPLETED", new { ok = true, serverSessionId, discardTransport, localSessions = discarded.Sessions, transportChunks = discarded.Chunks });
                case "CANCEL_LOCAL_SESSION":
                    var localSessionId = ReadString(command.Payload, "localSessionId");
                    if (string.IsNullOrWhiteSpace(localSessionId))
                        return ("REJECTED", new { ok = false, error = "local_session_id_required" });
                    var localDiscard = await spool.CancelLocalSessionAsync(localSessionId, ReadBoolean(command.Payload, "discardTransport"), cancellationToken);
                    return ("COMPLETED", new { ok = true, localSessionId, localSessions = localDiscard.Sessions, transportChunks = localDiscard.Chunks });
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
    }

    private static Guid? ReadServerSessionId(JsonElement payload)
        => payload.TryGetProperty("serverSessionId", out var value) && Guid.TryParse(value.GetString(), out var id) ? id : null;

    private static bool ReadBoolean(JsonElement payload, string property)
        => payload.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private async Task<bool> UploadAndFinalizeAsync(string? localSessionId, CancellationToken cancellationToken)
        => (await delivery.RunAsync(localSessionId, cancellationToken)).Success;


    private async Task<bool> RecoverPendingSessionsAsync(CancellationToken cancellationToken)
    {
        var completed = true;
        foreach (var localSessionId in await spool.SessionsNeedingRecoveryAsync(cancellationToken))
        {
            if (string.Equals(localSessionId, recorder.SessionId, StringComparison.Ordinal)
                && state.State is (RecorderState.Recording or RecorderState.Paused or RecorderState.Finalizing))
                continue;
            try
            {
                var previous = await spool.GetSessionInfoAsync(localSessionId, cancellationToken);
                await spool.SetSessionStateAsync(localSessionId, "FINALIZING", cancellationToken);
                await spool.AddEventIfMissingAsync(
                    localSessionId,
                    "RECORDER_RECOVERED_AFTER_RESTART",
                    JsonSerializer.Serialize(new
                    {
                        previousState = previous?.State,
                        previousLocalFinalizeState = previous?.LocalFinalizeState,
                        previousDeliveryState = previous?.DeliveryState,
                        recoveredAt = DateTimeOffset.UtcNow
                    }),
                    cancellationToken);
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
    private static string? ReadString(JsonElement payload, string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? ReadTitle(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("title", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // If the Service is standby because Host owns the lease, it must not
        // touch the shared spool during shutdown either.
        if (_runtimeLease is not null)
        {
            var localSession = recorder.SessionId;
            await recorder.StopAsync(cancellationToken);
            await UploadAndFinalizeAsync(localSession, cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Runtime selection for the separately installed legacy Windows Service.
/// Desktop process environment variables are not inherited by SCM-launched
/// services, so the installer passes an explicit argument. A missing argument
/// remains legacy for backwards compatibility with older service entries.
/// </summary>
internal static class RecorderServiceRuntime
{
    private static int _active;

    public static bool IsAudioGraph { get; } = ResolveEngine() == RecorderRuntimeResolver.AudioGraph;
    public static bool IsActive => Volatile.Read(ref _active) == 1;

    public static void SetActive(bool active) => Volatile.Write(ref _active, active ? 1 : 0);

    private static string ResolveEngine()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--runtime=", StringComparison.OrdinalIgnoreCase))
                return Normalize(argument["--runtime=".Length..]);
            if (string.Equals(argument, "--runtime", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return Normalize(args[index + 1]);
        }

        return RecorderRuntimeResolver.LegacyWasapi;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant() switch
    {
        RecorderRuntimeResolver.AudioGraph => RecorderRuntimeResolver.AudioGraph,
        _ => RecorderRuntimeResolver.LegacyWasapi
    };
}

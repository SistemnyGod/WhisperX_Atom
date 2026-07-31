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
        if (int.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_AUTORECORD_SECONDS"), out var seconds) && seconds > 0)
        {
            logger.LogInformation("Automatic recording smoke is enabled for {Seconds} seconds.", seconds);
            var localSession = await recorder.StartAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
            finally
            {
                await recorder.StopAsync(CancellationToken.None);
                await UploadAndFinalizeAsync(localSession, CancellationToken.None);
            }
        }

        var cursor = long.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_COMMAND_CURSOR"), out var initialCursor) ? initialCursor : 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!api.IsConfigured)
            {
                var pending = await spool.PendingChunksAsync(1, stoppingToken);
                logger.LogDebug("Spool pending chunks: {Count}", pending.Count);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            try
            {
                using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                IReadOnlyList<AgentCommandEnvelope> commands;
                try { commands = await api.ReadCommandsAsync(cursor, pollTimeout.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { commands = Array.Empty<AgentCommandEnvelope>(); }
                foreach (var command in commands)
                {
                    cursor = Math.Max(cursor, command.Cursor);
                    var result = await ExecuteCommandAsync(command, stoppingToken);
                    await api.CompleteCommandAsync(command.Id, result.Status, result.Payload, stoppingToken);
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
                    var session = await recorder.StartAsync(cancellationToken);
                    var meetingId = ReadMeetingId(command.Payload) ?? ReadEnvironmentMeetingId();
                    Guid? serverSession = null;
                    if (meetingId is Guid meeting)
                    {
                        try { serverSession = await api.BindSessionAsync(session, meeting, recorder.ActiveTracks, spool, cancellationToken); }
                        catch (Exception ex) { logger.LogWarning(ex, "Server session binding failed; recording continues locally. Session={SessionId}", session); }
                    }
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
                case "STATUS":
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

    private async Task<bool> UploadAndFinalizeAsync(string? localSessionId, CancellationToken cancellationToken)
    {
        if (!api.IsConfigured || string.IsNullOrWhiteSpace(localSessionId)) return false;
        try
        {
            await api.UploadPendingChunksAsync(spool, cancellationToken);
            var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
            if (serverSessionId is not Guid server) return false;
            await api.FinalizeServerSessionAsync(server, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Server finalize failed; chunks remain in the local spool. Session={SessionId}", localSessionId);
            return false;
        }
    }

    private static Guid? ReadMeetingId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("meetingId", out var value)) return null;
        return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;
    }

    private static Guid? ReadEnvironmentMeetingId() => Guid.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_MEETING_ID"), out var id) ? id : null;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var localSession = recorder.SessionId;
        await recorder.StopAsync(cancellationToken);
        await UploadAndFinalizeAsync(localSession, cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}

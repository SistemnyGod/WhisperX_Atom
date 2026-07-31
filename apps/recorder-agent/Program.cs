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
            await recorder.StartAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
            finally { await recorder.StopAsync(CancellationToken.None); }
        }

        var cursor = long.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_COMMAND_CURSOR"), out var initialCursor) ? initialCursor : 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (api.IsConfigured)
            {
                try
                {
                    var commands = await api.ReadCommandsAsync(cursor, stoppingToken);
                    foreach (var command in commands)
                    {
                        cursor = Math.Max(cursor, command.Cursor);
                        var result = await ExecuteCommandAsync(command, stoppingToken);
                        await api.CompleteCommandAsync(command.Id, result.Status, result.Payload, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Agent command channel is unavailable.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            else
            {
                var pending = await spool.PendingChunksAsync(1, stoppingToken);
                logger.LogDebug("Spool pending chunks: {Count}", pending.Count);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
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
                    return ("COMPLETED", new { ok = true, sessionId = session, state = state.State.ToString() });
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
                    await recorder.StopAsync(cancellationToken);
                    return ("COMPLETED", new { ok = true, state = state.State.ToString() });
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await recorder.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}

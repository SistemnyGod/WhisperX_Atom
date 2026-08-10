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
    ILogger<AgentPipeHost> logger) : BackgroundService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Task> _finalizations = new(StringComparer.Ordinal);

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
                    return Status(includeHealth: true);
                case "CONFIGURE":
                    var serverUrl = ReadString(request.Payload, "serverUrl");
                    var agentToken = ReadString(request.Payload, "token");
                    var agentId = ReadGuid(request.Payload, "agentId");
                    if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(agentToken) || agentId is not Guid configuredAgent)
                        return Error("agent_configuration_invalid");
                    await api.ConfigureAsync(serverUrl, configuredAgent, agentToken, cancellationToken);
                    return Status();
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
        var task = CompleteFinalizationAsync(stop, meetingId);
        _finalizations[stop.SessionId] = task;
        _ = task.ContinueWith(completed => _finalizations.TryRemove(stop.SessionId, out _), TaskScheduler.Default);
    }

    private async Task CompleteFinalizationAsync(RecordingStopHandle stop, Guid? meetingId)
    {
        try
        {
            await stop.LocalFinalization;
            await FinalizeAsync(stop.SessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording finalization failed after local stop. Session={SessionId}, Meeting={MeetingId}", stop.SessionId, meetingId);
            await spool.SetSessionStateAsync(stop.SessionId!, "FAILED", CancellationToken.None);
        }
    }
    private async Task<bool> FinalizeAsync(string? localSessionId, CancellationToken cancellationToken)
    {
        if (!api.IsConfigured || string.IsNullOrWhiteSpace(localSessionId)) return false;
        await api.UploadPendingChunksAsync(spool, cancellationToken);
        await api.UploadPendingEventsAsync(spool, localSessionId, cancellationToken);
        var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
        if (serverSessionId is not Guid server) return false;
        var finalized = await api.FinalizeServerSessionAsync(server, localSessionId, spool, cancellationToken);
        if (finalized) await spool.PurgeFinalizedSessionAsync(localSessionId, cancellationToken);
        return finalized;
    }

    private AgentIpcResponse Status(bool includeHealth = false)
    {
        var health = includeHealth ? DeviceHealthSnapshot.Collect(DataRoot()) : null;
        return new AgentIpcResponse(true, state.State.ToString(), recorder.SessionId, null, health is null ? null : new AgentIpcHealth(
            health.Microphone, health.SystemAudio, health.CaptureDeviceCount, health.RenderDeviceCount, health.FreeBytes, health.TotalBytes, health.Error), null, recorder.CurrentMediaTimeMs);
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
        InvalidOperationException when exception.Message.Contains("already", StringComparison.OrdinalIgnoreCase) => "recording_already_active",
        IOException when exception.Message.Contains("storage", StringComparison.OrdinalIgnoreCase) => "recording_storage_unavailable",
        UnauthorizedAccessException => "recording_access_denied",
        _ => "recorder_command_failed"
    };

    private static Guid? ReadGuid(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

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

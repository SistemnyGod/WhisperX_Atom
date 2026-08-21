using System.IO.Pipes;
using System.Text.Json;

namespace WhisperX.Atom.Voice.Host;

/// <summary>For managed mode the Voice Host delegates all recorder mutations
/// to Desktop. This keeps buttons and speech on the same command path.</summary>
internal sealed class VoiceIntentBrokerClient
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;

    public VoiceIntentBrokerClient(string pipeName = VoiceHostIpc.DesktopBrokerPipeName) => _pipeName = pipeName;

    public async Task<VoiceBrokerResponse> ExecuteAsync(
        string intent,
        string text,
        double confidence,
        bool testMode,
        CancellationToken cancellationToken,
        string? traceId = null,
        string? commandId = null)
    {
        traceId ??= Guid.NewGuid().ToString("N");
        try
        {
            // Retry only the connection handshake. Once a request is written
            // we never replay a mutation, so a delayed ACK cannot duplicate
            // START/STOP/marker commands.
            await using var pipe = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new
            {
                command = "EXECUTE_INTENT",
                intent,
                text,
                confidence,
                timestamp = DateTimeOffset.UtcNow,
                traceId,
                commandId,
                testMode
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: "empty_response");
            return JsonSerializer.Deserialize<VoiceBrokerResponse>(line, _json)
                ?? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: "invalid_response", TraceId: traceId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: "timeout", TraceId: traceId);
        }
        catch (Exception ex)
        {
            return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: ex.GetType().Name, TraceId: traceId);
        }
    }

    public async Task<VoiceBrokerResponse> AskAssistantAsync(string question, string? requestedMode, bool testMode, CancellationToken cancellationToken, string? traceId = null, string? commandId = null)
    {
        traceId ??= Guid.NewGuid().ToString("N");
        // Acceptance is durable on the server; a slow LAN must not leave the
        // Voice Host in EXECUTING forever. This timeout applies only to this
        // broker request and never cancels Recorder or host shutdown.
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var requestCancellation = requestTimeout.Token;
        try
        {
            await using var pipe = await ConnectWithRetryAsync(requestCancellation).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new { command = "ASSISTANT_QUESTION", question, requestedMode = requestedMode ?? "AUTO", testMode, timestamp = DateTimeOffset.UtcNow, traceId, commandId };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), requestCancellation).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(requestCancellation).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(line)
                ? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId, CommandId: commandId)
                : JsonSerializer.Deserialize<VoiceBrokerResponse>(line, _json) ?? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId, CommandId: commandId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "VOICE_ASSISTANT_ACCEPTANCE_TIMEOUT", Detail: "broker_timeout", TraceId: traceId, CommandId: commandId);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return new(false, "VOICE_ASSISTANT_DESKTOP_UNAVAILABLE", Detail: ex.GetType().Name, TraceId: traceId, CommandId: commandId);
        }
    }

    public async Task<VoiceBrokerResponse> PublishLiveAsrSegmentsAsync(
        Guid? recordingSessionId,
        IReadOnlyList<VoiceLiveAsrSegment> segments,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new
            {
                command = "LIVE_ASR_SEGMENTS",
                recordingSessionId,
                segments,
                timestamp = DateTimeOffset.UtcNow
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(line)
                ? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: "empty_response")
                : JsonSerializer.Deserialize<VoiceBrokerResponse>(line, _json)
                    ?? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: "invalid_response");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: ex.GetType().Name);
        }
    }

    public async Task<VoiceBrokerResponse> GetAssistantResultAsync(Guid queryId, CancellationToken cancellationToken, string? traceId = null, string? commandId = null)
    {
        traceId ??= Guid.NewGuid().ToString("N");
        try
        {
            await using var pipe = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new { command = "ASSISTANT_RESULT", queryId, timestamp = DateTimeOffset.UtcNow, traceId, commandId };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(line)
                ? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId, CommandId: commandId)
                : JsonSerializer.Deserialize<VoiceBrokerResponse>(line, _json) ?? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId, CommandId: commandId);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: ex.GetType().Name, TraceId: traceId, CommandId: commandId);
        }
    }

    private async Task<NamedPipeClientStream> ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                pipe.Dispose();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                pipe.Dispose();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }
    }

    public async Task<VoiceBrokerResponse> RecordEventAsync(string eventType, object payload, CancellationToken cancellationToken, string? localSessionId = null)
    {
        var traceId = Guid.NewGuid().ToString("N");
        try
        {
            await using var pipe = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new { command = "RECORD_EVENT", eventType, payload, localSessionId, timestamp = DateTimeOffset.UtcNow, traceId };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(line) ? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId) : JsonSerializer.Deserialize<VoiceBrokerResponse>(line, _json) ?? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId);
        }
        catch { return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId); }
    }

    public async Task<VoiceBrokerResponse> PublishAssistantPlaybackFinishedAsync(
        string queryId,
        string responseId,
        string playbackState,
        bool cancelled,
        string? commandId,
        string? traceId,
        string? localSessionId,
        CancellationToken cancellationToken)
    {
        traceId ??= Guid.NewGuid().ToString("N");
        try
        {
            await using var pipe = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new
            {
                command = "ASSISTANT_PLAYBACK_FINISHED",
                queryId,
                responseId,
                playbackState,
                cancelled,
                commandId,
                traceId,
                localSessionId,
                timestamp = DateTimeOffset.UtcNow
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(line)
                ? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId, CommandId: commandId)
                : JsonSerializer.Deserialize<VoiceBrokerResponse>(line, _json)
                    ?? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId, CommandId: commandId);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: ex.GetType().Name, TraceId: traceId, CommandId: commandId);
        }
    }
}

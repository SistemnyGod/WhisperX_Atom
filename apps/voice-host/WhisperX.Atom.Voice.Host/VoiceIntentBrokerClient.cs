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
        string? traceId = null)
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

    public async Task<VoiceBrokerResponse> RecordEventAsync(string eventType, object payload, CancellationToken cancellationToken)
    {
        var traceId = Guid.NewGuid().ToString("N");
        try
        {
            await using var pipe = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new { command = "RECORD_EVENT", eventType, payload, timestamp = DateTimeOffset.UtcNow, traceId };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(line) ? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId) : JsonSerializer.Deserialize<VoiceBrokerResponse>(line, _json) ?? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId);
        }
        catch { return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", TraceId: traceId); }
    }
}

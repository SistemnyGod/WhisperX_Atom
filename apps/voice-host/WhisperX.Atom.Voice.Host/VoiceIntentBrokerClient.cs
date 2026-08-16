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
        CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new
            {
                command = "EXECUTE_INTENT",
                intent,
                text,
                confidence,
                timestamp = DateTimeOffset.UtcNow,
                traceId = Guid.NewGuid(),
                testMode
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: "empty_response");
            return JsonSerializer.Deserialize<VoiceBrokerResponse>(line, _json)
                ?? new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: "invalid_response");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: "timeout");
        }
        catch (Exception ex)
        {
            return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: ex.GetType().Name);
        }
    }
}

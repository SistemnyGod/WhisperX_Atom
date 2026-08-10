using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace WhisperX.Atom.Desktop;

public sealed record DesktopVoiceSnapshot(string State, bool Enabled, bool PushToTalk, string? LastRecognizedText, string? LastResponse, string? PendingConfirmation, DateTimeOffset UpdatedAt, bool IsSpeaking = false, bool ModelReady = false, bool ModelIntegrityReady = false, bool NativeRuntimeReady = false, bool MicrophoneReady = false, bool RecorderPipeReady = false, string? RecorderPipeError = null, string Sensitivity = "balanced", string? LastIntent = null, string? LastErrorCode = null, double? WakeLatencyMs = null, double? IntentLatencyMs = null, double? RecorderAckLatencyMs = null, double? TotalLatencyMs = null, double? LastCommandLatencyMs = null, int AudioQueueDepth = 0, long AudioQueueDrops = 0);
public sealed record DesktopVoiceResponse(bool Ok, JsonElement? Data, string? Error);

public sealed class VoiceHostClient
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<DesktopVoiceResponse> SendAsync(string command, object? payload = null, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", "WhisperXAtomVoiceHost", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(500, cancellationToken);
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload ?? new { }, _json));
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, payload = document.RootElement.Clone() }, _json));
        var line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line)) throw new IOException("Voice Host returned an empty response.");
        using var result = JsonDocument.Parse(line);
        var root = result.RootElement;
        return new DesktopVoiceResponse(root.GetProperty("ok").GetBoolean(), root.TryGetProperty("data", out var data) ? data.Clone() : null, root.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    public async Task<DesktopVoiceSnapshot?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("STATUS", cancellationToken: cancellationToken);
        return response.Ok && response.Data is JsonElement data ? data.Deserialize<DesktopVoiceSnapshot>(_json) : null;
    }
}

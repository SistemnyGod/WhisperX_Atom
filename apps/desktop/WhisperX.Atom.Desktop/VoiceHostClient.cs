using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace WhisperX.Atom.Desktop;

 public sealed record DesktopVoiceSnapshot(string State, bool Enabled, bool PushToTalk, string? LastRecognizedText, string? LastResponse, string? PendingConfirmation, DateTimeOffset UpdatedAt, bool IsSpeaking = false, bool ModelReady = false, bool ModelIntegrityReady = false, bool NativeRuntimeReady = false, bool MicrophoneReady = false, bool RecorderPipeReady = false, string? RecorderPipeError = null, string Sensitivity = "balanced", string? LastIntent = null, string? LastErrorCode = null, double? WakeLatencyMs = null, double? IntentLatencyMs = null, double? RecorderAckLatencyMs = null, double? TotalLatencyMs = null, double? LastCommandLatencyMs = null, int AudioQueueDepth = 0, long AudioQueueDrops = 0, string? EffectiveMicrophoneName = null, double? MicrophonePeak = null, DateTimeOffset? LastAudioAtUtc = null, double? MicrophoneRms = null, bool MicrophoneClipping = false, string? AudioSignalState = null, long AudioTelemetrySequence = 0, string? BuildIdentity = null, string? WakeWordMode = null, int? ProcessId = null, string? LastTraceId = null, DateTimeOffset? HeartbeatAtUtc = null, string? RequestedMicrophoneDeviceId = null, string? EffectiveMicrophoneDeviceId = null, string? LastCommandId = null, int RestartCount = 0, string? RestartState = null, string? MicrophoneErrorDetail = null, string? RequestedVoiceName = null, string? EffectiveVoiceName = null, string? EffectiveVoiceCulture = null, bool VoiceFallbackUsed = false, int SpeechQueueDepth = 0, long SpeechQueueDrops = 0, string LiveAudioMode = "MIC_FALLBACK", string LiveRoomTrackState = "WAITING", string LiveSystemTrackState = "WAITING", long LiveAudioDrops = 0, long LiveSegmentsPublished = 0, long LiveSegmentsSuppressed = 0, string TtsEngine = "SILERO", string TtsModel = "v5_5_ru", bool TtsReady = false, string? TtsVoice = null, string? TtsCulture = null, int? TtsSampleRate = 48000, int? TtsCpuThreads = 4, int? TtsHostProcessId = null, long TtsModelLoadMs = 0, long TtsLastSynthesisMs = 0, bool TtsFallbackUsed = false, string? TtsFallbackReason = null, int TtsRestartCount = 0, double? VoiceNoiseFloorDb = null, double? VoiceVadThresholdDb = null);
public sealed record DesktopVoiceResponse(bool Ok, JsonElement? Data, string? Error);

public sealed class VoiceHostIpcException : IOException
{
    public bool RequestWritten { get; }
    public VoiceHostIpcException(string message, bool requestWritten, Exception? inner = null) : base(message, inner) => RequestWritten = requestWritten;
}

public sealed class VoiceHostClient
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<DesktopVoiceResponse> SendAsync(string command, object? payload = null, CancellationToken cancellationToken = default)
    {
        var requestWritten = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "WhisperXAtomVoiceHost", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload ?? new { }, _json));
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, payload = document.RootElement.Clone() }, _json)).ConfigureAwait(false);
            requestWritten = true;
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) throw new IOException("Voice Host returned an empty response.");
            using var result = JsonDocument.Parse(line);
            var root = result.RootElement;
            return new DesktopVoiceResponse(root.GetProperty("ok").GetBoolean(), root.TryGetProperty("data", out var data) ? data.Clone() : null, root.TryGetProperty("error", out var error) ? error.GetString() : null);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            throw new VoiceHostIpcException($"Voice Host IPC failed before={(!requestWritten)}.", requestWritten, ex);
        }
    }

    public async Task<DesktopVoiceSnapshot?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("STATUS", cancellationToken: cancellationToken);
        return response.Ok && response.Data is JsonElement data ? data.Deserialize<DesktopVoiceSnapshot>(_json) : null;
    }

    /// <summary>Reads the latest-only telemetry stream until the page is closed or the host disconnects.</summary>
    public async Task SubscribeTelemetryAsync(Func<VoiceTelemetryPacket, Task> onPacket, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", "WhisperXAtomVoiceTelemetry", PipeDirection.In, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1000, cancellationToken);
        using var reader = new StreamReader(pipe);
        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) break;
            var packet = JsonSerializer.Deserialize<VoiceTelemetryPacket>(line, _json);
            if (packet is not null) await onPacket(packet).ConfigureAwait(false);
        }
    }
}

public sealed record VoiceTelemetryPacket(
    long Sequence,
    DateTimeOffset? AtUtc,
    double Rms,
    double Peak,
    bool Clipping,
    string SignalState,
    string? DeviceId = null,
    string? DeviceName = null);

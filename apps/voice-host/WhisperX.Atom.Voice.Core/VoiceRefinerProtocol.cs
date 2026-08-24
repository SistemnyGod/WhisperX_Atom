using System.Text.Json.Serialization;

namespace WhisperX.Atom.Voice;

/// <summary>Private current-user protocol between Voice Host and the resident refiner.</summary>
public static class VoiceRefinerProtocol
{
    public const int SchemaVersion = 1;
    public const int NativeAbiVersion = 1;
    public const string PipePrefix = "WhisperXAtomVoiceRefiner-";
    public const int MaxPcmBytes = 960_000; // 30 seconds, 16 kHz mono PCM16
    public const int QueueCapacity = 2;
    public const int HostInferenceTimeoutMs = 15_000;
    public const int ClientTimeoutMs = 18_000;
    public const int InferenceTimeoutExitCode = 73;

    public static string PipeName => PipePrefix + SanitizeUser(Environment.UserName);

    private static string SanitizeUser(string value)
    {
        var chars = value.Where(static c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "current-user" : new string(chars);
    }
}

public enum VoiceRefinerMode
{
    Off,
    Shadow,
    AssistantOnly,
    WakeAudit
}

public enum VoiceRefinerHostState
{
    Starting,
    Ready,
    Busy,
    Degraded,
    Stopped
}

public sealed record VoiceRefinerRequest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("buildIdentity")] string BuildIdentity,
    [property: JsonPropertyName("utteranceId")] string? UtteranceId = null,
    [property: JsonPropertyName("sequence")] long Sequence = 0,
    [property: JsonPropertyName("sampleRate")] int SampleRate = 16000,
    [property: JsonPropertyName("pcmBytes")] int PcmBytes = 0,
    [property: JsonPropertyName("nativeAbiVersion")] int NativeAbiVersion = VoiceRefinerProtocol.NativeAbiVersion);

public sealed record VoiceRefinerResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("requestId")] string? RequestId = null,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("hostState")] string? HostState = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("confidence")] double? Confidence = null,
    [property: JsonPropertyName("processingMs")] double ProcessingMs = 0,
    [property: JsonPropertyName("queueWaitMs")] double? QueueWaitMs = null,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("buildIdentity")] string? BuildIdentity = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("modelReady")] bool ModelReady = false,
    [property: JsonPropertyName("queueDepth")] int QueueDepth = 0,
    [property: JsonPropertyName("nativeAbiVersion")] int? NativeAbiVersion = null,
    [property: JsonPropertyName("utteranceId")] string? UtteranceId = null,
    [property: JsonPropertyName("sequence")] long? Sequence = null,
    [property: JsonPropertyName("sampleRate")] int? SampleRate = null);

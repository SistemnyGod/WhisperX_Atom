using System.Text.Json;

namespace WhisperX.Atom.Voice.Host;

public static class VoiceHostIpc
{
    public const string PipeName = "WhisperXAtomVoiceHost";
    public const string DesktopBrokerPipeName = "WhisperXAtomDesktopVoiceBroker";
    public const string TelemetryPipeName = "WhisperXAtomVoiceTelemetry";
}

public sealed record VoiceHostRequest(string Command, JsonElement Payload);
public sealed record VoiceHostResponse(bool Ok, object? Data = null, string? Error = null);

public sealed record VoiceTelemetryPacket(
    long Sequence,
    DateTimeOffset? AtUtc,
    double Rms,
    double Peak,
    bool Clipping,
    string SignalState,
    string? DeviceId = null,
    string? DeviceName = null);

internal sealed record VoiceLiveAsrSegment(
    Guid Id,
    long StartMs,
    long EndMs,
    string Text,
    double? Confidence = null,
    int Revision = 0);

internal sealed record VoiceBrokerResponse(
    bool Ok,
    string? ErrorCode = null,
    string? RecorderState = null,
    string? LocalSessionId = null,
    string? LocalFinalizeState = null,
    string? SpokenText = null,
    string? Detail = null,
    string? TraceId = null,
    string? CommandId = null,
    string? QueryId = null,
    string? AssistantStatus = null,
    string? ResolvedMode = null);

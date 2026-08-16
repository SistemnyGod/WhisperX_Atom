using System.Text.Json;

namespace WhisperX.Atom.Voice.Host;

public static class VoiceHostIpc
{
    public const string PipeName = "WhisperXAtomVoiceHost";
    public const string DesktopBrokerPipeName = "WhisperXAtomDesktopVoiceBroker";
}

public sealed record VoiceHostRequest(string Command, JsonElement Payload);
public sealed record VoiceHostResponse(bool Ok, object? Data = null, string? Error = null);

internal sealed record VoiceBrokerResponse(
    bool Ok,
    string? ErrorCode = null,
    string? RecorderState = null,
    string? LocalSessionId = null,
    string? LocalFinalizeState = null,
    string? SpokenText = null,
    string? Detail = null);

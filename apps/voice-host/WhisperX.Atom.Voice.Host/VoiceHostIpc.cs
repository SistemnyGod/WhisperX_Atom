using System.Text.Json;

namespace WhisperX.Atom.Voice.Host;

public static class VoiceHostIpc
{
    public const string PipeName = "WhisperXAtomVoiceHost";
}

public sealed record VoiceHostRequest(string Command, JsonElement Payload);
public sealed record VoiceHostResponse(bool Ok, object? Data = null, string? Error = null);

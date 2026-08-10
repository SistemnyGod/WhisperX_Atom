using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public static class AgentIpcProtocol
{
    public const string PipeName = "WhisperXAtomAgent";
    public const int Version = 2;
}

public sealed record AgentIpcRequest(string Command, JsonElement Payload);

public sealed record AgentIpcResponse(
    bool Ok,
    string State,
    string? SessionId,
    string? Error,
    AgentIpcHealth? Health,
    Guid? MeetingId = null,
    long? MediaTimeMs = null,
    int ProtocolVersion = AgentIpcProtocol.Version);

public sealed record AgentIpcHealth(
    bool Microphone,
    bool SystemAudio,
    int CaptureDeviceCount,
    int RenderDeviceCount,
    long FreeBytes,
    long TotalBytes,
    string? Error);

using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public static class AgentIpcProtocol
{
    public const string PipeName = "WhisperXAtomAgent";
    public const int Version = 5;
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
    int ProtocolVersion = AgentIpcProtocol.Version,
    AgentPreflightResult? Preflight = null,
    RecordingSessionStatus? SessionStatus = null);

public sealed record AgentIpcHealth(
    bool Microphone,
    bool SystemAudio,
    int CaptureDeviceCount,
    int RenderDeviceCount,
    long FreeBytes,
    long TotalBytes,
    string? Error,
    string? ArchiveRoot = null,
    int PendingUploadSessions = 0,
    IReadOnlyList<AgentIpcAudioDevice>? CaptureDevices = null,
    IReadOnlyList<AgentIpcAudioDevice>? RenderDevices = null,
    string? SelectedMicrophoneDeviceId = null,
    string? SelectedSystemAudioDeviceId = null,
    int RawChunksPending = 0,
    int RawChunksWriting = 0,
    int RawChunksEncoding = 0,
    int RawChunksFailed = 0,
    long RawChunksBytes = 0,
    double? MicrophonePeak = null,
    double? SystemAudioPeak = null,
    double? MicrophoneDb = null,
    double? SystemAudioDb = null,
    Guid? InstallationId = null,
    Guid? AgentId = null,
    string ServerConnectionState = "UNKNOWN",
    DateTimeOffset? LastHeartbeatAtUtc = null,
    string? LastServerError = null);

public sealed record AgentIpcAudioDevice(
    string Id,
    string Name,
    bool IsDefault,
    string State = "Active");

public sealed record AgentPreflightResult(
    bool Ready,
    bool Microphone,
    bool SystemAudio,
    bool Ffmpeg,
    bool Spool,
    bool Archive,
    long FreeBytes,
    long MinimumFreeBytes,
    string BackendConnectionState,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record RecordingSessionStatus(
    string SessionId,
    Guid? MeetingId,
    string CaptureState,
    string DeliveryState,
    int LocalChunkCount,
    int ConfirmedChunkCount,
    int PendingChunkCount,
    string? Error,
    Guid? ServerSessionId = null,
    string LocalFinalizeState = "PENDING",
    string? ArchivePath = null,
    string? ErrorCode = null,
    bool Retryable = true,
    DateTimeOffset? NextRetryAtUtc = null);

public sealed record FinalizationResult(
    bool Success,
    string Stage,
    string? ErrorCode = null,
    bool Retryable = true,
    string? ArchivePath = null,
    Guid? ServerSessionId = null,
    IReadOnlyList<int>? MissingChunks = null);

using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public static class AgentIpcProtocol
{
    public const string PipeName = "WhisperXAtomAgent";
    public const int Version = 6;
    public const int LegacyVersion = 5;
    public const int MinimumSupportedVersion = 5;
    public const int MaxServerInstances = 8;
}

public static class RecorderPipeNames
{
    public const string LegacyService = AgentIpcProtocol.PipeName;
    public const string AudioGraphHost = "WhisperXAtomRecorderHost";

    public static string ForCurrentProcess() => RecorderRuntimeResolver.Current.PipeName;
}

public static class RecorderRuntimeMode
{
    public static bool IsAudioGraph => RecorderRuntimeResolver.Current.IsAudioGraph;
}

public sealed record AgentIpcRequest(string Command, JsonElement Payload)
{
    public int ProtocolVersion { get; init; } = AgentIpcProtocol.LegacyVersion;
}

public sealed record AudioSourceTestResult(
    bool Success,
    string? DeviceId,
    string? FriendlyName,
    bool SignalDetected,
    double? AverageRmsDb,
    double? PeakDb,
    bool Clipping,
    long DurationMs,
    string? ErrorCode = null,
    bool StreamOpened = false,
    bool StreamStarted = false,
    int PacketCount = 0,
    long BytesReceived = 0,
    long? FirstPacketLatencyMs = null,
    int? SampleRate = null,
    int? Channels = null,
    string? RawSampleFormat = null,
    string CaptureState = "UNKNOWN",
    string? ProcessUser = null,
    string? ProcessSid = null,
    int? WindowsSessionId = null,
    int? ProcessId = null,
    string? DefaultMultimediaEndpointId = null,
    string? DefaultCommunicationsEndpointId = null,
    string? SelectedEndpointId = null,
    bool EndpointFound = false,
    bool EndpointActive = false,
    bool AccessGranted = false,
    bool FormatResolved = false,
    string? ErrorDetail = null);

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
    RecordingSessionStatus? SessionStatus = null,
    AudioSourceTestResult? AudioSourceTest = null,
    AudioDeviceProbeResult? AudioGraphProbe = null,
    int MinimumSupportedProtocolVersion = AgentIpcProtocol.MinimumSupportedVersion,
    int CurrentProtocolVersion = AgentIpcProtocol.Version);

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
    string? LastServerError = null,
    double? MicrophoneRms = null,
    double? SystemAudioRms = null,
    double? MicrophoneRmsDb = null,
    double? SystemAudioRmsDb = null,
    bool? MicrophoneClipping = null,
    bool? SystemAudioClipping = null,
    DateTimeOffset? MicrophoneLastAudioAtUtc = null,
    DateTimeOffset? SystemAudioLastAudioAtUtc = null,
    long? MicrophoneSilenceDurationMs = null,
    long? SystemAudioSilenceDurationMs = null,
    bool MicrophoneTelemetryStale = true,
    bool SystemAudioTelemetryStale = true,
    string RawBacklogHealth = "HEALTHY",
    // Additive fields: older Desktop/Voice Host clients can still deserialize
    // the v5 payload while newer clients can distinguish the live capture from
    // sessions being delivered in the background.
    string? ActiveSessionId = null,
    int BackgroundPendingSessions = 0,
    int BackgroundFailedSessions = 0,
    int RawChunksReady = 0,
    int RawChunksReadyForUpload = 0,
    string StorageWatermarkState = "NORMAL",
    double StorageFreePercent = 100,
    string? StorageWatermarkReason = null,
    string RecordingProfile = "ROOM",
    bool RecordingProfileManaged = false,
    AudioSourceTestResult? MicrophoneProbe = null,
    AudioSourceTestResult? SystemAudioProbe = null,
    bool? MicrophoneCaptureReady = null,
    bool? SystemAudioCaptureReady = null,
    string? MicrophoneCaptureState = null,
    string? SystemAudioCaptureState = null,
    string CaptureEngine = "LEGACY_WASAPI",
    string RecorderProcessModel = "WINDOWS_SERVICE",
    bool DeviceWatcherReady = false,
    bool AudioGraphReady = false,
    bool FirstFrameConfirmed = false,
    bool UserReselectRequired = false,
    // The confirmed configuration remains SelectedMicrophoneDeviceId. For
    // DEFAULT selection that value is null, while this value identifies the
    // endpoint Windows resolved for the current Host runtime.
    string? EffectiveMicrophoneDeviceId = null);

public sealed record AgentIpcAudioDevice(
    string Id,
    string Name,
    bool IsDefault,
    string State = "Active",
    string DataFlow = "Unknown",
    bool IsDefaultConsole = false,
    bool IsDefaultMultimedia = false,
    bool IsDefaultCommunications = false,
    int? SampleRate = null,
    int? Channels = null,
    string? SourceEncoding = null,
    string? SourceSubFormat = null,
    int? BitsPerSample = null,
    int? ValidBitsPerSample = null,
    string? NormalizedSampleFormat = null,
    DateTimeOffset? LastSeenAtUtc = null);

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
    IReadOnlyList<string> Errors,
    string StorageWatermarkState = "NORMAL",
    double StorageFreePercent = 100,
    string? StorageWatermarkReason = null);

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
    DateTimeOffset? NextRetryAtUtc = null,
    Guid? MediaAssetId = null,
    Guid? ProcessingJobId = null,
    string? TraceId = null,
    int ChunksReady = 0,
    int ChunksUploading = 0,
    int ChunksFailed = 0,
    long BytesPending = 0,
    double? OldestPendingAgeSeconds = null);

public sealed record FinalizationResult(
    bool Success,
    string Stage,
    string? ErrorCode = null,
    bool Retryable = true,
    string? ArchivePath = null,
    Guid? ServerSessionId = null,
    IReadOnlyList<int>? MissingChunks = null,
    string? ErrorMessage = null,
    Guid? MeetingId = null,
    Guid? MediaAssetId = null,
    Guid? ProcessingJobId = null,
    string? TraceId = null,
    DateTimeOffset? NextRetryAtUtc = null,
    string LocalArchiveState = "PENDING",
    string DeliveryState = "NOT_STARTED",
    string ServerFinalizeState = "NOT_STARTED",
    string MediaState = "PENDING",
    int? ErrorHttpStatus = null);

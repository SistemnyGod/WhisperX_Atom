using System.Text.Json;
using System.Reflection;

namespace WhisperX.Atom.Recorder;

public static class AgentIpcProtocol
{
    public const string PipeName = "WhisperXAtomAgent";
    public const int Version = 6;
    public const int LegacyVersion = 5;
    public const int MinimumSupportedVersion = 5;
    public const int MaxServerInstances = 8;

    public const string ConcurrentRequestsCapability = "CONCURRENT_REQUESTS";
    public const string DeviceEventStreamCapability = "DEVICE_EVENT_STREAM";
    public const string AudioTelemetryStreamCapability = "AUDIO_TELEMETRY_STREAM_V1";
    // The current-user Host can persist a render-loopback source as a second
    // track without changing the v6 wire shape. Older clients simply ignore
    // this additive capability and continue using the room microphone track.
    public const string IndependentSystemAudioTrackCapability = "SYSTEM_AUDIO_TRACK_V1";

    public static string CurrentBuildIdentity
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AgentIpcProtocol).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "UNKNOWN";
        }
    }
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
    int CurrentProtocolVersion = AgentIpcProtocol.Version,
    string? ErrorDetail = null,
    AgentIpcAudioTelemetry? AudioTelemetry = null)
{
    /// <summary>
    /// True when the local IPC endpoint answered with a state payload. Health
    /// may be accompanied by a warning (for example an unavailable optional
    /// system-audio endpoint),
    /// so command success must not be used as a liveness signal.
    /// </summary>
    public bool IsReachable => Health is not null || Ok;

    public bool HasMicrophoneCapture => Health?.MicrophoneCaptureReady == true || Health?.AudioGraphReady == true;
}

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
    string? EffectiveMicrophoneDeviceId = null,
    string? RuntimeBuildIdentity = null,
    IReadOnlyList<string>? Capabilities = null,
    string? EffectiveMicrophoneDeviceName = null,
    string? EffectiveSystemAudioDeviceId = null,
    string? EffectiveSystemAudioDeviceName = null,
    string MicrophoneSignalState = "UNKNOWN",
    AudioGraphAttemptDiagnostics? LastAudioGraphAttempt = null,
    string? RuntimeUser = null,
    string? RuntimeSid = null,
    int? WindowsSessionId = null,
    string? ServerOrigin = null,
    // Additive v6 diagnostics. Startup/recovery are independent from capture;
    // a DEGRADED recovery state must not make the microphone unavailable.
    string StartupState = "UNKNOWN",
    string RecoveryState = "NOT_STARTED",
    int RecoveryPendingCount = 0,
    string EncoderState = "UNKNOWN",
    int RawFinalizerQueueDepth = 0,
    int RawFinalizerMaximumDepth = 0,
    int RawFinalizerCapacity = 0,
    int RawTerminalFailedCount = 0,
    DateTimeOffset? EncoderLastHeartbeatAtUtc = null,
    string? EncoderCurrentChunkId = null,
    DateTimeOffset? EncoderLastSuccessAtUtc = null,
    string? EncoderLastErrorCode = null,
    int EncoderQueueDepth = 0);

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

public sealed record AgentIpcAudioTelemetry(
    long Sequence,
    long MediaTimeMs,
    double RmsLinear,
    double PeakLinear,
    double RmsDb,
    double PeakDb,
    bool Clipping,
    string SignalState,
    string? EffectiveDeviceId,
    string? EffectiveDeviceName,
    DateTimeOffset CapturedAtUtc,
    bool IsStale = false);

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
    string? StorageWatermarkReason = null,
    // Optional v6 tail fields. Older Desktop/Hosts deserialize the original
    // prefix and continue to use Ready; newer clients can distinguish local
    // capture from encoder and delivery availability.
    bool CaptureReady = false,
    bool EncodingReady = true,
    bool DeliveryReady = true,
    bool Ffprobe = true);

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
    double? OldestPendingAgeSeconds = null,
    string EncodingState = "PENDING",
    string ArchiveState = "PENDING",
    int RawChunkCount = 0,
    int RawWritingCount = 0,
    int RawReadyCount = 0,
    int RawEncodingCount = 0,
    int RawCompletedCount = 0,
    int RawFailedCount = 0,
    long RawBytes = 0,
    double? RawOldestPendingAgeSeconds = null,
    string RawBacklogHealth = "HEALTHY",
    int RawFinalizerQueueDepth = 0,
    int RawFinalizerMaximumDepth = 0,
    int RawFinalizerCapacity = 0,
    string? ArchiveErrorCode = null,
    string? ArchiveErrorDetail = null,
    int RawTerminalFailedCount = 0);

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

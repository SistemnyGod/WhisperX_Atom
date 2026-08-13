using System.Threading.Channels;

namespace WhisperX.Atom.Recorder;

public enum AudioEngineKind
{
    LegacyWasapi,
    AudioGraph
}

public enum AudioSelectionMode
{
    Default,
    Fixed
}

public enum AudioDataFlow
{
    Capture,
    Render
}

public enum AudioSampleType
{
    Pcm16,
    Pcm24,
    Pcm32,
    Float32
}

/// <summary>
/// The only format shape allowed outside a platform-specific audio adapter.
/// AudioGraph microphone capture currently normalizes to 48 kHz mono PCM16.
/// </summary>
public sealed record AudioStreamFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int ValidBitsPerSample,
    AudioSampleType SampleType,
    int ChannelMask = 0)
{
    public int BytesPerSample => BitsPerSample / 8;
    public int BytesPerFrame => BytesPerSample * Channels;
    public string CanonicalEncoding => SampleType switch
    {
        AudioSampleType.Pcm16 => "PCM_S16LE",
        AudioSampleType.Pcm24 => "PCM_S24LE",
        AudioSampleType.Pcm32 => "PCM_S32LE",
        AudioSampleType.Float32 => "FLOAT32",
        _ => throw new ArgumentOutOfRangeException()
    };
    public string FfmpegInput => SampleType switch
    {
        AudioSampleType.Pcm16 => "s16le",
        AudioSampleType.Pcm24 => "s24le",
        AudioSampleType.Pcm32 => "s32le",
        AudioSampleType.Float32 => "f32le",
        _ => throw new ArgumentOutOfRangeException()
    };
}

public static class AudioStreamFormats
{
    /// <summary>The canonical Phase 1 microphone contract.</summary>
    public static AudioStreamFormat Phase1Microphone { get; } =
        new(48000, 1, 16, 16, AudioSampleType.Pcm16);
}

public enum AudioCaptureState
{
    Unknown,
    Discovered,
    Selected,
    Checking,
    Starting,
    Recording,
    Paused,
    Stopped,
    DeviceLost,
    AccessBlocked,
    Failed
}

public sealed record AudioDeviceDescriptor(
    string Id,
    string Name,
    AudioDataFlow DataFlow,
    string State,
    bool IsDefault,
    AudioSelectionMode SelectionMode,
    bool IsSelected,
    DateTimeOffset LastSeenAtUtc,
    string RuntimeStatus = "DISCOVERED");

public sealed class AudioDeviceChangedEventArgs(string eventType, AudioDeviceDescriptor? device, DateTimeOffset atUtc) : EventArgs
{
    public string EventType { get; } = eventType;
    public AudioDeviceDescriptor? Device { get; } = device;
    public DateTimeOffset AtUtc { get; } = atUtc;
}

public sealed record AudioDeviceProbeResult(
    string? DeviceId,
    string? DeviceName,
    bool EndpointFound,
    bool EndpointActive,
    bool AccessGranted,
    bool FormatResolved,
    bool StreamOpened,
    bool StreamStarted,
    int FrameCount,
    long BytesReceived,
    long? FirstFrameLatencyMs,
    bool SignalDetected,
    double? AverageRmsDb,
    double? PeakDb,
    bool Clipping,
    int SampleRate,
    int Channels,
    string NormalizedSampleFormat,
    string? ErrorCode = null,
    string? ErrorDetail = null,
    string CaptureState = "UNKNOWN")
{
    public bool Ready => EndpointFound && EndpointActive && AccessGranted && FormatResolved
        && StreamOpened && StreamStarted && FrameCount > 0 && BytesReceived > 0;
}

public sealed record AudioTelemetrySnapshot(
    long FrameCount,
    long BytesReceived,
    long? FirstFrameLatencyMs,
    double? RmsDb,
    double? PeakDb,
    bool Clipping,
    DateTimeOffset? LastAudioAtUtc,
    long? SilenceDurationMs,
    bool IsStale = true);

/// <summary>
/// A normalized frame emitted by an audio engine. The Phase 1 contract is
/// always 48 kHz, mono, PCM16; ownership of the byte array transfers to the
/// bounded frame pipeline and it must not be mutated afterwards.
/// </summary>
public sealed record AudioFrame(
    long StartSample,
    int SampleCount,
    DateTimeOffset CapturedAtUtc,
    byte[] Pcm16Bytes,
    double Rms,
    double Peak,
    bool Clipping,
    AudioStreamFormat? Format = null);

public sealed record AudioFramePacket(
    long Sequence,
    long StartSample,
    DateTimeOffset CapturedAtUtc,
    byte[] Buffer,
    int SampleCount,
    AudioStreamFormat Format,
    double Rms,
    double Peak,
    bool Clipping);

public interface IAudioDeviceCatalog : IAsyncDisposable
{
    IReadOnlyList<AudioDeviceDescriptor> Devices { get; }

    bool IsReady { get; }

    event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task ReconcileAsync(CancellationToken cancellationToken = default);

    AudioDeviceDescriptor? Resolve(AudioSelectionMode mode, string? deviceId);
}

public interface IAudioCaptureEngine : IAsyncDisposable
{
    AudioEngineKind Kind { get; }

    IAudioDeviceCatalog DeviceCatalog { get; }

    AudioDeviceDescriptor? SelectedDevice { get; }

    AudioCaptureState State { get; }

    AudioTelemetrySnapshot Telemetry { get; }

    ChannelReader<AudioFrame> Frames { get; }

    event EventHandler<AudioDeviceChangedEventArgs>? DeviceStateChanged;

    event EventHandler<AudioCaptureFailureEventArgs>? CaptureFailed;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<AudioDeviceProbeResult> ProbeAsync(
        AudioSelectionMode selectionMode,
        string? deviceId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task SelectDeviceAsync(
        AudioSelectionMode selectionMode,
        string? deviceId,
        CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IAudioCaptureEngineFactory
{
    IAudioCaptureEngine Create(string profile);
}

public interface IAudioDeviceProbe
{
    AudioEngineKind EngineKind { get; }
    Task<AudioDeviceProbeResult> ProbeAsync(
        IAudioCaptureEngine engine,
        AudioSelectionMode selectionMode,
        string? deviceId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}

public interface IAudioChunkEncoder
{
    Task EncodeAsync(
        string inputPath,
        string outputPath,
        AudioStreamFormat format,
        CancellationToken cancellationToken = default);
}

public sealed class AudioCaptureFailureEventArgs(string errorCode, string? detail, bool retryable, DateTimeOffset atUtc) : EventArgs
{
    public string ErrorCode { get; } = errorCode;
    public string? Detail { get; } = detail;
    public bool Retryable { get; } = retryable;
    public DateTimeOffset AtUtc { get; } = atUtc;
}

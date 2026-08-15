using System.Buffers;
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
    string CaptureState = "UNKNOWN",
    AudioGraphAttemptDiagnostics? AttemptDiagnostics = null)
{
    public bool Ready => EndpointFound && EndpointActive && AccessGranted && FormatResolved
        && StreamOpened && StreamStarted && FrameCount > 0 && BytesReceived > 0;
}

/// <summary>
/// Persistent diagnostics for one AudioGraph attempt. This is deliberately a
/// platform-neutral DTO: it contains no Windows Audio objects and remains
/// available after the graph and nodes have been disposed.
/// </summary>
public sealed class AudioGraphAttemptDiagnostics
{
    public bool GraphCreateAttempted { get; set; }
    public string? GraphCreationStatus { get; set; }
    public bool GraphCreated { get; set; }
    public int? GraphExtendedErrorHResult { get; set; }

    public bool InputNodeCreateAttempted { get; set; }
    public string? InputNodeCreationStatus { get; set; }
    public bool InputNodeCreated { get; set; }
    public int? InputNodeExtendedErrorHResult { get; set; }

    public bool OutputNodeCreated { get; set; }
    public bool ConnectionCreated { get; set; }
    public bool GraphStartCalled { get; set; }

    public long QuantumStartedCount { get; set; }
    public long GetFrameCallCount { get; set; }
    public long EmptyFrameCount { get; set; }
    public long NonEmptyFrameCount { get; set; }
    public int? AudioBufferLengthLast { get; set; }
    public int? AudioBufferCapacityLast { get; set; }
    public string? OutputSubtype { get; set; }
    public int? OutputBitsPerSample { get; set; }
    public int? OutputSampleRate { get; set; }
    public int? OutputChannelCount { get; set; }
    public int? ObservedBytesPerSample { get; set; }
    public string? ObservedSampleFormat { get; set; }
    public bool FormatIntegrityVerified { get; set; }
    public bool FormatMismatch { get; set; }
    public long NonFiniteSampleCount { get; set; }
    public int? RequestedSamplesPerQuantum { get; set; }
    public int? GraphSamplesPerQuantum { get; set; }
    public long NativeFrameBytes { get; set; }
    public long NormalizedFrameBytes { get; set; }
    public string? NormalizationMode { get; set; }
    public long BytesReceived { get; set; }
    public long? FirstQuantumLatencyMs { get; set; }
    public long? FirstFrameLatencyMs { get; set; }

    // Realtime pipeline counters. These are monotonic for the current
    // capture attempt and intentionally remain optional for older Hosts.
    public long FramesProduced { get; set; }
    public long FramesConsumed { get; set; }
    public int CurrentQueueDepth { get; set; }
    public int MaximumQueueDepth { get; set; }
    public long PipelineOverruns { get; set; }

    public bool UnrecoverableErrorOccurred { get; set; }
    public int? UnrecoverableErrorHResult { get; set; }
    public string? FinalCaptureState { get; set; }
    public string? FinalErrorCode { get; set; }
    public string? FinalErrorDetail { get; set; }

    public AudioGraphAttemptDiagnostics Clone() => new()
    {
        GraphCreateAttempted = GraphCreateAttempted,
        GraphCreationStatus = GraphCreationStatus,
        GraphCreated = GraphCreated,
        GraphExtendedErrorHResult = GraphExtendedErrorHResult,
        InputNodeCreateAttempted = InputNodeCreateAttempted,
        InputNodeCreationStatus = InputNodeCreationStatus,
        InputNodeCreated = InputNodeCreated,
        InputNodeExtendedErrorHResult = InputNodeExtendedErrorHResult,
        OutputNodeCreated = OutputNodeCreated,
        ConnectionCreated = ConnectionCreated,
        GraphStartCalled = GraphStartCalled,
        QuantumStartedCount = QuantumStartedCount,
        GetFrameCallCount = GetFrameCallCount,
        EmptyFrameCount = EmptyFrameCount,
        NonEmptyFrameCount = NonEmptyFrameCount,
        AudioBufferLengthLast = AudioBufferLengthLast,
        AudioBufferCapacityLast = AudioBufferCapacityLast,
        OutputSubtype = OutputSubtype,
        OutputBitsPerSample = OutputBitsPerSample,
        OutputSampleRate = OutputSampleRate,
        OutputChannelCount = OutputChannelCount,
        ObservedBytesPerSample = ObservedBytesPerSample,
        ObservedSampleFormat = ObservedSampleFormat,
        FormatIntegrityVerified = FormatIntegrityVerified,
        FormatMismatch = FormatMismatch,
        NonFiniteSampleCount = NonFiniteSampleCount,
        RequestedSamplesPerQuantum = RequestedSamplesPerQuantum,
        GraphSamplesPerQuantum = GraphSamplesPerQuantum,
        NativeFrameBytes = NativeFrameBytes,
        NormalizedFrameBytes = NormalizedFrameBytes,
        NormalizationMode = NormalizationMode,
        BytesReceived = BytesReceived,
        FirstQuantumLatencyMs = FirstQuantumLatencyMs,
        FirstFrameLatencyMs = FirstFrameLatencyMs,
        FramesProduced = FramesProduced,
        FramesConsumed = FramesConsumed,
        CurrentQueueDepth = CurrentQueueDepth,
        MaximumQueueDepth = MaximumQueueDepth,
        PipelineOverruns = PipelineOverruns,
        UnrecoverableErrorOccurred = UnrecoverableErrorOccurred,
        UnrecoverableErrorHResult = UnrecoverableErrorHResult,
        FinalCaptureState = FinalCaptureState,
        FinalErrorCode = FinalErrorCode,
        FinalErrorDetail = FinalErrorDetail
    };
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
    bool IsStale = true)
{
    // Desktop's waveform contract is a normalized linear amplitude (0..1),
    // while the legacy health DTO also exposes the human-readable dB value.
    // Keep both derived from the same normalized PCM16 samples.
    public double? PeakLinear => PeakDb is double peakDb
        ? Math.Clamp(Math.Pow(10d, peakDb / 20d), 0d, 1d)
        : null;
}

/// <summary>
/// Short-window signal telemetry used by the live Desktop monitor. It is
/// deliberately separate from AudioTelemetrySnapshot, whose values are
/// cumulative diagnostics for the complete capture attempt.
/// </summary>
public sealed record LiveAudioTelemetrySnapshot(
    long Sequence,
    long MediaTimeMs,
    double RmsLinear,
    double PeakLinear,
    bool Clipping,
    DateTimeOffset CapturedAtUtc,
    bool IsStale = false)
{
    public double RmsDb => ToDb(RmsLinear);
    public double PeakDb => ToDb(PeakLinear);

    private static double ToDb(double value)
        => value <= 0 ? -60d : Math.Clamp(20d * Math.Log10(value), -60d, 0d);
}

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
    AudioStreamFormat? Format = null) : IDisposable
{
    /// <summary>Number of valid bytes in <see cref="Pcm16Bytes"/>.</summary>
    public int Pcm16Length { get; init; } = Pcm16Bytes.Length;

    /// <summary>Optional owner for an ArrayPool-rented buffer.</summary>
    public PooledAudioBuffer? BufferOwner { get; init; }

    public ReadOnlyMemory<byte> Pcm16Memory => Pcm16Bytes.AsMemory(0, Math.Min(Pcm16Length, Pcm16Bytes.Length));

    public void Dispose() => BufferOwner?.Dispose();
}

/// <summary>
/// Single-owner lease for a pooled audio buffer. The lease is transferred to
/// AudioFrame and returned exactly once when the consumer has persisted it or
/// the queue rejects the frame.
/// </summary>
public sealed class PooledAudioBuffer : IDisposable
{
    private byte[]? _buffer;

    public PooledAudioBuffer(byte[] buffer, int length)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        Length = Math.Clamp(length, 0, buffer.Length);
    }

    public int Length { get; }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PooledAudioBuffer));

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}

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

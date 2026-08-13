using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// User-session microphone engine. The graph is configured to emit the
/// normalized Phase 1 format: 48 kHz, mono, PCM16.
/// </summary>
public sealed class AudioGraphCaptureEngine : IAudioCaptureEngine
{
    private const int SampleRate = RecordingContract.MicrophoneSampleRate;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int QueueCapacity = 32;

    private readonly AudioGraphDeviceCatalog _catalog;
    private readonly object _gate = new();
    private readonly Stopwatch _captureClock = new();
    private Channel<AudioFrame> _frames = CreateChannel();
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _inputNode;
    private AudioFrameOutputNode? _outputNode;
    private AudioEncodingProperties? _encodingProperties;
    private AudioDeviceDescriptor? _selectedDevice;
    private AudioSelectionMode _selectionMode = AudioSelectionMode.Default;
    private string? _requestedDeviceId;
    private AudioCaptureState _state = AudioCaptureState.Unknown;
    private AudioTelemetrySnapshot _telemetry = new(0, 0, null, null, null, false, null, null);
    private long _sampleCursor;
    private long _frameCount;
    private long _bytesReceived;
    private long? _firstFrameLatencyMs;
    private double _rmsSum;
    private double _peak;
    private bool _clipping;
    private DateTimeOffset? _lastAudioAtUtc;
    private DateTimeOffset? _silenceStartedAtUtc;
    private int _failureRaised;

    public AudioGraphCaptureEngine(AudioGraphDeviceCatalog catalog)
    {
        _catalog = catalog;
        _catalog.DeviceChanged += OnCatalogDeviceChanged;
    }

    public AudioEngineKind Kind => AudioEngineKind.AudioGraph;

    public IAudioDeviceCatalog DeviceCatalog => _catalog;

    public AudioDeviceDescriptor? SelectedDevice { get { lock (_gate) return _selectedDevice; } }

    public AudioCaptureState State { get { lock (_gate) return _state; } }

    public AudioTelemetrySnapshot Telemetry
    {
        get
        {
            lock (_gate)
            {
                double? averageRms = _frameCount == 0 ? null : _rmsSum / _frameCount;
                double? rmsDb = averageRms is null ? null : ToDb(averageRms.Value);
                double? peakDb = _peak <= 0 ? null : ToDb(_peak);
                long? silence = _silenceStartedAtUtc is null || _lastAudioAtUtc is not null && _lastAudioAtUtc > _silenceStartedAtUtc
                    ? null
                    : (long?)(DateTimeOffset.UtcNow - _silenceStartedAtUtc.Value).TotalMilliseconds;
                return new AudioTelemetrySnapshot(_frameCount, _bytesReceived, _firstFrameLatencyMs, rmsDb, peakDb, _clipping, _lastAudioAtUtc, silence, _frameCount == 0);
            }
        }
    }

    public ChannelReader<AudioFrame> Frames => _frames.Reader;

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceStateChanged;

    public event EventHandler<AudioCaptureFailureEventArgs>? CaptureFailed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _catalog.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_catalog.Resolve(AudioSelectionMode.Default, null) is null)
            SetState(AudioCaptureState.DeviceLost);
        else
            SetState(AudioCaptureState.Discovered);
    }

    public async Task<AudioDeviceProbeResult> ProbeAsync(
        AudioSelectionMode selectionMode,
        string? deviceId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        await SelectDeviceAsync(selectionMode, deviceId, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : duration, cancellationToken).ConfigureAwait(false);
            return BuildProbeResult(ElapsedMilliseconds(started), null, null);
        }
        catch (Exception ex)
        {
            var code = ex is OperationCanceledException ? "AUDIO_TEST_CANCELLED" : AudioGraphErrorMapper.Map(ex);
            return BuildProbeResult(ElapsedMilliseconds(started), code, ex.Message);
        }
        finally
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    public Task SelectDeviceAsync(AudioSelectionMode selectionMode, string? deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State is AudioCaptureState.Recording or AudioCaptureState.Paused or AudioCaptureState.Starting)
            throw new InvalidOperationException("AUDIO_DEVICE_SELECTION_LOCKED");
        var selected = _catalog.Resolve(selectionMode, deviceId);
        if (selected is null || !string.Equals(selected.State, "Active", StringComparison.OrdinalIgnoreCase))
        {
            SetState(AudioCaptureState.DeviceLost);
            throw new InvalidOperationException(selectionMode == AudioSelectionMode.Fixed
                ? "AUDIO_DEVICE_UNAVAILABLE"
                : "AUDIO_DEFAULT_ENDPOINT_MISSING");
        }

        lock (_gate)
        {
            _selectionMode = selectionMode;
            _requestedDeviceId = deviceId;
            _selectedDevice = selected with { SelectionMode = selectionMode, IsSelected = true };
        }
        SetState(AudioCaptureState.Selected);
        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State is AudioCaptureState.Recording or AudioCaptureState.Paused)
            throw new InvalidOperationException("AUDIO_CAPTURE_ALREADY_RUNNING");
        // DEFAULT is resolved at every START. A transient endpoint selected by a
        // previous session must never turn DEFAULT into an implicit FIXED choice.
        if (_selectionMode == AudioSelectionMode.Default)
            await SelectDeviceAsync(AudioSelectionMode.Default, null, cancellationToken).ConfigureAwait(false);
        else if (_selectedDevice is null)
            await SelectDeviceAsync(_selectionMode, _requestedDeviceId, cancellationToken).ConfigureAwait(false);

        var selected = SelectedDevice ?? throw new InvalidOperationException("AUDIO_DEVICE_NOT_SELECTED");
        var information = _catalog.GetDeviceInformation(selected.Id);
        if (information is null) throw new InvalidOperationException("AUDIO_DEVICE_NOT_FOUND");

        await DisposeGraphAsync().ConfigureAwait(false);
        ResetTelemetry();
        _frames = CreateChannel();
        _failureRaised = 0;
        SetState(AudioCaptureState.Starting);

        _encodingProperties = AudioEncodingProperties.CreatePcm(SampleRate, Channels, BitsPerSample);
        var settings = new AudioGraphSettings(AudioRenderCategory.Media)
        {
            EncodingProperties = _encodingProperties,
            DesiredSamplesPerQuantum = SampleRate / 100
        };
        var creation = await AudioGraph.CreateAsync(settings);
        if (creation.Status != AudioGraphCreationStatus.Success || creation.Graph is null)
            throw new InvalidOperationException($"AUDIOGRAPH_CREATE_FAILED:{creation.Status}");
        _graph = creation.Graph;

        var inputResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, _encodingProperties, information);
        if (!string.Equals(inputResult.Status.ToString(), "Success", StringComparison.OrdinalIgnoreCase) || inputResult.DeviceInputNode is null)
            throw new InvalidOperationException($"AUDIO_DEVICE_CREATE_FAILED:{inputResult.Status}:{inputResult.ExtendedError?.HResult}");
        _inputNode = inputResult.DeviceInputNode;
        _outputNode = _graph.CreateFrameOutputNode(_encodingProperties);
        _inputNode.AddOutgoingConnection(_outputNode);
        _graph.QuantumStarted += OnQuantumStarted;
        _graph.UnrecoverableErrorOccurred += OnUnrecoverableError;
        _captureClock.Restart();
        _graph.Start();

        try
        {
            await WaitForFirstFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        SetState(AudioCaptureState.Recording);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != AudioCaptureState.Recording) throw new InvalidOperationException("AUDIO_CAPTURE_NOT_RECORDING");
        _graph?.Stop();
        SetState(AudioCaptureState.Paused);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != AudioCaptureState.Paused) throw new InvalidOperationException("AUDIO_CAPTURE_NOT_PAUSED");
        _graph?.Start();
        SetState(AudioCaptureState.Recording);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _graph?.Stop();
        _frames.Writer.TryComplete();
        await DisposeGraphAsync().ConfigureAwait(false);
        _captureClock.Stop();
        if (State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed)
            SetState(AudioCaptureState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        _catalog.DeviceChanged -= OnCatalogDeviceChanged;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _catalog.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WaitForFirstFrameAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _frameCount) > 0 && Volatile.Read(ref _bytesReceived) > 0) return;
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        RaiseFailure("AUDIO_NO_FRAMES", "AudioGraph started but no non-empty frame arrived within 3 seconds.", true);
        throw new TimeoutException("AUDIO_CAPTURE_START_TIMEOUT");
    }

    private void OnQuantumStarted(AudioGraph _, object __)
    {
        try
        {
            var output = _outputNode;
            if (output is null) return;
            using var frame = output.GetFrame();
            using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            using var reference = buffer.CreateReference();
            unsafe
            {
                ((IMemoryBufferByteAccess)reference).GetBuffer(out var data, out var capacity);
                if (data is null || capacity == 0) return;
                var bytes = new byte[capacity];
                Marshal.Copy((IntPtr)data, bytes, 0, checked((int)capacity));
                var metrics = Measure(bytes);
                var sampleCount = bytes.Length / 2;
                var audioFrame = new AudioFrame(Interlocked.Read(ref _sampleCursor), sampleCount, DateTimeOffset.UtcNow, bytes, metrics.Rms, metrics.Peak, metrics.Clipping, AudioStreamFormats.Phase1Microphone);
                if (!_frames.Writer.TryWrite(audioFrame))
                {
                    RaiseFailure("AUDIO_PIPELINE_OVERRUN", "Audio frame queue is full; stopping capture to avoid silent loss.", false);
                    SetState(AudioCaptureState.Failed);
                    _frames.Writer.TryComplete(new InvalidOperationException("AUDIO_PIPELINE_OVERRUN"));
                    _graph?.Stop();
                    return;
                }

                Interlocked.Add(ref _sampleCursor, sampleCount);
                Interlocked.Increment(ref _frameCount);
                Interlocked.Add(ref _bytesReceived, bytes.Length);
                lock (_gate)
                {
                    _firstFrameLatencyMs ??= _captureClock.ElapsedMilliseconds;
                    _rmsSum += metrics.Rms;
                    _peak = Math.Max(_peak, metrics.Peak);
                    _clipping |= metrics.Clipping;
                    _lastAudioAtUtc = DateTimeOffset.UtcNow;
                    _silenceStartedAtUtc = metrics.Rms < 0.003d ? _silenceStartedAtUtc ?? DateTimeOffset.UtcNow : null;
                }
            }
        }
        catch (Exception ex)
        {
            RaiseFailure(AudioGraphErrorMapper.Map(ex), ex.Message, true);
        }
    }

    private void OnUnrecoverableError(AudioGraph _, object __)
    {
        SetState(AudioCaptureState.Failed);
        RaiseFailure("AUDIO_GRAPH_UNRECOVERABLE", "AudioGraph reported an unrecoverable error.", false);
    }

    private void OnCatalogDeviceChanged(object? _, AudioDeviceChangedEventArgs args)
    {
        if (args.Device is not null && string.Equals(args.Device.Id, SelectedDevice?.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (args.EventType == "DEVICE_REMOVED" || args.Device.RuntimeStatus == "INACTIVE")
            {
                SetState(AudioCaptureState.DeviceLost);
                RaiseFailure("AUDIO_DEVICE_LOST", "Selected microphone was removed or became inactive.", false);
                _frames.Writer.TryComplete(new InvalidOperationException("AUDIO_DEVICE_LOST"));
                _graph?.Stop();
            }
        }
        DeviceStateChanged?.Invoke(this, args);
    }

    private AudioDeviceProbeResult BuildProbeResult(long durationMs, string? errorCode, string? errorDetail)
    {
        var selected = SelectedDevice;
        var telemetry = Telemetry;
        return new AudioDeviceProbeResult(
            selected?.Id,
            selected?.Name,
            selected is not null,
            selected?.State == "Active",
            errorCode != "AUDIO_DEVICE_ACCESS_DENIED",
            true,
            _graph is not null,
            telemetry.FrameCount > 0,
            checked((int)Math.Min(int.MaxValue, telemetry.FrameCount)),
            telemetry.BytesReceived,
            telemetry.FirstFrameLatencyMs,
            telemetry.RmsDb is not null && telemetry.RmsDb > -50,
            telemetry.RmsDb,
            telemetry.PeakDb,
            telemetry.Clipping,
            SampleRate,
            Channels,
            "PCM_S16",
            errorCode,
            errorDetail,
            errorCode is null ? telemetry.FrameCount > 0 ? "READY" : "NO_PACKETS" : "FAILED");
    }

    private async Task DisposeGraphAsync()
    {
        if (_graph is not null)
        {
            _graph.QuantumStarted -= OnQuantumStarted;
            _graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
            _graph.Stop();
        }
        _outputNode?.Dispose();
        _inputNode?.Dispose();
        _graph?.Dispose();
        _outputNode = null;
        _inputNode = null;
        _graph = null;
        await Task.CompletedTask;
    }

    private void ResetTelemetry()
    {
        lock (_gate)
        {
            _sampleCursor = 0;
            _frameCount = 0;
            _bytesReceived = 0;
            _firstFrameLatencyMs = null;
            _rmsSum = 0;
            _peak = 0;
            _clipping = false;
            _lastAudioAtUtc = null;
            _silenceStartedAtUtc = null;
            _telemetry = new AudioTelemetrySnapshot(0, 0, null, null, null, false, null, null);
        }
    }

    private void SetState(AudioCaptureState state)
    {
        lock (_gate) _state = state;
    }

    private void RaiseFailure(string code, string? detail, bool retryable)
    {
        if (Interlocked.Exchange(ref _failureRaised, 1) != 0) return;
        CaptureFailed?.Invoke(this, new AudioCaptureFailureEventArgs(code, detail, retryable, DateTimeOffset.UtcNow));
    }

    private static Channel<AudioFrame> CreateChannel() => Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleWriter = true,
        SingleReader = false
    });

    private static (double Rms, double Peak, bool Clipping) Measure(byte[] bytes)
    {
        var samples = bytes.Length / 2;
        if (samples == 0) return (0, 0, false);
        var sum = 0d;
        var peak = 0d;
        var clipping = false;
        for (var index = 0; index < samples; index++)
        {
            var sample = BitConverter.ToInt16(bytes, index * 2) / 32768d;
            var absolute = Math.Abs(sample);
            sum += sample * sample;
            peak = Math.Max(peak, absolute);
            clipping |= absolute >= 0.999d;
        }
        return (Math.Sqrt(sum / samples), peak, clipping);
    }

    private static double ToDb(double value) => value <= 0 ? -60d : Math.Clamp(20d * Math.Log10(value), -60d, 0d);

    private static long ElapsedMilliseconds(long started)
        => (long)Math.Round((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}

internal static class AudioGraphErrorMapper
{
    public static string Map(Exception exception)
    {
        var message = exception.ToString();
        if (message.Contains("AUDIO_INPUT_NODE_CREATE_FAILED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_DEVICE_CREATE_FAILED", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_INPUT_NODE_CREATE_FAILED";
        if (message.Contains("AUDIOGRAPH_CREATE_FAILED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_GRAPH_CREATE_FAILED", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_GRAPH_CREATE_FAILED";
        if (message.Contains("AUDIO_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)) return "AUDIO_DEVICE_LOST";
        if (message.Contains("AUDIO_STORAGE_WRITE_FAILED", StringComparison.OrdinalIgnoreCase)
            || exception is IOException)
            return "AUDIO_STORAGE_WRITE_FAILED";
        if (message.Contains("access", StringComparison.OrdinalIgnoreCase) || exception is UnauthorizedAccessException)
            return "AUDIO_DEVICE_ACCESS_DENIED";
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase)) return "AUDIO_DEVICE_NOT_FOUND";
        if (message.Contains("inactive", StringComparison.OrdinalIgnoreCase)) return "AUDIO_DEVICE_INACTIVE";
        if (message.Contains("overrun", StringComparison.OrdinalIgnoreCase)) return "AUDIO_PIPELINE_OVERRUN";
        if (message.Contains("AUDIO_CAPTURE_START_TIMEOUT", StringComparison.OrdinalIgnoreCase)
            || message.Contains("frame", StringComparison.OrdinalIgnoreCase)) return "AUDIO_NO_FRAMES";
        return "AUDIO_GRAPH_UNRECOVERABLE";
    }
}

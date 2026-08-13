using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;

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
    private AudioEncodingProperties? _outputEncodingProperties;
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
    private AudioGraphAttemptDiagnostics _attempt = new();
    private volatile bool _probeMode;

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

    public AudioGraphAttemptDiagnostics LastAttemptDiagnostics
    {
        get { lock (_gate) return _attempt.Clone(); }
    }

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
        lock (_gate) _attempt = new AudioGraphAttemptDiagnostics();
        await SelectDeviceAsync(selectionMode, deviceId, cancellationToken).ConfigureAwait(false);
        _probeMode = true;
        var started = Stopwatch.GetTimestamp();
        AudioDeviceProbeResult? result = null;
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : duration, cancellationToken).ConfigureAwait(false);
            result = BuildProbeResult(ElapsedMilliseconds(started), null, null);
        }
        catch (Exception ex)
        {
            var code = ex is OperationCanceledException ? "AUDIO_TEST_CANCELLED" : AudioGraphErrorMapper.Map(ex);
            result = BuildProbeResult(ElapsedMilliseconds(started), code, ex.Message);
        }
        finally
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            _probeMode = false;
            if (result is not null)
                result = result with { AttemptDiagnostics = LastAttemptDiagnostics };
        }
        return result ?? BuildProbeResult(ElapsedMilliseconds(started), "AUDIO_TEST_FAILED", "AudioGraph probe did not produce a result.");
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
        lock (_gate) _attempt = new AudioGraphAttemptDiagnostics();
        _frames = CreateChannel();
        _failureRaised = 0;
        SetState(AudioCaptureState.Starting);

        try
        {
            _encodingProperties = AudioEncodingProperties.CreatePcm(SampleRate, Channels, BitsPerSample);
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                EncodingProperties = _encodingProperties,
                DesiredSamplesPerQuantum = SampleRate / 100
            };
            lock (_gate) _attempt.GraphSamplesPerQuantum = SampleRate / 100;
            lock (_gate) _attempt.GraphCreateAttempted = true;
            var creation = await AudioGraph.CreateAsync(settings);
            lock (_gate)
            {
                _attempt.GraphCreationStatus = creation.Status.ToString();
                _attempt.GraphExtendedErrorHResult = creation.ExtendedError?.HResult;
            }
            if (creation.Status != AudioGraphCreationStatus.Success || creation.Graph is null)
                throw new InvalidOperationException($"AUDIOGRAPH_CREATE_FAILED:{creation.Status}");
            _graph = creation.Graph;
            lock (_gate) _attempt.GraphCreated = true;

            lock (_gate) _attempt.InputNodeCreateAttempted = true;
            var inputResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, _encodingProperties, information);
            lock (_gate)
            {
                _attempt.InputNodeCreationStatus = inputResult.Status.ToString();
                _attempt.InputNodeExtendedErrorHResult = inputResult.ExtendedError?.HResult;
            }
            if (!string.Equals(inputResult.Status.ToString(), "Success", StringComparison.OrdinalIgnoreCase) || inputResult.DeviceInputNode is null)
                throw new InvalidOperationException($"AUDIO_INPUT_NODE_CREATE_FAILED:{inputResult.Status}:{inputResult.ExtendedError?.HResult}");
            _inputNode = inputResult.DeviceInputNode;
            lock (_gate) _attempt.InputNodeCreated = true;
            _outputNode = _graph.CreateFrameOutputNode(_encodingProperties);
            lock (_gate) _attempt.OutputNodeCreated = true;
            CaptureOutputFormat(_outputNode.EncodingProperties);
            _inputNode.AddOutgoingConnection(_outputNode);
            lock (_gate) _attempt.ConnectionCreated = true;
            _graph.QuantumStarted += OnQuantumStarted;
            _graph.UnrecoverableErrorOccurred += OnUnrecoverableError;
            _captureClock.Restart();
            lock (_gate) _attempt.GraphStartCalled = true;
            _graph.Start();
            await WaitForFirstFrameAsync(cancellationToken).ConfigureAwait(false);
            SetState(AudioCaptureState.Recording);
        }
        catch (Exception ex)
        {
            var code = AudioGraphErrorMapper.Map(ex);
            RaiseFailure(code, ex.Message, true);
            SetState(AudioCaptureState.Failed);
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
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
            string? failureCode;
            string? failureDetail;
            lock (_gate)
            {
                failureCode = _attempt.FinalErrorCode;
                failureDetail = _attempt.FinalErrorDetail;
            }
            if (!string.IsNullOrWhiteSpace(failureCode))
                throw new InvalidOperationException($"{failureCode}:{failureDetail}");
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
            lock (_gate)
            {
                _attempt.QuantumStartedCount++;
                _attempt.FirstQuantumLatencyMs ??= _captureClock.ElapsedMilliseconds;
            }
            var output = _outputNode;
            if (output is null) return;
            lock (_gate) _attempt.GetFrameCallCount++;
            using var frame = output.GetFrame();
            using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            using var reference = buffer.CreateReference();
            unsafe
            {
                var validLength = buffer.Length;
                lock (_gate)
                {
                    _attempt.AudioBufferLengthLast = checked((int)Math.Min(validLength, (uint)int.MaxValue));
                    _attempt.AudioBufferCapacityLast = null;
                }
                if (validLength == 0)
                {
                    lock (_gate) _attempt.EmptyFrameCount++;
                    return;
                }

                // AudioBuffer references are WinRT IInspectable objects. A
                // direct CLR cast is invalid under CsWinRT; As<T>() performs
                // the required QueryInterface for IMemoryBufferByteAccess.
                var byteAccess = reference.As<IMemoryBufferByteAccess>();
                byteAccess.GetBuffer(out var data, out var capacity);
                lock (_gate)
                {
                    _attempt.AudioBufferCapacityLast = checked((int)Math.Min(capacity, int.MaxValue));
                }
                var bytesToCopy = Math.Min(validLength, capacity);
                if (data is null || bytesToCopy == 0) return;
                var nativeBytes = new byte[checked((int)bytesToCopy)];
                Marshal.Copy((IntPtr)data, nativeBytes, 0, checked((int)bytesToCopy));
                var normalizedBytes = NormalizeToPcm16(nativeBytes, _outputEncodingProperties);
                lock (_gate)
                {
                    _attempt.NonEmptyFrameCount++;
                    _attempt.NativeFrameBytes += nativeBytes.Length;
                    _attempt.NormalizedFrameBytes += normalizedBytes.Length;
                    _attempt.NormalizationMode ??= ResolveNormalizationMode(_outputEncodingProperties);
                }

                var metrics = Measure(normalizedBytes);
                var sampleCount = normalizedBytes.Length / 2;
                if (!_probeMode)
                {
                    var audioFrame = new AudioFrame(Interlocked.Read(ref _sampleCursor), sampleCount, DateTimeOffset.UtcNow, normalizedBytes, metrics.Rms, metrics.Peak, metrics.Clipping, AudioStreamFormats.Phase1Microphone);
                    if (!_frames.Writer.TryWrite(audioFrame))
                    {
                        RaiseFailure("AUDIO_PIPELINE_OVERRUN", "Audio frame queue is full; stopping capture to avoid silent loss.", false);
                        SetState(AudioCaptureState.Failed);
                        _frames.Writer.TryComplete(new InvalidOperationException("AUDIO_PIPELINE_OVERRUN"));
                        _graph?.Stop();
                        return;
                    }
                }

                Interlocked.Add(ref _sampleCursor, sampleCount);
                Interlocked.Increment(ref _frameCount);
                Interlocked.Add(ref _bytesReceived, normalizedBytes.Length);
                lock (_gate)
                {
                    _attempt.BytesReceived = _bytesReceived;
                    _firstFrameLatencyMs ??= _captureClock.ElapsedMilliseconds;
                    _attempt.FirstFrameLatencyMs ??= _firstFrameLatencyMs;
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
        lock (_gate)
        {
            _attempt.UnrecoverableErrorOccurred = true;
            _attempt.UnrecoverableErrorHResult = __ switch
            {
                Exception exception => exception.HResult,
                int hresult => hresult,
                _ => null
            };
        }
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
        var attempt = LastAttemptDiagnostics;
        attempt.FinalErrorCode ??= errorCode;
        attempt.FinalErrorDetail ??= errorDetail;
        attempt.FinalCaptureState ??= State.ToString();
        return new AudioDeviceProbeResult(
            selected?.Id,
            selected?.Name,
            selected is not null,
            selected?.State == "Active",
            errorCode != "AUDIO_DEVICE_ACCESS_DENIED",
            attempt.NormalizationMode is "FLOAT32_TO_PCM16" or "PCM16_COPY",
            attempt.InputNodeCreated,
            attempt.GraphStartCalled,
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
            errorCode is null ? telemetry.FrameCount > 0 ? "READY" : "NO_PACKETS" : "FAILED",
            attempt);
    }

    private void CaptureOutputFormat(AudioEncodingProperties properties)
    {
        _outputEncodingProperties = properties;
        lock (_gate)
        {
            _attempt.OutputSubtype = properties.Subtype;
            _attempt.OutputBitsPerSample = checked((int)properties.BitsPerSample);
            _attempt.OutputSampleRate = checked((int)properties.SampleRate);
            _attempt.OutputChannelCount = checked((int)properties.ChannelCount);
        }
    }

    private static string ResolveNormalizationMode(AudioEncodingProperties? properties)
    {
        if (properties is null) return "UNKNOWN";
        if (IsFloat(properties) && properties.BitsPerSample == 32) return "FLOAT32_TO_PCM16";
        if (IsPcm(properties) && properties.BitsPerSample == 16) return "PCM16_COPY";
        return "UNSUPPORTED";
    }

    private static byte[] NormalizeToPcm16(byte[] nativeBytes, AudioEncodingProperties? properties)
    {
        if (properties is null)
            throw new InvalidOperationException("AUDIO_FORMAT_UNSUPPORTED:output_properties_missing");
        if (properties.SampleRate != SampleRate || properties.ChannelCount != Channels)
            throw new InvalidOperationException($"AUDIO_FORMAT_UNSUPPORTED:{properties.SampleRate}Hz:{properties.ChannelCount}ch");

        if (IsPcm(properties) && properties.BitsPerSample == BitsPerSample)
        {
            var usableLength = nativeBytes.Length - nativeBytes.Length % 2;
            return usableLength == nativeBytes.Length ? nativeBytes : nativeBytes[..usableLength];
        }

        if (IsFloat(properties) && properties.BitsPerSample == 32)
        {
            var sampleCount = nativeBytes.Length / sizeof(float);
            var normalized = new byte[sampleCount * sizeof(short)];
            for (var index = 0; index < sampleCount; index++)
            {
                var sample = Math.Clamp(BitConverter.ToSingle(nativeBytes, index * sizeof(float)), -1f, 1f);
                var pcm = sample <= -1f
                    ? short.MinValue
                    : (short)Math.Round(sample * short.MaxValue, MidpointRounding.AwayFromZero);
                BitConverter.TryWriteBytes(normalized.AsSpan(index * sizeof(short), sizeof(short)), pcm);
            }
            return normalized;
        }

        throw new InvalidOperationException($"AUDIO_FORMAT_UNSUPPORTED:{properties.Subtype}:{properties.BitsPerSample}");
    }

    private static bool IsFloat(AudioEncodingProperties properties)
        => string.Equals(properties.Subtype, MediaEncodingSubtypes.Float, StringComparison.OrdinalIgnoreCase)
           || properties.Subtype.Contains("Float", StringComparison.OrdinalIgnoreCase);

    private static bool IsPcm(AudioEncodingProperties properties)
        => string.Equals(properties.Subtype, MediaEncodingSubtypes.Pcm, StringComparison.OrdinalIgnoreCase)
           || properties.Subtype.Contains("Pcm", StringComparison.OrdinalIgnoreCase);

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
        lock (_gate)
        {
            _state = state;
            _attempt.FinalCaptureState = state.ToString();
        }
    }

    private void RaiseFailure(string code, string? detail, bool retryable)
    {
        if (Interlocked.Exchange(ref _failureRaised, 1) != 0) return;
        lock (_gate)
        {
            _attempt.FinalErrorCode = code;
            _attempt.FinalErrorDetail = detail;
        }
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
        if (exception is InvalidCastException
            || message.Contains("AUDIO_BUFFER_INTEROP_FAILED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("IMemoryBufferByteAccess", StringComparison.OrdinalIgnoreCase)
            || message.Contains("WinRT.IInspectable", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_BUFFER_INTEROP_FAILED";
        if (exception is COMException comException && unchecked((uint)comException.HResult) == 0x80070005u)
            return "AUDIO_DEVICE_ACCESS_DENIED";
        if (message.Contains("AUDIO_INPUT_NODE_CREATE_FAILED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_DEVICE_CREATE_FAILED", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_INPUT_NODE_CREATE_FAILED";
        if (message.Contains("AUDIOGRAPH_CREATE_FAILED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_GRAPH_CREATE_FAILED", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_GRAPH_CREATE_FAILED";
        if (message.Contains("AUDIO_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)) return "AUDIO_DEVICE_LOST";
        if (message.Contains("AUDIO_FORMAT_UNSUPPORTED", StringComparison.OrdinalIgnoreCase)) return "AUDIO_FORMAT_UNSUPPORTED";
        if (message.Contains("AUDIO_STORAGE_WRITE_FAILED", StringComparison.OrdinalIgnoreCase)
            || exception is IOException)
            return "AUDIO_STORAGE_WRITE_FAILED";
        if (exception is UnauthorizedAccessException)
            return "AUDIO_DEVICE_ACCESS_DENIED";
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase)) return "AUDIO_DEVICE_NOT_FOUND";
        if (message.Contains("inactive", StringComparison.OrdinalIgnoreCase)) return "AUDIO_DEVICE_INACTIVE";
        if (message.Contains("overrun", StringComparison.OrdinalIgnoreCase)) return "AUDIO_PIPELINE_OVERRUN";
        if (message.Contains("AUDIO_CAPTURE_START_TIMEOUT", StringComparison.OrdinalIgnoreCase)
            || message.Contains("frame", StringComparison.OrdinalIgnoreCase)) return "AUDIO_NO_FRAMES";
        return "AUDIO_GRAPH_UNRECOVERABLE";
    }
}

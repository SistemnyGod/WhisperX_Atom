using System.Buffers;
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
public sealed class AudioGraphCaptureEngine : IAudioCaptureEngine, IHostCaptureSource
{
    private const int SampleRate = RecordingContract.MicrophoneSampleRate;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private static readonly TimeSpan LiveTelemetryWindow = TimeSpan.FromMilliseconds(75);
    // 256 quanta provide a bounded ~2.56 second startup/backpressure window
    // while keeping overrun explicit instead of silently dropping frames.
    private const int QueueCapacity = 256;

    private readonly AudioGraphDeviceCatalog _catalog;
    private readonly object _gate = new();
    private readonly Stopwatch _captureClock = new();
    private Channel<AudioFrame> _frames = CreateChannel();
    // The writer binds to this channel before AudioGraph.Start() can emit its
    // first quantum.  Keeping the preparation flag separate avoids a race
    // where the consumer observes the previous completed channel while
    // StartAsync replaces it for the new session.
    private bool _frameChannelPrepared;
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
    private long _consumedSampleCursor;
    private long _frameCount;
    private long _bytesReceived;
    private long? _firstFrameLatencyMs;
    private double _rmsSum;
    private double _peak;
    private bool _clipping;
    private DateTimeOffset? _lastAudioAtUtc;
    private DateTimeOffset? _silenceStartedAtUtc;
    private long _liveSequence;
    private double _liveRmsSum;
    private double _livePeak;
    private bool _liveClipping;
    private int _liveFrameCount;
    private long _framesProduced;
    private long _framesConsumed;
    private int _queueDepth;
    private int _maximumQueueDepth;
    private long _pipelineOverruns;
    private DateTimeOffset _liveWindowStartedAtUtc;
    private LiveAudioTelemetrySnapshot _liveTelemetry = new(0, 0, 0, 0, false, DateTimeOffset.MinValue, true);
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
                long? silence = _silenceStartedAtUtc is null
                    ? null
                    : (long?)(DateTimeOffset.UtcNow - _silenceStartedAtUtc.Value).TotalMilliseconds;
                return new AudioTelemetrySnapshot(
                    _frameCount,
                    _bytesReceived,
                    _firstFrameLatencyMs,
                    rmsDb,
                    peakDb,
                    _clipping,
                    _lastAudioAtUtc,
                    silence,
                    _frameCount == 0,
                    _attempt.NativePeak > 0 ? _attempt.NativePeak : null,
                    _attempt.NativeOverRangeSampleCount,
                    _attempt.NativeSampleCount,
                    _attempt.NormalizedClippedSampleCount,
                    _attempt.NormalizedSampleCount);
            }
        }
    }

    /// <summary>
    /// Capture timeline for the current AudioGraph session. This is derived
    /// from samples accepted by the graph, so it remains stable across the
    /// Desktop's periodic HEALTH requests and pauses without depending on
    /// wall-clock polling in the UI.
    /// </summary>
    public long CurrentMediaTimeMs
        => Math.Max(0, Interlocked.Read(ref _consumedSampleCursor) * 1000L / SampleRate);

    public LiveAudioTelemetrySnapshot LiveTelemetry
    {
        get
        {
            lock (_gate)
            {
                var stale = _liveTelemetry.CapturedAtUtc == DateTimeOffset.MinValue
                    || DateTimeOffset.UtcNow - _liveTelemetry.CapturedAtUtc > TimeSpan.FromMilliseconds(750);
                return _liveTelemetry with { MediaTimeMs = CurrentMediaTimeMs, IsStale = stale };
            }
        }
    }

    public ChannelReader<AudioFrame> Frames => _frames.Reader;

    public ChannelReader<AudioFrame> PrepareFrameChannel()
    {
        if (State is AudioCaptureState.Recording or AudioCaptureState.Paused)
            throw new InvalidOperationException("AUDIO_CAPTURE_ALREADY_RUNNING");
        _frames = CreateChannel();
        _frameChannelPrepared = true;
        return _frames.Reader;
    }

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceStateChanged;

    public event EventHandler<AudioCaptureFailureEventArgs>? CaptureFailed;

    public AudioGraphAttemptDiagnostics LastAttemptDiagnostics
    {
        get
        {
            lock (_gate)
            {
                var snapshot = _attempt.Clone();
                snapshot.FramesProduced = Volatile.Read(ref _framesProduced);
                snapshot.FramesConsumed = Volatile.Read(ref _framesConsumed);
                snapshot.CurrentQueueDepth = Math.Max(0, Volatile.Read(ref _queueDepth));
                snapshot.MaximumQueueDepth = Math.Max(0, Volatile.Read(ref _maximumQueueDepth));
                snapshot.PipelineOverruns = Volatile.Read(ref _pipelineOverruns);
                return snapshot;
            }
        }
    }

    /// <summary>Called by the durable consumer after it accepts a frame.</summary>
    public void MarkFrameConsumed(int sampleCount)
    {
        Interlocked.Increment(ref _framesConsumed);
        if (sampleCount > 0)
            Interlocked.Add(ref _consumedSampleCursor, sampleCount);
        var depth = Interlocked.Decrement(ref _queueDepth);
        if (depth < 0) Interlocked.Exchange(ref _queueDepth, 0);
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
        if (State is AudioCaptureState.Recording or AudioCaptureState.Paused or AudioCaptureState.Starting)
        {
            lock (_gate)
            {
                _attempt = new AudioGraphAttemptDiagnostics
                {
                    FinalCaptureState = State.ToString(),
                    FinalErrorCode = "AUDIO_CAPTURE_BUSY",
                    FinalErrorDetail = "Audio capture is active; stop the current session before probing a device."
                };
            }
            return BuildProbeResult(0, "AUDIO_CAPTURE_BUSY", "Audio capture is active; stop the current session before probing a device.");
        }
        lock (_gate) _attempt = new AudioGraphAttemptDiagnostics();
        var started = Stopwatch.GetTimestamp();
        AudioDeviceProbeResult? result = null;
        var startedHere = false;
        try
        {
            // Keep endpoint resolution inside the probe boundary. A missing
            // fixed endpoint must still produce AudioGraphProbe diagnostics;
            // otherwise SET_AUDIO_DEVICES can only report a bare exception.
            await SelectDeviceAsync(selectionMode, deviceId, cancellationToken).ConfigureAwait(false);
            _probeMode = true;
            await StartAsync(cancellationToken).ConfigureAwait(false);
            startedHere = true;
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
            // A probe may fail before it starts its own graph (for example when
            // a user asks to probe while recording). Never stop a graph owned by
            // the active recording session from this cleanup path.
            if (startedHere)
            {
                try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
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
            // Do not leave the previous endpoint looking effective while a
            // fixed selection failure is being reported. The caller may
            // explicitly restore it after the probe, but the probe itself
            // must describe the failed selection, not stale state.
            lock (_gate) _selectedDevice = null;
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
        if (!_frameChannelPrepared)
            _frames = CreateChannel();
        _frameChannelPrepared = false;
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
            lock (_gate) _attempt.RequestedSamplesPerQuantum = SampleRate / 100;
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
            lock (_gate)
            {
                _attempt.GraphCreated = true;
                _attempt.GraphSamplesPerQuantum = checked((int)_graph.SamplesPerQuantum);
            }

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
        // STOP is only reached through an explicit command, shutdown, or an
        // unrecoverable capture error.  Do not add a wall-clock auto-stop:
        // long meetings are supported by rotating durable raw chunks.
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
                var nativeBuffer = ArrayPool<byte>.Shared.Rent(checked((int)bytesToCopy));
                PooledAudioBuffer? normalizedOwner = null;
                try
                {
                    Marshal.Copy((IntPtr)data, nativeBuffer, 0, checked((int)bytesToCopy));
                    var normalized = NormalizePooledFrame(nativeBuffer, checked((int)bytesToCopy), _outputEncodingProperties, _graph?.SamplesPerQuantum);
                    normalizedOwner = normalized.Buffer;
                    lock (_gate)
                    {
                        _attempt.NonEmptyFrameCount++;
                        _attempt.NativeFrameBytes += bytesToCopy;
                        _attempt.NormalizedFrameBytes += normalized.Length;
                        _attempt.ObservedBytesPerSample = normalized.ObservedBytesPerSample;
                        _attempt.ObservedSampleFormat = normalized.ObservedSampleFormat;
                        _attempt.FormatIntegrityVerified = normalized.FormatIntegrityVerified;
                        _attempt.NonFiniteSampleCount += normalized.NonFiniteSampleCount;
                        _attempt.NativePeak = Math.Max(_attempt.NativePeak, normalized.NativePeak);
                        _attempt.NativeOverRangeSampleCount += normalized.NativeOverRangeSampleCount;
                        _attempt.NativeSampleCount += normalized.NativeSampleCount;
                        _attempt.NormalizedClippedSampleCount += normalized.NormalizedClippedSampleCount;
                        _attempt.NormalizedSampleCount += normalized.NormalizedSampleCount;
                        _attempt.NormalizedPeak = Math.Max(_attempt.NormalizedPeak, normalized.Peak);
                        _attempt.NormalizedSampleSum += normalized.SampleSum;
                        _attempt.NormalizedSquareSum += normalized.SquareSum;
                        _attempt.NormalizedSilenceSampleCount += normalized.SilenceSampleCount;
                        _attempt.NormalizationMode = normalized.NormalizationMode;
                    }

                    var sampleCount = normalized.Length / sizeof(short);
                    if (!_probeMode)
                    {
                        var audioFrame = new AudioFrame(Interlocked.Read(ref _sampleCursor), sampleCount, DateTimeOffset.UtcNow, normalized.Buffer.Buffer, normalized.Rms, normalized.Peak, normalized.Clipping, AudioStreamFormats.Phase1Microphone)
                        {
                            Pcm16Length = normalized.Length,
                            BufferOwner = normalized.Buffer
                        };
                        if (!_frames.Writer.TryWrite(audioFrame))
                        {
                            audioFrame.Dispose();
                            normalizedOwner = null;
                            Interlocked.Increment(ref _pipelineOverruns);
                            lock (_gate) _attempt.PipelineOverruns = _pipelineOverruns;
                            RaiseFailure("AUDIO_PIPELINE_OVERRUN", "Audio frame queue is full; stopping capture to avoid silent loss.", false);
                            SetState(AudioCaptureState.Failed);
                            _frames.Writer.TryComplete(new InvalidOperationException("AUDIO_PIPELINE_OVERRUN"));
                            _graph?.Stop();
                            return;
                        }
                        normalizedOwner = null; // ownership moved to AudioFrame
                        var depth = Interlocked.Increment(ref _queueDepth);
                        Interlocked.Increment(ref _framesProduced);
                        while (depth > Volatile.Read(ref _maximumQueueDepth)
                               && Interlocked.CompareExchange(ref _maximumQueueDepth, depth, Volatile.Read(ref _maximumQueueDepth)) != Volatile.Read(ref _maximumQueueDepth)) { }
                    }
                    else
                    {
                        normalized.Buffer.Dispose();
                        normalizedOwner = null;
                    }

                    Interlocked.Add(ref _sampleCursor, sampleCount);
                    Interlocked.Increment(ref _frameCount);
                    Interlocked.Add(ref _bytesReceived, normalized.Length);
                    PublishLiveTelemetry((normalized.Rms, normalized.Peak, normalized.Clipping));
                lock (_gate)
                {
                    _attempt.BytesReceived = _bytesReceived;
                    _firstFrameLatencyMs ??= _captureClock.ElapsedMilliseconds;
                    _attempt.FirstFrameLatencyMs ??= _firstFrameLatencyMs;
                    _rmsSum += normalized.Rms;
                    _peak = Math.Max(_peak, normalized.Peak);
                    _clipping |= normalized.Clipping;
                    _lastAudioAtUtc = DateTimeOffset.UtcNow;
                    _silenceStartedAtUtc = normalized.Rms < 0.003d ? _silenceStartedAtUtc ?? DateTimeOffset.UtcNow : null;
                }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(nativeBuffer);
                    normalizedOwner?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is AudioBufferFormatMismatchException mismatch && mismatch.NonFiniteSampleCount > 0)
            {
                lock (_gate) _attempt.NonFiniteSampleCount += mismatch.NonFiniteSampleCount;
            }
            var code = AudioGraphErrorMapper.Map(ex);
            RaiseFailure(code, ex.Message, true);
            SetState(AudioCaptureState.Failed);
            _frames.Writer.TryComplete(ex);
            _graph?.Stop();
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

    private sealed record NormalizedFrame(
        byte[] Pcm16,
        int ObservedBytesPerSample,
        string ObservedSampleFormat,
        string NormalizationMode,
        bool FormatIntegrityVerified,
        long NonFiniteSampleCount);

    private sealed record PooledNormalizedFrame(
        PooledAudioBuffer Buffer,
        int Length,
        double Rms,
        double Peak,
        bool Clipping,
        int ObservedBytesPerSample,
        string ObservedSampleFormat,
        string NormalizationMode,
        bool FormatIntegrityVerified,
        long NonFiniteSampleCount,
        double NativePeak,
        long NativeOverRangeSampleCount,
        long NativeSampleCount,
        long NormalizedClippedSampleCount,
        long NormalizedSampleCount,
        double SampleSum,
        double SquareSum,
        long SilenceSampleCount);

    private sealed class AudioBufferFormatMismatchException(string message, long nonFiniteSampleCount = 0)
        : InvalidOperationException(message)
    {
        public long NonFiniteSampleCount { get; } = nonFiniteSampleCount;
    }

    // Kept as a small compatibility seam for diagnostics/tests that exercise
    // normalization without a live graph quantum size.
    private static byte[] NormalizeToPcm16(byte[] nativeBytes, AudioEncodingProperties? properties)
        => NormalizeFrame(nativeBytes, properties, null).Pcm16;

    /// <summary>
    /// Realtime variant used by QuantumStarted. Both source and destination
    /// buffers are pooled, and normalization computes signal metrics in the
    /// same pass so capture never performs a second scan or allocates an
    /// unbounded byte array.
    /// </summary>
    // Compatibility seam: NormalizeFrame(nativeBytes, _outputEncodingProperties, _graph?.SamplesPerQuantum)
    private static PooledNormalizedFrame NormalizePooledFrame(
        byte[] nativeBytes,
        int length,
        AudioEncodingProperties? properties,
        int? graphSamplesPerQuantum)
    {
        if (properties is null)
            throw new InvalidOperationException("AUDIO_FORMAT_UNSUPPORTED:output_properties_missing");
        if (properties.SampleRate != SampleRate || properties.ChannelCount != Channels)
            throw new InvalidOperationException($"AUDIO_FORMAT_UNSUPPORTED:{properties.SampleRate}Hz:{properties.ChannelCount}ch");

        var expectedFloatLength = graphSamplesPerQuantum is int quantum
            ? checked((long)quantum * Channels * sizeof(float))
            : -1;
        var expectedPcmLength = graphSamplesPerQuantum is int pcmQuantum
            ? checked((long)pcmQuantum * Channels * sizeof(short))
            : -1;
        var observedFloat = length == expectedFloatLength
            || (expectedFloatLength < 0 && length % (Channels * sizeof(float)) == 0);
        var observedPcm = length == expectedPcmLength
            || (expectedPcmLength < 0 && length % (Channels * sizeof(short)) == 0);

        if (observedFloat && length % sizeof(float) == 0)
        {
            var samples = length / sizeof(float);
            var output = ArrayPool<byte>.Shared.Rent(checked(samples * sizeof(short)));
            var owner = new PooledAudioBuffer(output, samples * sizeof(short));
            var sum = 0d;
            var peak = 0d;
            var nativePeak = 0d;
            long overRange = 0;
            long normalizedClipped = 0;
            var sampleSum = 0d;
            var squareSum = 0d;
            long silenceSamples = 0;
            var clipping = false;
            long nonFinite = 0;
            try
            {
                for (var index = 0; index < samples; index++)
                {
                    var sample = BitConverter.ToSingle(nativeBytes, index * sizeof(float));
                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                    {
                        nonFinite++;
                        continue;
                    }
                    var nativeAbsolute = Math.Abs((double)sample);
                    nativePeak = Math.Max(nativePeak, nativeAbsolute);
                    if (nativeAbsolute > 1d) overRange++;
                    sample = Math.Clamp(sample, -1f, 1f);
                    var absolute = Math.Abs((double)sample);
                    sampleSum += sample;
                    squareSum += sample * sample;
                    if (absolute < 0.003d) silenceSamples++;
                    sum += absolute * absolute;
                    peak = Math.Max(peak, absolute);
                    if (absolute >= 0.999d) normalizedClipped++;
                    clipping |= absolute >= 0.999d;
                    var pcm = sample <= -1f
                        ? short.MinValue
                        : (short)Math.Round(sample * short.MaxValue, MidpointRounding.AwayFromZero);
                    BitConverter.TryWriteBytes(output.AsSpan(index * sizeof(short), sizeof(short)), pcm);
                }
                if (nonFinite > 0)
                    throw new AudioBufferFormatMismatchException($"AUDIO_BUFFER_FORMAT_MISMATCH:non_finite_float_samples={nonFinite}", nonFinite);
                var integrity = expectedFloatLength < 0 || length == expectedFloatLength;
                return new PooledNormalizedFrame(owner, samples * sizeof(short), Math.Sqrt(sum / Math.Max(1, samples)), peak, clipping, sizeof(float), "FLOAT32", "FLOAT32_TO_PCM16", integrity, 0, nativePeak, overRange, samples, normalizedClipped, samples, sampleSum, squareSum, silenceSamples);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        if (observedPcm && length % sizeof(short) == 0)
        {
            var usableLength = length - length % sizeof(short);
            var output = ArrayPool<byte>.Shared.Rent(Math.Max(sizeof(short), usableLength));
            var owner = new PooledAudioBuffer(output, usableLength);
            Buffer.BlockCopy(nativeBytes, 0, output, 0, usableLength);
            var samples = usableLength / sizeof(short);
            var sum = 0d;
            var peak = 0d;
            long normalizedClipped = 0;
            var sampleSum = 0d;
            var squareSum = 0d;
            long silenceSamples = 0;
            var clipping = false;
            for (var index = 0; index < samples; index++)
            {
                var sample = BitConverter.ToInt16(output, index * sizeof(short)) / 32768d;
                var absolute = Math.Abs(sample);
                sampleSum += sample;
                squareSum += sample * sample;
                if (absolute < 0.003d) silenceSamples++;
                sum += sample * sample;
                peak = Math.Max(peak, absolute);
                if (absolute >= 0.999d) normalizedClipped++;
                clipping |= absolute >= 0.999d;
            }
            var integrity = expectedPcmLength < 0 || length == expectedPcmLength;
            return new PooledNormalizedFrame(owner, usableLength, Math.Sqrt(sum / Math.Max(1, samples)), peak, clipping, sizeof(short), "PCM16", "PCM16_COPY", integrity, 0, peak, 0, samples, normalizedClipped, samples, sampleSum, squareSum, silenceSamples);
        }

        throw new InvalidOperationException($"AUDIO_BUFFER_FORMAT_MISMATCH:{properties.Subtype}:{properties.BitsPerSample}:bytes={length}:expectedFloat={expectedFloatLength}:expectedPcm={expectedPcmLength}");
    }

    private static NormalizedFrame NormalizeFrame(
        byte[] nativeBytes,
        AudioEncodingProperties? properties,
        int? graphSamplesPerQuantum)
    {
        if (properties is null)
            throw new InvalidOperationException("AUDIO_FORMAT_UNSUPPORTED:output_properties_missing");
        if (properties.SampleRate != SampleRate || properties.ChannelCount != Channels)
            throw new InvalidOperationException($"AUDIO_FORMAT_UNSUPPORTED:{properties.SampleRate}Hz:{properties.ChannelCount}ch");

        var expectedFloatLength = graphSamplesPerQuantum is int quantum
            ? checked((long)quantum * Channels * sizeof(float))
            : -1;
        var expectedPcmLength = graphSamplesPerQuantum is int pcmQuantum
            ? checked((long)pcmQuantum * Channels * sizeof(short))
            : -1;

        // AudioGraph frame buffers are commonly exposed as IEEE Float32 even
        // when EncodingProperties reports PCM. Prefer the observed quantum
        // byte length over the metadata subtype; trusting Subtype here turns
        // every pair of float bytes into a random-looking PCM16 sample.
        var observedFloat = nativeBytes.Length == expectedFloatLength
            || (expectedFloatLength < 0 && nativeBytes.Length % (Channels * sizeof(float)) == 0);
        var observedPcm = nativeBytes.Length == expectedPcmLength
            || (expectedPcmLength < 0 && nativeBytes.Length % (Channels * sizeof(short)) == 0);

        if (observedFloat && nativeBytes.Length % sizeof(float) == 0)
        {
            var sampleCount = nativeBytes.Length / sizeof(float);
            var normalized = new byte[sampleCount * sizeof(short)];
            long nonFinite = 0;
            for (var index = 0; index < sampleCount; index++)
            {
                var sample = BitConverter.ToSingle(nativeBytes, index * sizeof(float));
                if (float.IsNaN(sample) || float.IsInfinity(sample))
                {
                    nonFinite++;
                    continue;
                }
                sample = Math.Clamp(sample, -1f, 1f);
                var pcm = sample <= -1f
                    ? short.MinValue
                    : (short)Math.Round(sample * short.MaxValue, MidpointRounding.AwayFromZero);
                BitConverter.TryWriteBytes(normalized.AsSpan(index * sizeof(short), sizeof(short)), pcm);
            }
            if (nonFinite > 0)
                throw new AudioBufferFormatMismatchException(
                    $"AUDIO_BUFFER_FORMAT_MISMATCH:non_finite_float_samples={nonFinite}", nonFinite);
            var integrity = expectedFloatLength < 0 || nativeBytes.Length == expectedFloatLength;
            return new NormalizedFrame(normalized, sizeof(float), "FLOAT32", "FLOAT32_TO_PCM16", integrity, nonFinite);
        }

        if (observedPcm && nativeBytes.Length % sizeof(short) == 0)
        {
            var integrity = expectedPcmLength < 0 || nativeBytes.Length == expectedPcmLength;
            var usableLength = nativeBytes.Length - nativeBytes.Length % sizeof(short);
            return new NormalizedFrame(
                usableLength == nativeBytes.Length ? nativeBytes : nativeBytes[..usableLength],
                sizeof(short),
                "PCM16",
                "PCM16_COPY",
                integrity,
                0);
        }

        throw new InvalidOperationException($"AUDIO_BUFFER_FORMAT_MISMATCH:{properties.Subtype}:{properties.BitsPerSample}:bytes={nativeBytes.Length}:expectedFloat={expectedFloatLength}:expectedPcm={expectedPcmLength}");
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
            _consumedSampleCursor = 0;
            _frameCount = 0;
            _bytesReceived = 0;
            _firstFrameLatencyMs = null;
            _rmsSum = 0;
            _peak = 0;
            _clipping = false;
            _lastAudioAtUtc = null;
            _silenceStartedAtUtc = null;
            _telemetry = new AudioTelemetrySnapshot(0, 0, null, null, null, false, null, null);
            _liveSequence = 0;
            _liveRmsSum = 0;
            _livePeak = 0;
            _liveClipping = false;
            _liveFrameCount = 0;
            _framesProduced = 0;
            _framesConsumed = 0;
            _queueDepth = 0;
            _maximumQueueDepth = 0;
            _pipelineOverruns = 0;
            _liveWindowStartedAtUtc = DateTimeOffset.UtcNow;
            _liveTelemetry = new LiveAudioTelemetrySnapshot(0, 0, 0, 0, false, DateTimeOffset.MinValue, true);
        }
    }

    private void PublishLiveTelemetry((double Rms, double Peak, bool Clipping) metrics)
    {
        LiveAudioTelemetrySnapshot? snapshot = null;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_liveFrameCount == 0) _liveWindowStartedAtUtc = now;
            _liveFrameCount++;
            _liveRmsSum += metrics.Rms;
            _livePeak = Math.Max(_livePeak, metrics.Peak);
            _liveClipping |= metrics.Clipping;
            if (now - _liveWindowStartedAtUtc < LiveTelemetryWindow) return;

            var rms = _liveFrameCount == 0 ? 0 : _liveRmsSum / _liveFrameCount;
            snapshot = new LiveAudioTelemetrySnapshot(
                ++_liveSequence,
                CurrentMediaTimeMs,
                Math.Clamp(rms, 0d, 1d),
                Math.Clamp(_livePeak, 0d, 1d),
                _liveClipping,
                now,
                false);
            _liveRmsSum = 0;
            _livePeak = 0;
            _liveClipping = false;
            _liveFrameCount = 0;
            _liveWindowStartedAtUtc = now;
            _liveTelemetry = snapshot;
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
            _attempt.FormatMismatch |= string.Equals(code, "AUDIO_BUFFER_FORMAT_MISMATCH", StringComparison.OrdinalIgnoreCase);
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
        if (message.Contains("AUDIO_CAPTURE_BUSY", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_DEVICE_SELECTION_LOCKED", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_CAPTURE_BUSY";
        if (message.Contains("RECORDER_RUNTIME_LEASE_HELD", StringComparison.OrdinalIgnoreCase))
            return "RECORDER_RUNTIME_LEASE_HELD";
        if (exception is InvalidCastException
            || message.Contains("AUDIO_BUFFER_INTEROP_FAILED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("IMemoryBufferByteAccess", StringComparison.OrdinalIgnoreCase)
            || message.Contains("WinRT.IInspectable", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_BUFFER_INTEROP_FAILED";
        if (message.Contains("AUDIO_BUFFER_FORMAT_MISMATCH", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_BUFFER_FORMAT_MISMATCH";
        if (message.Contains("AUDIO_FRAME_GAP", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_FRAME_GAP";
        if (message.Contains("AUDIO_FRAME_OVERLAP", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_FRAME_OVERLAP";
        if (message.Contains("AUDIO_FRAME_FORMAT_MISMATCH", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_FRAME_FORMAT_INVALID", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_FRAME_FORMAT_MISMATCH";
        if (message.Contains("AUDIO_FRAME_SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_FRAME_RANGE_OVERFLOW", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_FRAME_INVALID", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_FRAME_SIZE_MISMATCH";
        if (exception is COMException comException && unchecked((uint)comException.HResult) == 0x80070005u)
            return "AUDIO_DEVICE_ACCESS_DENIED";
        if (message.Contains("AUDIO_INPUT_NODE_CREATE_FAILED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AUDIO_DEVICE_CREATE_FAILED", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_INPUT_NODE_CREATE_FAILED";
        if (message.Contains("AUDIO_DEVICE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_DEVICE_UNAVAILABLE";
        if (message.Contains("AUDIO_DEFAULT_ENDPOINT_MISSING", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_DEVICE_NOT_FOUND";
        if (message.Contains("AUDIO_DEVICE_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase))
            return "AUDIO_DEVICE_ACCESS_DENIED";
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

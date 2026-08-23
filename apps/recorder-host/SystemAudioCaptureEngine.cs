using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Independent render-loopback source for the AudioGraph Host.
///
/// This is deliberately a second capture source, not a mixer: render frames
/// are normalized to the same 48 kHz mono PCM16 contract as the microphone and
/// are written under a separate <c>system-audio</c> track. The original render
/// endpoint is kept in the track metadata so media assembly can choose whether
/// to keep, align, or mix the tracks later.
/// </summary>
public sealed class SystemAudioCaptureEngine : IAudioCaptureEngine, IHostCaptureSource
{
    private const int SampleRate = RecordingContract.MicrophoneSampleRate;
    private const int QueueCapacity = 256;
    private readonly SystemAudioDeviceCatalog _catalog;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private Channel<AudioFrame> _frames = CreateChannel();
    private bool _frameChannelPrepared;
    private WasapiLoopbackCapture? _capture;
    private AudioDeviceDescriptor? _selected;
    private AudioSelectionMode _selectionMode = AudioSelectionMode.Default;
    private string? _requestedDeviceId;
    private AudioCaptureState _state = AudioCaptureState.Unknown;
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
    private int _failureRaised;
    private long _framesProduced;
    private long _framesConsumed;
    private int _queueDepth;
    private int _maximumQueueDepth;
    private long _pipelineOverruns;
    private AudioGraphAttemptDiagnostics _attempt = new();

    public SystemAudioCaptureEngine(SystemAudioDeviceCatalog catalog)
    {
        _catalog = catalog;
        _catalog.DeviceChanged += OnDeviceChanged;
    }

    public AudioEngineKind Kind => AudioEngineKind.LegacyWasapi;
    public IAudioDeviceCatalog DeviceCatalog => _catalog;
    public AudioDeviceDescriptor? SelectedDevice { get { lock (_gate) return _selected; } }
    public AudioCaptureState State { get { lock (_gate) return _state; } }
    public ChannelReader<AudioFrame> Frames => _frames.Reader;

    public AudioTelemetrySnapshot Telemetry
    {
        get
        {
            lock (_gate)
            {
                var rms = _frameCount == 0 ? null : (double?)(_rmsSum / _frameCount);
                var silence = _silenceStartedAtUtc is null || _lastAudioAtUtc > _silenceStartedAtUtc
                    ? null
                    : (long?)(DateTimeOffset.UtcNow - _silenceStartedAtUtc.Value).TotalMilliseconds;
                return new AudioTelemetrySnapshot(
                    _frameCount,
                    _bytesReceived,
                    _firstFrameLatencyMs,
                    rms is null ? null : ToDb(rms.Value),
                    _peak <= 0 ? null : ToDb(_peak),
                    _clipping,
                    _lastAudioAtUtc,
                    silence,
                    _frameCount == 0);
            }
        }
    }

    public long CurrentMediaTimeMs
        => Math.Max(0, Interlocked.Read(ref _consumedSampleCursor) * 1000L / SampleRate);

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

    public ChannelReader<AudioFrame> PrepareFrameChannel()
    {
        if (State is AudioCaptureState.Recording or AudioCaptureState.Paused)
            throw new InvalidOperationException("AUDIO_CAPTURE_ALREADY_RUNNING");
        _frames = CreateChannel();
        _frameChannelPrepared = true;
        return _frames.Reader;
    }

    public void MarkFrameConsumed(int sampleCount)
    {
        Interlocked.Increment(ref _framesConsumed);
        if (sampleCount > 0) Interlocked.Add(ref _consumedSampleCursor, sampleCount);
        var depth = Interlocked.Decrement(ref _queueDepth);
        if (depth < 0) Interlocked.Exchange(ref _queueDepth, 0);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _catalog.InitializeAsync(cancellationToken).ConfigureAwait(false);
        SetState(_catalog.Resolve(AudioSelectionMode.Default, null) is null
            ? AudioCaptureState.DeviceLost
            : AudioCaptureState.Discovered);
    }

    public async Task SelectDeviceAsync(AudioSelectionMode selectionMode, string? deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State is AudioCaptureState.Recording or AudioCaptureState.Paused or AudioCaptureState.Starting)
            throw new InvalidOperationException("AUDIO_DEVICE_SELECTION_LOCKED");
        var selected = _catalog.Resolve(selectionMode, deviceId);
        if (selected is null || !string.Equals(selected.State, "Active", StringComparison.OrdinalIgnoreCase))
        {
            SetState(AudioCaptureState.DeviceLost);
            throw new InvalidOperationException(selectionMode == AudioSelectionMode.Fixed
                ? "AUDIO_SYSTEM_AUDIO_UNAVAILABLE"
                : "AUDIO_SYSTEM_AUDIO_DEFAULT_ENDPOINT_MISSING");
        }
        lock (_gate)
        {
            _selectionMode = selectionMode;
            _requestedDeviceId = deviceId;
            _selected = selected with { SelectionMode = selectionMode, IsSelected = true };
        }
        SetState(AudioCaptureState.Selected);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State is AudioCaptureState.Recording or AudioCaptureState.Paused)
            throw new InvalidOperationException("AUDIO_CAPTURE_ALREADY_RUNNING");
        if (_selected is null)
            await SelectDeviceAsync(_selectionMode, _requestedDeviceId, cancellationToken).ConfigureAwait(false);
        var selected = SelectedDevice ?? throw new InvalidOperationException("AUDIO_SYSTEM_AUDIO_UNAVAILABLE");
        var device = _catalog.GetDevice(selected.Id) ?? throw new InvalidOperationException("AUDIO_SYSTEM_AUDIO_UNAVAILABLE");

        // The recorder binds the writer to PrepareFrameChannel() immediately before
        // starting the source. Do not call StopAsync here: it completes that freshly
        // prepared channel and the first loopback frames would be dropped. Dispose
        // only a stale capture instance and rotate the channel when one was not
        // prepared by the session writer.
        var staleCapture = Interlocked.Exchange(ref _capture, null);
        if (staleCapture is not null)
        {
            staleCapture.DataAvailable -= OnDataAvailable;
            staleCapture.RecordingStopped -= OnRecordingStopped;
            try { staleCapture.StopRecording(); } catch { }
            staleCapture.Dispose();
        }
        _clock.Stop();
        if (!_frameChannelPrepared)
        {
            _frames.Writer.TryComplete();
            _frames = CreateChannel();
        }
        ResetTelemetry();
        _frameChannelPrepared = false;
        _failureRaised = 0;
        lock (_gate) _attempt = new AudioGraphAttemptDiagnostics { NormalizationMode = "WASAPI_LOOPBACK_TO_PCM16_MONO" };
        SetState(AudioCaptureState.Starting);
        try
        {
            _capture = new WasapiLoopbackCapture(device);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _clock.Restart();
            _capture.StartRecording();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref _frameCount) == 0)
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _frameCount) == 0)
            {
                RaiseFailure("AUDIO_SYSTEM_AUDIO_NO_FRAMES", "Render loopback started but returned no frames.", true);
                throw new TimeoutException("AUDIO_SYSTEM_AUDIO_START_TIMEOUT");
            }
            SetState(AudioCaptureState.Recording);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != AudioCaptureState.Recording) throw new InvalidOperationException("AUDIO_CAPTURE_NOT_RECORDING");
        _capture?.StopRecording();
        SetState(AudioCaptureState.Paused);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != AudioCaptureState.Paused) throw new InvalidOperationException("AUDIO_CAPTURE_NOT_PAUSED");
        _capture?.StartRecording();
        SetState(AudioCaptureState.Recording);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        _frames.Writer.TryComplete();
        _clock.Stop();
        if (State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed)
            SetState(AudioCaptureState.Stopped);
        return Task.CompletedTask;
    }

    public async Task<AudioDeviceProbeResult> ProbeAsync(AudioSelectionMode selectionMode, string? deviceId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await SelectDeviceAsync(selectionMode, deviceId, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : duration, cancellationToken).ConfigureAwait(false);
            var telemetry = Telemetry;
            return BuildProbeResult(ElapsedMilliseconds(started), null, null, telemetry);
        }
        catch (Exception ex)
        {
            return BuildProbeResult(ElapsedMilliseconds(started), MapError(ex), ex.Message, Telemetry);
        }
        finally { try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        _catalog.DeviceChanged -= OnDeviceChanged;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _catalog.DisposeAsync().ConfigureAwait(false);
    }

    private void OnDeviceChanged(object? _, AudioDeviceChangedEventArgs args)
    {
        if (args.Device is not null && string.Equals(args.Device.Id, SelectedDevice?.Id, StringComparison.OrdinalIgnoreCase)
            && (args.EventType == "DEVICE_REMOVED" || args.Device.RuntimeStatus == "DEVICE_LOST")
            && State is AudioCaptureState.Starting or AudioCaptureState.Recording or AudioCaptureState.Paused)
        {
            // A selected render endpoint disappearing must not silently turn an
            // ONLINE recording into an incomplete mic-only session.  Raise the
            // normal exactly-once failure path; RecorderHostRuntime closes the
            // durable chunks and persists a stable stop reason.
            SetState(AudioCaptureState.DeviceLost);
            try { _capture?.StopRecording(); } catch { }
            RaiseFailure("AUDIO_SYSTEM_AUDIO_DEVICE_LOST", "The selected system-audio endpoint was removed.", true);
        }
        DeviceStateChanged?.Invoke(this, args);
    }

    private void OnDataAvailable(object? _, WaveInEventArgs args)
    {
        try
        {
            var format = _capture?.WaveFormat ?? throw new InvalidOperationException("AUDIO_SYSTEM_AUDIO_FORMAT_MISSING");
            var bytes = SystemAudioSampleNormalizer.ToPcm16Mono48k(args.Buffer, args.BytesRecorded, format);
            if (bytes.Length == 0) return;
            var metrics = Measure(bytes);
            var frame = new AudioFrame(Interlocked.Read(ref _sampleCursor), bytes.Length / 2, DateTimeOffset.UtcNow, bytes, metrics.Rms, metrics.Peak, metrics.Clipping, AudioStreamFormats.Phase1Microphone);
            if (!_frames.Writer.TryWrite(frame))
            {
                Interlocked.Increment(ref _pipelineOverruns);
                RaiseFailure("AUDIO_PIPELINE_OVERRUN", "System audio frame queue is full.", false);
                _capture?.StopRecording();
                return;
            }
            Interlocked.Add(ref _sampleCursor, frame.SampleCount);
            Interlocked.Increment(ref _framesProduced);
            Interlocked.Increment(ref _frameCount);
            Interlocked.Add(ref _bytesReceived, bytes.Length);
            var depth = Interlocked.Increment(ref _queueDepth);
            while (true)
            {
                var max = Volatile.Read(ref _maximumQueueDepth);
                if (depth <= max || Interlocked.CompareExchange(ref _maximumQueueDepth, depth, max) == max) break;
            }
            lock (_gate)
            {
                _firstFrameLatencyMs ??= _clock.ElapsedMilliseconds;
                _rmsSum += metrics.Rms;
                _peak = Math.Max(_peak, metrics.Peak);
                _clipping |= metrics.Clipping;
                _lastAudioAtUtc = DateTimeOffset.UtcNow;
                _silenceStartedAtUtc = metrics.Rms < 0.003d ? _silenceStartedAtUtc ?? DateTimeOffset.UtcNow : null;
            }
        }
        catch (Exception ex) { RaiseFailure(MapError(ex), ex.Message, true); }
    }

    private void OnRecordingStopped(object? _, StoppedEventArgs args)
    {
        if (args.Exception is null) return;
        var detail = args.Exception.ToString();
        // WASAPI reports physical render-endpoint removal through the stopped
        // callback on some drivers instead of the endpoint catalog. Preserve
        // the stable device-loss contract so ONLINE never degrades silently
        // to microphone-only capture.
        if (detail.Contains("AUDCLNT_E_DEVICE_INVALIDATED", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("device invalid", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("device removed", StringComparison.OrdinalIgnoreCase))
        {
            SetState(AudioCaptureState.DeviceLost);
            RaiseFailure("AUDIO_SYSTEM_AUDIO_DEVICE_LOST", args.Exception.Message, true);
            return;
        }
        RaiseFailure(MapError(args.Exception), args.Exception.Message, true);
    }

    private AudioDeviceProbeResult BuildProbeResult(long durationMs, string? error, string? detail, AudioTelemetrySnapshot telemetry)
        => new(SelectedDevice?.Id, SelectedDevice?.Name, SelectedDevice is not null, SelectedDevice?.State == "Active", error != "AUDIO_DEVICE_ACCESS_DENIED", true, _capture is not null, telemetry.FrameCount > 0, checked((int)Math.Min(int.MaxValue, telemetry.FrameCount)), telemetry.BytesReceived, telemetry.FirstFrameLatencyMs, telemetry.RmsDb is not null && telemetry.RmsDb > -60, telemetry.RmsDb, telemetry.PeakDb, telemetry.Clipping, SampleRate, 1, "PCM_S16", error, detail, error is null && telemetry.FrameCount > 0 ? "READY" : "FAILED");

    private void ResetTelemetry()
    {
        lock (_gate)
        {
            _sampleCursor = 0; _consumedSampleCursor = 0; _frameCount = 0; _bytesReceived = 0; _firstFrameLatencyMs = null; _rmsSum = 0; _peak = 0; _clipping = false; _lastAudioAtUtc = null; _silenceStartedAtUtc = null;
        }
        Interlocked.Exchange(ref _framesProduced, 0); Interlocked.Exchange(ref _framesConsumed, 0); Interlocked.Exchange(ref _queueDepth, 0); Interlocked.Exchange(ref _maximumQueueDepth, 0); Interlocked.Exchange(ref _pipelineOverruns, 0);
    }

    private void SetState(AudioCaptureState state) { lock (_gate) _state = state; }
    private void RaiseFailure(string code, string? detail, bool retryable) { if (Interlocked.Exchange(ref _failureRaised, 1) == 0) CaptureFailed?.Invoke(this, new AudioCaptureFailureEventArgs(code, detail, retryable, DateTimeOffset.UtcNow)); }
    private static Channel<AudioFrame> CreateChannel() => Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false, AllowSynchronousContinuations = false });
    private static (double Rms, double Peak, bool Clipping) Measure(byte[] bytes)
    {
        var count = bytes.Length / 2; var sum = 0d; var peak = 0d; var clipping = false;
        for (var i = 0; i < count; i++) { var value = BitConverter.ToInt16(bytes, i * 2) / 32768d; var absolute = Math.Abs(value); sum += value * value; peak = Math.Max(peak, absolute); clipping |= absolute >= .999d; }
        return count == 0 ? (0, 0, false) : (Math.Sqrt(sum / count), peak, clipping);
    }
    private static double ToDb(double value) => value <= 0 ? -60 : Math.Clamp(20 * Math.Log10(value), -60, 0);
    private static long ElapsedMilliseconds(long start) => (long)Math.Round((Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency);
    private static string MapError(Exception exception) => exception is UnauthorizedAccessException || exception.ToString().Contains("access", StringComparison.OrdinalIgnoreCase) ? "AUDIO_SYSTEM_AUDIO_ACCESS_DENIED" : exception.ToString().Contains("frame", StringComparison.OrdinalIgnoreCase) ? "AUDIO_SYSTEM_AUDIO_NO_FRAMES" : "AUDIO_SYSTEM_AUDIO_CAPTURE_FAILED";
}

public sealed class SystemAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private Dictionary<string, AudioDeviceDescriptor> _devices = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<AudioDeviceDescriptor> Devices { get { lock (_gate) return _devices.Values.OrderBy(x => x.Name).ToArray(); } }
    public bool IsReady { get; private set; }
    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => ReconcileAsync(cancellationToken);
    public Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var defaultId = GetDefaultId();
        var next = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToDictionary(device => device.ID, device => new AudioDeviceDescriptor(device.ID, device.FriendlyName, AudioDataFlow.Render, "Active", string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase), AudioSelectionMode.Default, false, DateTimeOffset.UtcNow), StringComparer.OrdinalIgnoreCase);
        List<AudioDeviceChangedEventArgs> changes = [];
        lock (_gate)
        {
            foreach (var item in next) if (!_devices.TryGetValue(item.Key, out var old) || old != item.Value) changes.Add(new AudioDeviceChangedEventArgs(old is null ? "DEVICE_ADDED" : "DEVICE_STATE_CHANGED", item.Value, DateTimeOffset.UtcNow));
            foreach (var removed in _devices.Keys.Except(next.Keys, StringComparer.OrdinalIgnoreCase).ToArray()) changes.Add(new AudioDeviceChangedEventArgs("DEVICE_REMOVED", _devices[removed] with { RuntimeStatus = "DEVICE_LOST" }, DateTimeOffset.UtcNow));
            _devices = next; IsReady = true;
        }
        foreach (var change in changes) DeviceChanged?.Invoke(this, change);
        return Task.CompletedTask;
    }
    public AudioDeviceDescriptor? Resolve(AudioSelectionMode mode, string? deviceId)
    {
        lock (_gate)
        {
            if (mode == AudioSelectionMode.Fixed) return string.IsNullOrWhiteSpace(deviceId) ? null : _devices.GetValueOrDefault(deviceId);
            var defaultId = GetDefaultId();
            return defaultId is not null && _devices.TryGetValue(defaultId, out var selected) ? selected : _devices.Values.FirstOrDefault(x => x.IsDefault);
        }
    }
    public MMDevice? GetDevice(string id) { try { return _enumerator.GetDevice(id); } catch { return null; } }
    public ValueTask DisposeAsync() { _enumerator.Dispose(); lock (_gate) _devices = []; return ValueTask.CompletedTask; }
    private string? GetDefaultId() { try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { return null; } }
}

internal static class SystemAudioSampleNormalizer
{
    private const int SampleRate = RecordingContract.MicrophoneSampleRate;

    public static byte[] ToPcm16Mono48k(byte[] source, int length, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        // Do not infer float from a 32-bit Extensible container. WASAPI may
        // expose either IEEE Float32 or PCM32 under the same container; the
        // subtype is the source of truth and is also persisted in track
        // metadata for later diagnostics.
        var resolved = AudioSampleFormatResolver.Resolve(format);
        var bits = resolved.BitsPerSample;
        var sourceFrames = length / Math.Max(1, bits / 8 * channels);
        if (sourceFrames <= 0) return [];
        var sourceRate = Math.Max(1, format.SampleRate);
        var targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (double)SampleRate / sourceRate));
        var output = new byte[targetFrames * 2];
        var float32 = resolved.Kind == RawAudioSampleFormat.Float32;
        for (var target = 0; target < targetFrames; target++)
        {
            var sourceIndex = Math.Min(sourceFrames - 1, (int)Math.Floor(target * sourceRate / (double)SampleRate));
            var sum = 0d;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = sourceIndex * (bits / 8 * channels) + channel * (bits / 8);
                sum += float32 ? BitConverter.ToSingle(source, offset) : bits switch
                {
                    16 => BitConverter.ToInt16(source, offset) / 32768d,
                    24 => ((source[offset + 2] << 24) | (source[offset + 1] << 16) | (source[offset] << 8)) / 2147483648d,
                    _ => BitConverter.ToInt32(source, offset) / 2147483648d
                };
            }
            var value = (short)Math.Clamp((int)Math.Round(sum / channels * 32767d), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(target * 2, 2), value);
        }
        return output;
    }
}

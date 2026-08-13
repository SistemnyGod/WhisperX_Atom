using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Compatibility engine for the existing Windows Service path. The service
/// keeps using this NAudio implementation until the AudioGraph Host passes
/// all runtime gates; it does not know anything about meetings or delivery.
/// </summary>
public sealed class LegacyWasapiCaptureEngine : IAudioCaptureEngine
{
    private readonly LegacyWasapiDeviceCatalog _catalog;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private Channel<AudioFrame> _frames = CreateChannel();
    private WasapiCapture? _capture;
    private AudioDeviceDescriptor? _selected;
    private AudioSelectionMode _selectionMode = AudioSelectionMode.Default;
    private long _sampleCursor;
    private long _frameCount;
    private long _bytes;
    private long? _firstFrameLatencyMs;
    private double _rmsSum;
    private double _peak;
    private bool _clipping;
    private DateTimeOffset? _lastAudioAtUtc;
    private DateTimeOffset? _silenceStartedAtUtc;
    private AudioCaptureState _state = AudioCaptureState.Unknown;
    private int _failureRaised;

    public LegacyWasapiCaptureEngine(LegacyWasapiDeviceCatalog catalog)
    {
        _catalog = catalog;
        _catalog.DeviceChanged += OnDeviceChanged;
    }

    public AudioEngineKind Kind => AudioEngineKind.LegacyWasapi;
    public IAudioDeviceCatalog DeviceCatalog => _catalog;
    public AudioDeviceDescriptor? SelectedDevice { get { lock (_gate) return _selected; } }
    public AudioCaptureState State { get { lock (_gate) return _state; } }
    public ChannelReader<AudioFrame> Frames => _frames.Reader;
    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceStateChanged;
    public event EventHandler<AudioCaptureFailureEventArgs>? CaptureFailed;

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
                    _bytes,
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _catalog.InitializeAsync(cancellationToken).ConfigureAwait(false);
        SetState(_catalog.Resolve(AudioSelectionMode.Default, null) is null ? AudioCaptureState.DeviceLost : AudioCaptureState.Discovered);
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
            throw new InvalidOperationException(selectionMode == AudioSelectionMode.Fixed ? "AUDIO_DEVICE_UNAVAILABLE" : "AUDIO_DEFAULT_ENDPOINT_MISSING");
        }
        lock (_gate)
        {
            _selectionMode = selectionMode;
            _selected = selected with { SelectionMode = selectionMode, IsSelected = true };
        }
        SetState(AudioCaptureState.Selected);
        await Task.CompletedTask.ConfigureAwait(false);
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

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State is AudioCaptureState.Recording or AudioCaptureState.Paused)
            throw new InvalidOperationException("AUDIO_CAPTURE_ALREADY_RUNNING");
        if (_selected is null) await SelectDeviceAsync(_selectionMode, null, cancellationToken).ConfigureAwait(false);
        var selected = SelectedDevice ?? throw new InvalidOperationException("AUDIO_DEVICE_NOT_SELECTED");
        var device = _catalog.GetDevice(selected.Id) ?? throw new InvalidOperationException("AUDIO_DEVICE_NOT_FOUND");

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        ResetTelemetry();
        _frames = CreateChannel();
        _failureRaised = 0;
        SetState(AudioCaptureState.Starting);
        try
        {
            _capture = new WasapiCapture(device);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _clock.Restart();
            _capture.StartRecording();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref _frameCount) == 0)
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _frameCount) == 0)
            {
                RaiseFailure("AUDIO_NO_FRAMES", "Legacy WASAPI started but returned no non-empty frames.", true);
                throw new TimeoutException("AUDIO_CAPTURE_START_TIMEOUT");
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
        if (State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed) SetState(AudioCaptureState.Stopped);
        return Task.CompletedTask;
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
            && (args.EventType == "DEVICE_REMOVED" || args.Device.RuntimeStatus == "INACTIVE"))
            SetState(AudioCaptureState.DeviceLost);
        DeviceStateChanged?.Invoke(this, args);
    }

    private void OnDataAvailable(object? _, WaveInEventArgs args)
    {
        try
        {
            var format = _capture?.WaveFormat ?? throw new InvalidOperationException("AUDIO_FORMAT_MISSING");
            var bytes = Normalize(args.Buffer, args.BytesRecorded, format);
            if (bytes.Length == 0) return;
            var metrics = Measure(bytes);
            var frame = new AudioFrame(Interlocked.Read(ref _sampleCursor), bytes.Length / 2, DateTimeOffset.UtcNow, bytes, metrics.Rms, metrics.Peak, metrics.Clipping, AudioStreamFormats.Phase1Microphone);
            if (!_frames.Writer.TryWrite(frame))
            {
                RaiseFailure("AUDIO_PIPELINE_OVERRUN", "Legacy WASAPI frame queue is full.", false);
                _capture?.StopRecording();
                return;
            }
            Interlocked.Add(ref _sampleCursor, frame.SampleCount);
            Interlocked.Increment(ref _frameCount);
            Interlocked.Add(ref _bytes, bytes.Length);
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
        if (args.Exception is not null) RaiseFailure(MapError(args.Exception), args.Exception.Message, true);
    }

    private AudioDeviceProbeResult BuildProbeResult(long durationMs, string? error, string? detail, AudioTelemetrySnapshot telemetry)
        => new(SelectedDevice?.Id, SelectedDevice?.Name, SelectedDevice is not null, SelectedDevice?.State == "Active", error != "AUDIO_DEVICE_ACCESS_DENIED", true, _capture is not null, telemetry.FrameCount > 0, checked((int)Math.Min(int.MaxValue, telemetry.FrameCount)), telemetry.BytesReceived, telemetry.FirstFrameLatencyMs, telemetry.RmsDb is not null && telemetry.RmsDb > -50, telemetry.RmsDb, telemetry.PeakDb, telemetry.Clipping, RecordingContract.MicrophoneSampleRate, 1, "PCM_S16", error, detail, error is null && telemetry.FrameCount > 0 ? "READY" : "FAILED");

    private static byte[] Normalize(byte[] source, int length, WaveFormat format)
    {
        var descriptor = AudioSampleFormatResolver.Resolve(format);
        var sourceBytes = descriptor.BitsPerSample / 8 * descriptor.Channels;
        if (sourceBytes <= 0) return [];
        var sourceFrames = length / sourceBytes;
        var targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (double)RecordingContract.MicrophoneSampleRate / descriptor.SampleRate));
        var output = new byte[targetFrames * 2];
        for (var target = 0; target < targetFrames; target++)
        {
            var sourceIndex = Math.Min(sourceFrames - 1, (int)Math.Floor(target * descriptor.SampleRate / (double)RecordingContract.MicrophoneSampleRate));
            var sum = 0d;
            for (var channel = 0; channel < descriptor.Channels; channel++)
                sum += ReadSample(source, sourceIndex * sourceBytes + channel * descriptor.BitsPerSample / 8, descriptor);
            var value = (short)Math.Clamp((int)Math.Round(sum / descriptor.Channels * 32767d), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(target * 2, 2), value);
        }
        return output;
    }

    private static double ReadSample(byte[] bytes, int offset, AudioSampleFormatDescriptor descriptor)
        => descriptor.Kind switch
        {
            RawAudioSampleFormat.Float32 => BitConverter.ToSingle(bytes, offset),
            RawAudioSampleFormat.Pcm16 => BitConverter.ToInt16(bytes, offset) / 32768d,
            RawAudioSampleFormat.Pcm24 => ((bytes[offset + 2] << 24) | (bytes[offset + 1] << 16) | (bytes[offset] << 8)) / 2147483648d,
            RawAudioSampleFormat.Pcm32 => BitConverter.ToInt32(bytes, offset) / 2147483648d,
            _ => 0d
        };

    private void ResetTelemetry()
    {
        lock (_gate)
        {
            _sampleCursor = 0; _frameCount = 0; _bytes = 0; _firstFrameLatencyMs = null; _rmsSum = 0; _peak = 0; _clipping = false; _lastAudioAtUtc = null; _silenceStartedAtUtc = null;
        }
    }

    private void SetState(AudioCaptureState state) { lock (_gate) _state = state; }
    private void RaiseFailure(string code, string? detail, bool retryable) { if (Interlocked.Exchange(ref _failureRaised, 1) == 0) CaptureFailed?.Invoke(this, new AudioCaptureFailureEventArgs(code, detail, retryable, DateTimeOffset.UtcNow)); }
    private static Channel<AudioFrame> CreateChannel() => Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false });
    private static (double Rms, double Peak, bool Clipping) Measure(byte[] bytes)
    {
        var count = bytes.Length / 2; var sum = 0d; var peak = 0d; var clipping = false;
        for (var i = 0; i < count; i++) { var value = BitConverter.ToInt16(bytes, i * 2) / 32768d; var absolute = Math.Abs(value); sum += value * value; peak = Math.Max(peak, absolute); clipping |= absolute >= .999d; }
        return count == 0 ? (0, 0, false) : (Math.Sqrt(sum / count), peak, clipping);
    }
    private static double ToDb(double value) => value <= 0 ? -60 : Math.Clamp(20 * Math.Log10(value), -60, 0);
    private static long ElapsedMilliseconds(long start) => (long)Math.Round((Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency);
    private static string MapError(Exception exception) => exception is UnauthorizedAccessException || exception.ToString().Contains("access", StringComparison.OrdinalIgnoreCase) ? "AUDIO_DEVICE_ACCESS_DENIED" : exception.ToString().Contains("format", StringComparison.OrdinalIgnoreCase) ? "AUDIO_FORMAT_UNSUPPORTED" : exception.ToString().Contains("frame", StringComparison.OrdinalIgnoreCase) ? "AUDIO_NO_FRAMES" : "LEGACY_WASAPI_ERROR";
}

public sealed class LegacyWasapiDeviceCatalog : IAudioDeviceCatalog
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private Dictionary<string, AudioDeviceDescriptor> _devices = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<AudioDeviceDescriptor> Devices { get { lock (_gate) return _devices.Values.OrderBy(x => x.Name).ToArray(); } }
    public bool IsReady { get; private set; }
    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;
    public Task InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ReconcileAsync(cancellationToken); }
    public Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var defaultId = GetDefaultId();
        var next = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToDictionary(device => device.ID, device => new AudioDeviceDescriptor(device.ID, device.FriendlyName, AudioDataFlow.Capture, "Active", string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase), AudioSelectionMode.Default, false, DateTimeOffset.UtcNow), StringComparer.OrdinalIgnoreCase);
        List<AudioDeviceChangedEventArgs> changes = [];
        lock (_gate)
        {
            foreach (var item in next) if (!_devices.TryGetValue(item.Key, out var old) || old != item.Value) changes.Add(new AudioDeviceChangedEventArgs(old is null ? "DEVICE_ADDED" : "DEVICE_STATE_CHANGED", item.Value, DateTimeOffset.UtcNow));
            foreach (var removed in _devices.Keys.Except(next.Keys, StringComparer.OrdinalIgnoreCase).ToArray()) changes.Add(new AudioDeviceChangedEventArgs("DEVICE_REMOVED", _devices[removed] with { RuntimeStatus = "DEVICE_LOST" }, DateTimeOffset.UtcNow));
            _devices = next;
            IsReady = true;
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
    private string? GetDefaultId() { try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID; } catch { return null; } }
}

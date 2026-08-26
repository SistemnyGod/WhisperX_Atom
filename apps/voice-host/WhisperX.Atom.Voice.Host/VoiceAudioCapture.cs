using System.Buffers;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Linq;
using System.Runtime.InteropServices;

namespace WhisperX.Atom.Voice.Host;

public sealed class VoiceAudioBlock : IDisposable
{
    private byte[]? _buffer;
    public VoiceAudioBlock(byte[] buffer, int length, WaveFormat format)
    {
        _buffer = buffer;
        Length = length;
        Format = format;
    }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(VoiceAudioBlock));
    public int Length { get; }
    public WaveFormat Format { get; }
    public double DurationMs => Length * 1000d / Math.Max(1, Format.AverageBytesPerSecond);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}

public sealed record VoiceAudioTelemetry(
    long Sequence,
    DateTimeOffset? AtUtc,
    double Rms,
    double Peak,
    bool Clipping,
    string SignalState,
    string? DeviceId = null,
    string? DeviceName = null)
{
    public static VoiceAudioTelemetry Empty { get; } = new(0, null, 0, 0, false, "WAITING_FOR_AUDIO");
}

/// <summary>WASAPI callback only copies to pooled memory; processing is performed by one worker.</summary>
public sealed class VoiceAudioCapture : IDisposable
{
    private WasapiCapture? _capture;
    private readonly object _gate = new();
    private readonly object _telemetryGate = new();
    private VoiceAudioTelemetry _telemetry = VoiceAudioTelemetry.Empty;

    public event Action<VoiceAudioBlock>? AudioAvailable;
    public event Action<Exception>? CaptureError;
    public bool IsRunning => _capture is not null;
    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public string? LastErrorDetail { get; private set; }
    public VoiceAudioTelemetry Telemetry { get { lock (_telemetryGate) return _telemetry; } }
    public double LastPeak => Telemetry.Peak;
    public DateTimeOffset LastAudioAtUtc => Telemetry.AtUtc ?? default;

    public void Start(string? deviceId = null)
    {
        lock (_gate)
        {
            if (_capture is not null) return;
            LastErrorDetail = null;
            WasapiCapture capture;
            if (string.IsNullOrWhiteSpace(deviceId) || string.Equals(deviceId, "DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device;
                try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
                catch { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
                DeviceId = device.ID;
                DeviceName = device.FriendlyName;
                capture = new WasapiCapture(device);
            }
            else
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device;
                try { device = ResolveRequestedDevice(enumerator, deviceId); }
                catch (Exception ex)
                {
                    LastErrorDetail = ex.Message;
                    throw;
                }
                DeviceId = device.ID;
                DeviceName = device.FriendlyName;
                capture = new WasapiCapture(device);
            }
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnStopped;
            _capture = capture;
            try { capture.StartRecording(); }
            catch (Exception ex)
            {
                LastErrorDetail ??= ex.Message;
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnStopped;
                capture.Dispose();
                _capture = null;
                throw;
            }
        }
    }

    /// <summary>
    /// AudioGraph exposes the PnP form of an endpoint id while NAudio's
    /// MMDeviceEnumerator expects the shorter MMDevice id.  Keep this
    /// conversion deliberately narrow: fixed endpoints are never silently
    /// replaced with the Windows default device.
    /// </summary>
    internal static string? NormalizeEndpointId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)
            || string.Equals(deviceId.Trim(), "DEFAULT", StringComparison.OrdinalIgnoreCase)) return null;

        var value = deviceId.Trim();
        const string prefix = @"\\?\SWD#MMDEVAPI#";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value;

        var suffix = value[prefix.Length..];
        var classMarker = suffix.IndexOf('#');
        return (classMarker > 0 ? suffix[..classMarker] : suffix).Trim();
    }

    private static MMDevice ResolveRequestedDevice(MMDeviceEnumerator enumerator, string requestedId)
    {
        var requested = requestedId.Trim();
        var normalized = NormalizeEndpointId(requested);
        var candidates = new[] { normalized, requested }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            try { return enumerator.GetDevice(candidate!); }
            catch (ArgumentException) { }
            catch (COMException) { }
        }

        var available = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToArray();
        MMDevice? selected = null;
        try
        {
            selected = available.FirstOrDefault(device => candidates.Any(candidate =>
                string.Equals(device.ID, candidate, StringComparison.OrdinalIgnoreCase)));
            if (selected is not null) return selected;

            var list = string.Join("; ", available.Select(device => $"{device.ID} ({device.FriendlyName})"));
            throw new ArgumentException(
                $"Requested microphone endpoint '{requested}' mapped to '{normalized ?? "<default>"}' was not found. Active endpoints: {list}");
        }
        finally
        {
            foreach (var device in available)
                if (!ReferenceEquals(device, selected)) device.Dispose();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            var capture = _capture;
            if (capture is null) return;
            _capture = null;
            try { capture.StopRecording(); } catch (InvalidOperationException) { }
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnStopped;
            capture.Dispose();
            DeviceId = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        VoiceAudioBlock? block = null;
        try
        {
            if (sender is not WasapiCapture capture || args.BytesRecorded <= 0) return;
            var rented = ArrayPool<byte>.Shared.Rent(args.BytesRecorded);
            args.Buffer.AsSpan(0, args.BytesRecorded).CopyTo(rented);
            var metrics = ComputeMetrics(args.Buffer, args.BytesRecorded, capture.WaveFormat);
            lock (_telemetryGate)
            {
                var sequence = _telemetry.Sequence + 1;
                var state = metrics.Clipping ? "CLIPPING" : metrics.Rms < 0.005 ? "SILENCE" : "VOICE";
                _telemetry = new VoiceAudioTelemetry(sequence, DateTimeOffset.UtcNow, metrics.Rms, metrics.Peak, metrics.Clipping, state, DeviceId, DeviceName);
            }
            block = new VoiceAudioBlock(rented, args.BytesRecorded, capture.WaveFormat);
            var handler = AudioAvailable;
            if (handler is null) { block.Dispose(); return; }
            handler(block);
            block = null;
        }
        catch (Exception ex)
        {
            block?.Dispose();
            CaptureError?.Invoke(ex);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) CaptureError?.Invoke(args.Exception);
    }

    public void Dispose() => Stop();

    internal static (double Rms, double Peak, bool Clipping) ComputeMetrics(byte[] buffer, int length, WaveFormat format)
    {
        var peak = 0d;
        var sumSquares = 0d;
        var samples = 0;
        var clipping = false;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            for (var offset = 0; offset + 3 < length; offset += 4)
            {
                var value = BitConverter.ToSingle(buffer, offset);
                if (!float.IsFinite(value)) continue;
                var normalized = Math.Clamp(Math.Abs(value), 0d, 1d);
                peak = Math.Max(peak, normalized);
                sumSquares += normalized * normalized;
                samples++;
                clipping |= Math.Abs(value) >= 0.999f;
            }
        else if (format.BitsPerSample == 16)
            for (var offset = 0; offset + 1 < length; offset += 2)
            {
                var normalized = Math.Abs(BitConverter.ToInt16(buffer, offset) / 32768d);
                peak = Math.Max(peak, normalized);
                sumSquares += normalized * normalized;
                samples++;
                clipping |= normalized >= 0.999;
            }
        return (samples == 0 ? 0d : Math.Sqrt(sumSquares / samples), Math.Clamp(peak, 0d, 1d), clipping);
    }
}

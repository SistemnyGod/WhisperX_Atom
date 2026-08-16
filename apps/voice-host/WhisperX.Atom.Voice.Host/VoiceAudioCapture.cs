using System.Buffers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

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

/// <summary>WASAPI callback only copies to pooled memory; processing is performed by one worker.</summary>
public sealed class VoiceAudioCapture : IDisposable
{
    private WasapiCapture? _capture;
    private readonly object _gate = new();

    public event Action<VoiceAudioBlock>? AudioAvailable;
    public event Action<Exception>? CaptureError;
    public bool IsRunning => _capture is not null;

    public void Start(string? deviceId = null)
    {
        lock (_gate)
        {
            if (_capture is not null) return;
            WasapiCapture capture;
            if (string.IsNullOrWhiteSpace(deviceId) || string.Equals(deviceId, "DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                capture = new WasapiCapture();
            }
            else
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                capture = new WasapiCapture(device);
            }
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnStopped;
            _capture = capture;
            try { capture.StartRecording(); }
            catch
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnStopped;
                capture.Dispose();
                _capture = null;
                throw;
            }
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
}

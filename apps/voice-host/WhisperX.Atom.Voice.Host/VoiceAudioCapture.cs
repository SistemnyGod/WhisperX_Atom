using NAudio.CoreAudioApi;
using NAudio.Wave;
using WhisperX.Atom.Voice;

namespace WhisperX.Atom.Voice.Host;

public sealed class VoiceAudioCapture : IDisposable
{
    private readonly VoiceRingBuffer _ring = new(16000 * 2 * 3);
    private readonly VoiceStateMachine _state;
    private WasapiCapture? _capture;
    private readonly object _gate = new();

    public VoiceAudioCapture(VoiceStateMachine state) => _state = state;
    public event Action<ReadOnlyMemory<byte>>? PcmAvailable;

    public bool IsRunning => _capture is not null;

    public void Start()
    {
        lock (_gate)
        {
            if (_capture is not null) return;
            var capture = new WasapiCapture();
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnStopped;
            _capture = capture;
            capture.StartRecording();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_capture is null) return;
            try { _capture.StopRecording(); } catch (InvalidOperationException) { }
            _capture.Dispose();
            _capture = null;
        }
    }

    public byte[] PreRoll() => _ring.Snapshot();

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            if (sender is not WasapiCapture capture) return;
            var pcm = AudioPcmConverter.ToMono16K(args.Buffer.AsSpan(0, args.BytesRecorded), capture.WaveFormat);
            if (pcm.Length == 0) return;
            _ring.Append(pcm);
            PcmAvailable?.Invoke(pcm);
        }
        catch { /* audio callback must never terminate the capture thread */ }
    }

    private static void OnStopped(object? sender, StoppedEventArgs args) { }

    public void Dispose() => Stop();
}

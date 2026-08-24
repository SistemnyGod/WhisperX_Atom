namespace WhisperX.Atom.Voice.Host;

/// <summary>
/// Keeps the wake pre-roll and the current utterance in memory only.  It is
/// deliberately independent from the durable Recorder PCM path.
/// </summary>
internal sealed class UtteranceCaptureBuffer
{
    private readonly object _gate = new();
    private readonly int _preRollBytes;
    private readonly int _maxBytes;
    private readonly int _postRollBytes;
    private readonly VoiceRingBuffer _preRoll;
    private MemoryStream? _utterance;
    private DateTimeOffset _startedAt;

    public UtteranceCaptureBuffer(int sampleRate = 16_000, double preRollSeconds = 2, double maxSeconds = 20, double postRollSeconds = 0.4)
    {
        _preRollBytes = checked((int)(sampleRate * 2 * Math.Max(0.5, preRollSeconds)));
        _maxBytes = checked((int)(sampleRate * 2 * Math.Max(1, maxSeconds)));
        _postRollBytes = checked((int)(sampleRate * 2 * Math.Max(0.1, postRollSeconds)));
        _preRoll = new VoiceRingBuffer(_preRollBytes);
    }

    public bool IsActive { get { lock (_gate) return _utterance is not null; } }
    public int PostRollBytes => _postRollBytes;

    public void Append(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0) return;
        lock (_gate)
        {
            _preRoll.Append(pcm.ToArray());
            if (_utterance is null) return;
            var remaining = _maxBytes - checked((int)_utterance.Length);
            if (remaining <= 0) return;
            _utterance.Write(pcm[..Math.Min(remaining, pcm.Length)]);
        }
    }

    public void Begin(DateTimeOffset startedAtUtc)
    {
        lock (_gate)
        {
            if (_utterance is not null) return;
            _utterance = new MemoryStream(_maxBytes);
            var preRoll = _preRoll.Snapshot();
            _utterance.Write(preRoll, 0, preRoll.Length);
            _startedAt = startedAtUtc;
        }
    }

    public byte[] Complete(bool includePostRoll = true)
    {
        lock (_gate)
        {
            if (_utterance is null) return _preRoll.Snapshot();
            var result = _utterance.ToArray();
            _utterance.Dispose();
            _utterance = null;
            _startedAt = default;
            return result;
        }
    }

    public byte[] Snapshot()
    {
        lock (_gate)
        {
            if (_utterance is not null) return _utterance.ToArray();
            return _preRoll.Snapshot();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _utterance?.Dispose();
            _utterance = null;
            _startedAt = default;
            _preRoll.Clear();
        }
    }
}

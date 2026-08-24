namespace WhisperX.Atom.Voice.Host;

/// <summary>
/// Keeps wake pre-roll and Shadow capture in memory only. Speech capacity is
/// independent from pre-roll: 2 seconds of context plus 20 seconds of speech
/// and a 400 ms post-roll.
/// </summary>
internal sealed class UtteranceCaptureBuffer
{
    private readonly object _gate = new();
    private readonly int _preRollBytes;
    private readonly int _speechMaxBytes;
    private readonly int _postRollBytes;
    private readonly VoiceRingBuffer _preRoll;
    private readonly List<PendingCapture> _pending = [];
    private MemoryStream? _utterance;
    private int _speechBytes;
    private bool _speechTruncated;
    private DateTimeOffset _startedAt;

    public UtteranceCaptureBuffer(int sampleRate = 16_000, double preRollSeconds = 2, double maxSeconds = 20, double postRollSeconds = 0.4)
    {
        _preRollBytes = checked((int)(sampleRate * 2 * Math.Max(0.5, preRollSeconds)));
        _speechMaxBytes = checked((int)(sampleRate * 2 * Math.Max(1, maxSeconds)));
        _postRollBytes = checked((int)(sampleRate * 2 * Math.Max(0.1, postRollSeconds)));
        _preRoll = new VoiceRingBuffer(_preRollBytes);
    }

    public bool IsActive { get { lock (_gate) return _utterance is not null; } }
    public int PostRollBytes => _postRollBytes;

    public void Append(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0) return;
        var bytes = pcm.ToArray();
        lock (_gate)
        {
            _preRoll.Append(bytes);
            foreach (var pending in _pending.ToArray()) pending.Append(bytes);
            if (_utterance is null) return;
            var remaining = _speechMaxBytes - _speechBytes;
            if (remaining <= 0)
            {
                _speechTruncated = true;
                return;
            }
            var count = Math.Min(remaining, bytes.Length);
            _utterance.Write(bytes, 0, count);
            _speechBytes += count;
            if (count < bytes.Length) _speechTruncated = true;
        }
    }

    public void Begin(DateTimeOffset startedAtUtc)
    {
        lock (_gate)
        {
            if (_utterance is not null) return;
            _utterance = new MemoryStream(_preRollBytes + _speechMaxBytes);
            var preRoll = _preRoll.Snapshot();
            _utterance.Write(preRoll, 0, preRoll.Length);
            _speechBytes = 0;
            _speechTruncated = false;
            _startedAt = startedAtUtc;
        }
    }

    /// <summary>Legacy immediate completion; callers needing post-roll use CompleteForShadow.</summary>
    public byte[] Complete(bool includePostRoll = true) => CompleteForShadow().InitialPcm16kMono;

    /// <summary>
    /// Freezes the accepted command immediately and collects post-roll in the
    /// background. Awaiting Completion never blocks the Recorder/Assistant path.
    /// </summary>
    public PendingCapture CompleteForShadow()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_utterance is null)
            {
                var pcm = _preRoll.Snapshot();
                return PendingCapture.Immediate(pcm, now, pcm.Length / 32.0, 0, 0, false);
            }
            var preRollLength = Math.Min(_preRollBytes, _utterance.Length);
            var pending = new PendingCapture(
                _utterance.ToArray(),
                _startedAt == default ? now : _startedAt,
                preRollLength / 32.0,
                _speechBytes / 32.0,
                _speechTruncated,
                _postRollBytes);
            _pending.Add(pending);
            _utterance.Dispose();
            _utterance = null;
            _speechBytes = 0;
            _speechTruncated = false;
            _startedAt = default;
            _ = pending.CompleteAfterDelayAsync();
            _ = pending.Completion.ContinueWith(_ =>
            {
                lock (_gate) _pending.Remove(pending);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return pending;
        }
    }

    public byte[] Snapshot()
    {
        lock (_gate) return _utterance is not null ? _utterance.ToArray() : _preRoll.Snapshot();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _utterance?.Dispose();
            _utterance = null;
            _speechBytes = 0;
            _speechTruncated = false;
            _startedAt = default;
            _preRoll.Clear();
            foreach (var pending in _pending.ToArray()) pending.CompleteImmediately();
            _pending.Clear();
        }
    }

    internal sealed class PendingCapture
    {
        private readonly object _gate = new();
        private readonly MemoryStream _postRoll = new();
        private readonly int _postRollCapacity;
        private readonly double _preRollMs;
        private readonly double _speechMs;
        private readonly bool _truncated;
        private int _remaining;
        private bool _completed;
        private readonly TaskCompletionSource<CaptureResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PendingCapture(byte[] initialPcm16kMono, DateTimeOffset capturedAtUtc, double preRollMs, double speechMs, bool truncated, int postRollCapacity)
        {
            InitialPcm16kMono = initialPcm16kMono;
            CapturedAtUtc = capturedAtUtc;
            _preRollMs = preRollMs;
            _speechMs = speechMs;
            _truncated = truncated;
            _postRollCapacity = postRollCapacity;
            _remaining = postRollCapacity;
        }

        public byte[] InitialPcm16kMono { get; }
        public DateTimeOffset CapturedAtUtc { get; }
        public Task<CaptureResult> Completion => _completion.Task;

        public static PendingCapture Immediate(byte[] pcm, DateTimeOffset at, double preRollMs, double speechMs, double postRollMs, bool truncated)
        {
            var pending = new PendingCapture(pcm, at, preRollMs, speechMs, truncated, 0);
            pending._completed = true;
            pending._completion.TrySetResult(new CaptureResult(pcm, preRollMs, speechMs, postRollMs, truncated));
            return pending;
        }

        public void Append(byte[] pcm)
        {
            lock (_gate)
            {
                if (_completed || _remaining <= 0) return;
                var count = Math.Min(_remaining, pcm.Length);
                _postRoll.Write(pcm, 0, count);
                _remaining -= count;
                if (_remaining == 0) CompleteLocked();
            }
        }

        public async Task CompleteAfterDelayAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_postRollCapacity / 32.0)).ConfigureAwait(false);
            lock (_gate) CompleteLocked();
        }

        public void CompleteImmediately()
        {
            lock (_gate) CompleteLocked();
        }

        private void CompleteLocked()
        {
            if (_completed) return;
            _completed = true;
            var post = _postRoll.ToArray();
            var all = new byte[InitialPcm16kMono.Length + post.Length];
            Buffer.BlockCopy(InitialPcm16kMono, 0, all, 0, InitialPcm16kMono.Length);
            Buffer.BlockCopy(post, 0, all, InitialPcm16kMono.Length, post.Length);
            _completion.TrySetResult(new CaptureResult(
                all,
                _preRollMs,
                _speechMs,
                post.Length / 32.0,
                // A short post-roll is normal when the endpoint is followed
                // by silence or the host is shutting down. `truncated` only
                // describes the speech window reaching its hard limit.
                _truncated));
        }
    }

    internal sealed record CaptureResult(byte[] Pcm16kMono, double PreRollMs, double SpeechMs, double PostRollMs, bool Truncated);
}

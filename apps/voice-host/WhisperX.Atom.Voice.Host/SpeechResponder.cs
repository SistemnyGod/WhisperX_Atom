using System.Speech.Synthesis;
using System.Globalization;
using System.Threading.Channels;

namespace WhisperX.Atom.Voice.Host;

public enum SpeechPlaybackState
{
    Accepted,
    Played,
    Cancelled,
    Failed
}

public sealed record SpeechPlaybackResult(SpeechPlaybackState State, bool Started, string? ErrorCode = null);

internal sealed record SpeechRequest(
    string Text,
    TaskCompletionSource<SpeechPlaybackResult> Completion,
    long Generation,
    Func<Task>? PlaybackStarted);

public sealed class SpeechResponder : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly object _gate = new();
    private readonly Channel<SpeechRequest> _queue = Channel.CreateBounded<SpeechRequest>(new BoundedChannelOptions(8)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private bool _disposed;
    private int _busy;
    private int _speaking;
    private bool _russianVoiceAvailable;
    private string? _requestedVoiceName;
    private int _queueDepth;
    private long _queueDrops;
    private long _cancelGeneration;
    private TaskCompletionSource<SpeechPlaybackResult>? _activePlayback;

    public SpeechResponder()
    {
        // Always synthesize with the installed Russian Windows voice. The
        // legacy WAV bundle can contain mojibake and must never override a
        // configured live voice, even if an old environment variable remains
        // on a machine after an upgrade.
        // Prefer the deterministic Russian voice requested by the product.
        // Windows often exposes both "Microsoft Irina" and "Microsoft Irina
        // Desktop"; the desktop variant is a safe fallback, never an English
        // voice.  An explicit ATOM_VOICE_NAME remains useful for diagnostics.
        try
        {
            var installed = _synthesizer.GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .Select(voice => voice.VoiceInfo)
                .ToArray();
            var requestedName = Environment.GetEnvironmentVariable("ATOM_VOICE_NAME");
            _requestedVoiceName = requestedName;
            var russianVoice = installed.FirstOrDefault(voice => !string.IsNullOrWhiteSpace(requestedName)
                    && string.Equals(voice.Name, requestedName, StringComparison.OrdinalIgnoreCase)
                    && voice.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(voice => string.Equals(voice.Name, "Microsoft Irina", StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(voice => string.Equals(voice.Name, "Microsoft Irina Desktop", StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(voice => voice.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
            if (russianVoice is not null)
            {
                _synthesizer.SelectVoice(russianVoice.Name);
                _russianVoiceAvailable = true;
            }
        }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        _synthesizer.Rate = ReadIntEnvironment("ATOM_VOICE_RATE", 0, -10, 10);
        _synthesizer.Volume = ReadIntEnvironment("ATOM_VOICE_VOLUME", 90, 0, 100);
        _worker = Task.Run(ProcessAsync);
    }

    public bool QuietMode { get; set; }
    public string? RequestedVoiceName => _requestedVoiceName;
    public bool IsRussianVoiceAvailable => _russianVoiceAvailable;
    public int QueueDepth => Volatile.Read(ref _queueDepth);
    public long QueueDrops => Interlocked.Read(ref _queueDrops);
    public bool VoiceFallbackUsed => !string.IsNullOrWhiteSpace(_requestedVoiceName)
        && !string.Equals(_requestedVoiceName, VoiceName, StringComparison.OrdinalIgnoreCase);
    public bool UsesPreRecordedResponses => false;
    public string VoiceName
    {
        get
        {
            try { return _synthesizer.Voice.Name; }
            catch (InvalidOperationException) { return "Windows default"; }
        }
    }
    public string VoiceCulture
    {
        get
        {
            try { return _synthesizer.Voice.Culture.Name; }
            catch (InvalidOperationException) { return CultureInfo.CurrentUICulture.Name; }
        }
    }
    public bool ConfigureVoice(string? requestedName, int? rate, int? volume)
    {
        lock (_gate)
        {
            try
            {
                var installed = _synthesizer.GetInstalledVoices()
                    .Where(voice => voice.Enabled && voice.VoiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                    .Select(voice => voice.VoiceInfo)
                    .ToArray();
                var selected = !string.IsNullOrWhiteSpace(requestedName)
                    ? installed.FirstOrDefault(voice => string.Equals(voice.Name, requestedName.Trim(), StringComparison.OrdinalIgnoreCase))
                    : null;
                selected ??= installed.FirstOrDefault(voice => string.Equals(voice.Name, "Microsoft Irina", StringComparison.OrdinalIgnoreCase));
                selected ??= installed.FirstOrDefault(voice => string.Equals(voice.Name, "Microsoft Irina Desktop", StringComparison.OrdinalIgnoreCase));
                selected ??= installed.FirstOrDefault();
                if (selected is null)
                {
                    _russianVoiceAvailable = false;
                    return false;
                }
                _synthesizer.SelectVoice(selected.Name);
                _requestedVoiceName = string.IsNullOrWhiteSpace(requestedName) ? _requestedVoiceName : requestedName.Trim();
                _synthesizer.Rate = Math.Clamp(rate ?? _synthesizer.Rate, -10, 10);
                _synthesizer.Volume = Math.Clamp(volume ?? _synthesizer.Volume, 0, 100);
                _russianVoiceAvailable = true;
                return true;
            }
            catch (InvalidOperationException) { _russianVoiceAvailable = false; return false; }
            catch (ArgumentException) { _russianVoiceAvailable = false; return false; }
        }
    }
    public IReadOnlyList<string> GetRussianVoiceNames()
    {
        lock (_gate)
        {
            try
            {
                return _synthesizer.GetInstalledVoices()
                    .Where(voice => voice.Enabled && voice.VoiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                    .Select(voice => voice.VoiceInfo.Name)
                    .OrderBy(name => string.Equals(name, "Microsoft Irina", StringComparison.OrdinalIgnoreCase) ? 0
                        : string.Equals(name, "Microsoft Irina Desktop", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (InvalidOperationException) { return []; }
        }
    }
    public bool IsSpeaking => Volatile.Read(ref _speaking) != 0;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public event Action<Exception>? Error;

    public bool TryEnqueue(string text)
        => TryEnqueue(text, out _);

    public bool TryEnqueue(string text, out Task completion)
    {
        var accepted = TryEnqueueDetailed(text, out var detailed);
        completion = detailed;
        return accepted;
    }

    /// <summary>Queues speech without conflating queue acceptance with playback completion.</summary>
    public bool TryEnqueueDetailed(string text, out Task<SpeechPlaybackResult> completion)
        => TryEnqueueDetailed(text, playbackStarted: null, out completion);

    /// <summary>
    /// Queues speech and invokes <paramref name="playbackStarted"/> immediately
    /// before the synthesizer starts emitting audio.  Keeping this callback on
    /// the queue item (rather than in the caller) is important: a response may
    /// wait behind another response and must not open its technical interval
    /// while it is merely queued.
    /// </summary>
    public bool TryEnqueueDetailed(string text, Func<Task>? playbackStarted, out Task<SpeechPlaybackResult> completion)
    {
        var completed = new TaskCompletionSource<SpeechPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = completed.Task;
        if (_disposed || QuietMode || !_russianVoiceAvailable || string.IsNullOrWhiteSpace(text))
        {
            var errorCode = _disposed
                ? "VOICE_HOST_STOPPED"
                : QuietMode
                    ? "VOICE_QUIET_MODE"
                    : string.IsNullOrWhiteSpace(text)
                        ? "VOICE_ASSISTANT_EMPTY_ANSWER"
                        : "VOICE_RUSSIAN_VOICE_UNAVAILABLE";
            completed.TrySetResult(new SpeechPlaybackResult(
                QuietMode ? SpeechPlaybackState.Cancelled : SpeechPlaybackState.Failed,
                false,
                errorCode));
            return false;
        }
        lock (_gate)
        {
            Volatile.Write(ref _busy, 1);
            if (_queue.Writer.TryWrite(new SpeechRequest(text, completed, Volatile.Read(ref _cancelGeneration), playbackStarted)))
            {
                Interlocked.Increment(ref _queueDepth);
                return true;
            }
            if (!_queue.Reader.TryPeek(out _)) Volatile.Write(ref _busy, 0);
            Interlocked.Increment(ref _queueDrops);
            completed.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Failed, false, "VOICE_ASSISTANT_QUEUE_FULL"));
            return false;
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                Interlocked.Decrement(ref _queueDepth);
                try
                {
                    if (QuietMode)
                    {
                        request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false, "VOICE_QUIET_MODE"));
                        continue;
                    }
                    if (request.Generation != Volatile.Read(ref _cancelGeneration))
                    {
                        request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false));
                        continue;
                    }
                    Volatile.Write(ref _speaking, 1);
                    // SpeakAsync invokes the callback immediately after the
                    // synthesizer accepts the text. This is the first point
                    // at which playback can actually begin; queued items do
                    // not create a technical interval while waiting.
                    var playback = await SpeakAsync(request.Text, request.Generation, request.PlaybackStarted, _shutdown.Token).ConfigureAwait(false);
                    request.Completion.TrySetResult(playback);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (OperationCanceledException)
                {
                    request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, true));
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex);
                    request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Failed, true, "VOICE_TTS_FAILED"));
                }
                finally
                {
                    Volatile.Write(ref _speaking, 0);
                    request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false));
                    lock (_gate)
                    {
                        if (!_queue.Reader.TryPeek(out _)) Volatile.Write(ref _busy, 0);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task<SpeechPlaybackResult> SpeakAsync(string text, long generation, Func<Task>? playbackStarted, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<SpeechPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SpeakCompletedEventArgs>? handler = null;
        handler = (_, args) => completed.TrySetResult(
            args.Cancelled
                ? new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, true)
                : args.Error is null
                    ? new SpeechPlaybackResult(SpeechPlaybackState.Played, true)
                    : new SpeechPlaybackResult(SpeechPlaybackState.Failed, true, "VOICE_TTS_FAILED"));
        lock (_gate)
        {
            if (generation != Volatile.Read(ref _cancelGeneration))
                return new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false);
            _synthesizer.SpeakCompleted += handler;
            try
            {
                _activePlayback = completed;
                _synthesizer.SpeakAsync(text);
            }
            catch
            {
                _activePlayback = null;
                _synthesizer.SpeakCompleted -= handler;
                throw;
            }
        }
        if (playbackStarted is not null)
        {
            try { await playbackStarted().ConfigureAwait(false); }
            catch (Exception ex)
            {
                // Timeline/diagnostic persistence must never prevent
                // the accepted speech from completing.
                Error?.Invoke(ex);
            }
        }
        return await AwaitPlaybackAsync(completed, handler, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpeechPlaybackResult> AwaitPlaybackAsync(TaskCompletionSource<SpeechPlaybackResult> playbackSource, EventHandler<SpeakCompletedEventArgs> handler, CancellationToken cancellationToken)
    {
        var playback = playbackSource.Task;
        try { return await playback.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                try { _synthesizer.SpeakAsyncCancelAll(); } catch (InvalidOperationException) { }
            }
            return new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, true);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activePlayback, playbackSource)) _activePlayback = null;
                _synthesizer.SpeakCompleted -= handler;
            }
        }
    }

    private static int ReadIntEnvironment(string name, int fallback, int minimum, int maximum)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    public bool Test() => TryEnqueue("Голосовой помощник готов");

    public void CancelAll()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _cancelGeneration);
            var playbackWasActive = _activePlayback is not null || Volatile.Read(ref _speaking) != 0;
            _activePlayback?.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, true));
            try { _synthesizer.SpeakAsyncCancelAll(); } catch (InvalidOperationException) { }
            while (_queue.Reader.TryRead(out var request))
            {
                Interlocked.Decrement(ref _queueDepth);
                request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false));
            }
            // Keep microphone frames on the dedicated cancel recognizer until
            // the playback worker has observed cancellation. Otherwise the
            // normal wake recognizer can consume the acoustic tail of TTS.
            Volatile.Write(ref _busy, playbackWasActive ? 1 : 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _cancelGeneration);
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        lock (_gate)
        {
            try { _synthesizer.SpeakAsyncCancelAll(); } catch (InvalidOperationException) { }
        }
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        lock (_gate) _synthesizer.Dispose();
        _shutdown.Dispose();
    }
}

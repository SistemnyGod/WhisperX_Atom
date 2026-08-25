using System.Diagnostics;
using System.Threading.Channels;
using WhisperX.Atom.Voice.Host.Tts;

namespace WhisperX.Atom.Voice.Host;

public enum SpeechPlaybackState { Accepted, Played, Cancelled, Failed }

public sealed record SpeechPlaybackResult(
    SpeechPlaybackState State, bool Started, string? ErrorCode = null,
    string? Engine = null, string? Model = null, string? Voice = null,
    bool FallbackUsed = false, long QueueWaitMs = 0, long SynthesisMs = 0, long PlaybackMs = 0);

internal sealed record SpeechRequest(
    string Text, TaskCompletionSource<SpeechPlaybackResult> Completion,
    long Generation, long AcceptedAt, Func<Task>? PlaybackStarted);

/// Owns the bounded speech queue and playback lifecycle. Synthesis is delegated
/// to the local Silero router; Windows Speech is only a Russian fallback.
public sealed class SpeechResponder : IDisposable
{
    private readonly object _gate = new();
    private readonly Channel<SpeechRequest> _queue = Channel.CreateBounded<SpeechRequest>(new BoundedChannelOptions(8)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TtsEngineRouter _router;
    private readonly SpeechAudioPlayer _player;
    private readonly Task _worker;
    private bool _disposed;
    private int _busy;
    private int _speaking;
    private int _queueDepth;
    private long _queueDrops;
    private long _cancelGeneration;
    private int _lastCancelHadPlayback;
    private int _volume = 90;
    private int _rate;
    private string _requestedSileroVoice = "eugene";
    private string _voiceProfile = TtsVoiceProfiles.MifodiyTech;
    private string _requestedWindowsVoice = "Microsoft Irina";
    private int _sampleRate = 48000;
    private int _cpuThreads = 4;
    private bool _fallbackEnabled = true;
    private int _duckedVolume = -1;

    public SpeechResponder() : this(CreateDefaultRouter(string.Empty)) { }
    internal SpeechResponder(string expectedBuildIdentity) : this(CreateDefaultRouter(expectedBuildIdentity)) { }
    internal SpeechResponder(TtsEngineRouter router) { _router = router; _player = router.Player; _worker = Task.Run(ProcessAsync); }

    public bool QuietMode { get; set; }
    public bool UsesPreRecordedResponses => false;
    public bool IsRussianVoiceAvailable => _router.IsReady;
    public bool IsSpeaking => Volatile.Read(ref _speaking) != 0;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public int QueueDepth => Volatile.Read(ref _queueDepth);
    public long QueueDrops => Interlocked.Read(ref _queueDrops);
    public bool LastCancelHadPlayback => Volatile.Read(ref _lastCancelHadPlayback) != 0;
    public string? RequestedVoiceName => _router.FallbackUsed ? _requestedWindowsVoice : _requestedSileroVoice;
    public string VoiceName => _router.VoiceName;
    public string VoiceCulture => _router.VoiceCulture;
    public bool VoiceFallbackUsed => _router.FallbackUsed;
    public string TtsEngine => _router.EngineName;
    public string TtsModel => _router.ModelName;
    public bool TtsReady => _router.IsReady;
    public bool TtsFallbackEnabled => _fallbackEnabled;
    public string? TtsFallbackReason => _router.FallbackReason;
    public int? TtsHostProcessId => _router.ProcessId;
    public long TtsLastSynthesisMs => _router.LastSynthesisMs;
    public long TtsModelLoadMs => _router.LastModelLoadMs;
    public int TtsRestartCount => _router.RestartCount;
    public int TtsSampleRate => _sampleRate;
    public int TtsCpuThreads => _cpuThreads;
    public string VoiceProfile => _voiceProfile;
    public bool TtsFxEnabled => _router.FxEnabled;
    public bool TtsFxApplied => _router.FxApplied;
    public string? TtsFxFallbackReason => _router.FxFallbackReason;
    public event Action<Exception>? Error;

    public bool ConfigureVoice(string? requestedName, int? rate, int? volume)
    {
        _requestedWindowsVoice = string.IsNullOrWhiteSpace(requestedName) ? "Microsoft Irina" : requestedName.Trim();
        _rate = Math.Clamp(rate ?? _rate, -10, 10); _volume = Math.Clamp(volume ?? _volume, 0, 100); return true;
    }

    public async Task<bool> ConfigureAsync(string? windowsFallbackVoice, int? rate, int? volume, string? sileroVoice,
        int? sampleRate, int? cpuThreads, bool fallbackEnabled, CancellationToken cancellationToken = default, string? ttsEngine = null, string? voiceProfile = null)
    {
        _requestedWindowsVoice = string.IsNullOrWhiteSpace(windowsFallbackVoice) ? "Microsoft Irina" : windowsFallbackVoice.Trim();
        _voiceProfile = TtsVoiceProfiles.Normalize(voiceProfile);
        _requestedSileroVoice = string.IsNullOrWhiteSpace(sileroVoice) ? "eugene" : sileroVoice.Trim();
        if (TtsVoiceProfiles.IsMifodiyTech(_voiceProfile)) _requestedSileroVoice = "eugene";
        _rate = Math.Clamp(rate ?? 0, -10, 10); _volume = Math.Clamp(volume ?? 90, 0, 100);
        _sampleRate = sampleRate is 24000 or 48000 ? sampleRate.Value : 48000; _cpuThreads = Math.Clamp(cpuThreads ?? 4, 1, 32); _fallbackEnabled = fallbackEnabled;
        return await _router.ConfigureAsync(_requestedWindowsVoice, _rate, _volume, _fallbackEnabled, _requestedSileroVoice, _sampleRate, _cpuThreads, cancellationToken, ttsEngine, _voiceProfile).ConfigureAwait(false);
    }

    public IReadOnlyList<string> GetRussianVoiceNames() => _router.GetRussianVoiceNames();
    public IReadOnlyList<object> GetTtsVoices() => _router.GetTtsVoices();
    public bool TryEnqueue(string text) => TryEnqueue(text, out _);
    public bool TryEnqueue(string text, out Task completion) { var ok = TryEnqueueDetailed(text, null, out var task); completion = task; return ok; }
    public bool TryEnqueueDetailed(string text, out Task<SpeechPlaybackResult> completion) => TryEnqueueDetailed(text, null, out completion);

    public bool TryEnqueueDetailed(string text, Func<Task>? playbackStarted, out Task<SpeechPlaybackResult> completion)
    {
        var source = new TaskCompletionSource<SpeechPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously); completion = source.Task;
        if (_disposed || QuietMode || string.IsNullOrWhiteSpace(text) || text.Length > 600)
        {
            source.TrySetResult(new SpeechPlaybackResult(QuietMode ? SpeechPlaybackState.Cancelled : SpeechPlaybackState.Failed, false,
                _disposed ? "VOICE_HOST_STOPPED" : QuietMode ? "VOICE_QUIET_MODE" : string.IsNullOrWhiteSpace(text) ? "VOICE_ASSISTANT_EMPTY_ANSWER" : "TTS_SYNTHESIS_FAILED")); return false;
        }
        lock (_gate)
        {
            Volatile.Write(ref _busy, 1);
            if (_queue.Writer.TryWrite(new SpeechRequest(text.Trim(), source, Volatile.Read(ref _cancelGeneration), Stopwatch.GetTimestamp(), playbackStarted))) { Interlocked.Increment(ref _queueDepth); return true; }
            Interlocked.Increment(ref _queueDrops); source.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Failed, false, "VOICE_ASSISTANT_QUEUE_FULL"));
            if (!_queue.Reader.TryPeek(out _)) Volatile.Write(ref _busy, 0); return false;
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                if (QuietMode || request.Generation != Volatile.Read(ref _cancelGeneration)) { request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false)); UpdateBusy(); continue; }
                Volatile.Write(ref _speaking, 1);
                try
                {
                    var queueWait = ElapsedMs(request.AcceptedAt);
                    var synthesized = await _router.SynthesizeAsync(request.Text, new TtsOptions(_requestedSileroVoice, _sampleRate, _rate, _volume, _cpuThreads), _shutdown.Token).ConfigureAwait(false);
                    if (!synthesized.Success || synthesized.AudioPath is null)
                    { request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Failed, false, synthesized.ErrorCode ?? "VOICE_TTS_UNAVAILABLE", synthesized.Engine, synthesized.Model, synthesized.Voice, _router.FallbackUsed, queueWait, synthesized.SynthesisMs)); continue; }
                    try
                    {
                        if (request.Generation != Volatile.Read(ref _cancelGeneration)) { request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false, "TTS_AUDIO_CANCELLED", synthesized.Engine, synthesized.Model, synthesized.Voice, _router.FallbackUsed, queueWait, synthesized.SynthesisMs)); continue; }
                        var started = false;
                        var playback = await _player.PlayAsync(synthesized.AudioPath, _volume, async () =>
                        {
                            started = true;
                            if (request.PlaybackStarted is null) return;
                            try { await request.PlaybackStarted().ConfigureAwait(false); }
                            catch (Exception callbackError) { Error?.Invoke(callbackError); }
                        }, _shutdown.Token, applyVoiceFx: _router.FxEnabled).ConfigureAwait(false);
                        _router.RecordPlaybackFx(playback.FxApplied, playback.FxFallbackReason);
                        request.Completion.TrySetResult(new SpeechPlaybackResult(playback.Played ? SpeechPlaybackState.Played : playback.Cancelled ? SpeechPlaybackState.Cancelled : SpeechPlaybackState.Failed, started, playback.ErrorCode, synthesized.Engine, synthesized.Model, synthesized.Voice, _router.FallbackUsed, queueWait, synthesized.SynthesisMs, playback.PlaybackMs));
                    }
                    finally { WindowsTtsEngine.TryDelete(synthesized.AudioPath); }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false, "TTS_AUDIO_CANCELLED")); }
                catch (Exception ex) { Error?.Invoke(ex); request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Failed, false, "VOICE_TTS_FAILED")); }
                finally { Volatile.Write(ref _speaking, 0); UpdateBusy(); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public bool Test() => TryEnqueue("Здравствуйте. Я Мифодий. Голосовой помощник готов к работе.");
    public async Task<SpeechPlaybackResult> TestAsync(CancellationToken cancellationToken = default)
    {
        if (!TryEnqueueDetailed("Здравствуйте. Я Мифодий. Голосовой помощник готов к работе.", out var completion))
            return await completion.ConfigureAwait(false);
        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _cancelGeneration);
            var playbackWasActive = Volatile.Read(ref _speaking) != 0;
            Volatile.Write(ref _lastCancelHadPlayback, playbackWasActive ? 1 : 0);
            _player.Stop();
            while (_queue.Reader.TryRead(out var request)) { Interlocked.Decrement(ref _queueDepth); request.Completion.TrySetResult(new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, false, "TTS_AUDIO_CANCELLED")); }
            Volatile.Write(ref _busy, 0);
        }
    }

    public void DuckPlayback(int percent)
    {
        lock (_gate)
        {
            if (_duckedVolume < 0) _duckedVolume = _volume;
            _volume = Math.Clamp(percent, 0, 100);
            _player.SetVolume(_volume);
        }
    }

    public void RestorePlaybackVolume()
    {
        lock (_gate)
        {
            if (_duckedVolume < 0) return;
            _volume = _duckedVolume;
            _player.SetVolume(_volume);
            _duckedVolume = -1;
        }
    }

    private void UpdateBusy() { lock (_gate) if (Volatile.Read(ref _queueDepth) == 0 && Volatile.Read(ref _speaking) == 0) Volatile.Write(ref _busy, 0); }
    private static long ElapsedMs(long started) => (long)(Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;

    private static TtsEngineRouter CreateDefaultRouter(string expectedBuildIdentity)
    {
        var ttsHostRoot = ResolveTtsHostRoot(AppContext.BaseDirectory);
        var executable = Path.Combine(ttsHostRoot, OperatingSystem.IsWindows() ? "TtsHost.exe" : "TtsHost");
        var modelRoot = Path.Combine(ttsHostRoot, "Models", "silero-v5_5_ru");
        var identity = string.IsNullOrWhiteSpace(expectedBuildIdentity) ? Environment.GetEnvironmentVariable("WHISPERX_BUILD_IDENTITY") ?? string.Empty : expectedBuildIdentity;
        var silero = new SileroTtsEngine(executable, modelRoot, Environment.GetEnvironmentVariable("ATOM_TTS_MODEL_SHA256") ?? string.Empty, identity);
        return new TtsEngineRouter(silero, new WindowsTtsEngine(), new SpeechAudioPlayer());
    }

    internal static string ResolveTtsHostRoot(string voiceHostBaseDirectory)
    {
        var executableName = OperatingSystem.IsWindows() ? "TtsHost.exe" : "TtsHost";
        var nested = Path.Combine(voiceHostBaseDirectory, "TtsHost");
        if (File.Exists(Path.Combine(nested, executableName))) return nested;

        var normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(voiceHostBaseDirectory));
        var parent = Directory.GetParent(normalizedBase)?.FullName;
        var sibling = parent is null ? null : Path.Combine(parent, "TtsHost");
        if (sibling is not null && File.Exists(Path.Combine(sibling, executableName))) return sibling;

        // Keep the historical nested path in diagnostics when no packaged
        // runtime is present. Silero can then fail normally and the configured
        // Windows fallback remains available.
        return nested;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; CancelAll(); _queue.Writer.TryComplete(); _shutdown.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { } _shutdown.Dispose(); _router.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

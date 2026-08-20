namespace WhisperX.Atom.Voice.Host.Tts;

public sealed record TtsRouterSnapshot(
    string Engine,
    string Model,
    string Voice,
    string Culture,
    bool Ready,
    bool FallbackUsed,
    string? FallbackReason,
    int? ProcessId,
    long LastSynthesisMs,
    long LastModelLoadMs,
    int RestartCount);

public sealed class TtsEngineRouter : IAsyncDisposable
{
    private readonly SileroTtsEngine _silero;
    private readonly WindowsTtsEngine _windows;
    private readonly SpeechAudioPlayer _player;
    private bool _fallbackEnabled = true;
    private bool _useFallback;
    private bool _explicitWindows;
    private string? _fallbackReason;
    private DateTimeOffset? _fallbackUntilUtc;
    private int _sileroFailureCount;
    private TtsOptions _options = new();

    public TtsEngineRouter(SileroTtsEngine silero, WindowsTtsEngine windows, SpeechAudioPlayer player)
    {
        _silero = silero; _windows = windows; _player = player;
    }

    public bool IsReady => _useFallback ? _windows.IsReady : _silero.IsReady;
    public string EngineName => _useFallback ? _windows.EngineName : _silero.EngineName;
    public string ModelName => _useFallback ? _windows.ModelName : _silero.ModelName;
    public string VoiceName => _useFallback ? _windows.VoiceName : "aidar";
    public string VoiceCulture => _useFallback ? _windows.VoiceCulture : "ru-RU";
    public bool FallbackUsed => _useFallback && !_explicitWindows;
    public string? FallbackReason => _fallbackReason;
    public int? ProcessId => _useFallback ? null : _silero.ProcessId;
    public long LastSynthesisMs => _silero.LastSynthesisMs;
    public long LastModelLoadMs => _silero.LastModelLoadMs;
    public int RestartCount => _silero.RestartCount;
    public SpeechAudioPlayer Player => _player;

    public async Task<bool> ConfigureAsync(string? requestedWindowsVoice, int? rate, int? volume, bool fallbackEnabled,
        string? requestedSileroVoice = null, int? sampleRate = null, int? cpuThreads = null,
        CancellationToken cancellationToken = default, string? requestedEngine = null)
    {
        _fallbackEnabled = fallbackEnabled;
        _windows.Configure(requestedWindowsVoice, rate, volume);
        _options = new TtsOptions(
            Voice: string.IsNullOrWhiteSpace(requestedSileroVoice) ? "aidar" : requestedSileroVoice.Trim(),
            SampleRate: sampleRate ?? 48000,
            Rate: rate ?? 0,
            Volume: volume ?? 90,
            CpuThreads: cpuThreads ?? 4);
        _explicitWindows = string.Equals(requestedEngine, "WINDOWS", StringComparison.OrdinalIgnoreCase);
        _useFallback = _explicitWindows;
        _fallbackReason = null;
        _fallbackUntilUtc = null;
        _sileroFailureCount = 0;
        if (_useFallback) return _windows.IsReady;
        if (await _silero.EnsureReadyAsync(cancellationToken).ConfigureAwait(false)) return true;
        _fallbackReason = _silero.LastErrorCode ?? "TTS_HOST_START_FAILED";
        if (_fallbackEnabled && _windows.IsReady) { _useFallback = true; return true; }
        return false;
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken)
    {
        if (_useFallback && _fallbackEnabled && _fallbackUntilUtc is not null && DateTimeOffset.UtcNow >= _fallbackUntilUtc &&
            await _silero.EnsureReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            _useFallback = false;
            _fallbackReason = null;
            _fallbackUntilUtc = null;
            _sileroFailureCount = 0;
        }
        if (!_useFallback)
        {
            var sileroOptions = options with { Voice = _options.Voice, SampleRate = _options.SampleRate, CpuThreads = _options.CpuThreads };
            var result = await _silero.SynthesizeAsync(text, sileroOptions, cancellationToken).ConfigureAwait(false);
            if (result.Success) return result;

            // A single host/pipe failure is retried once before opening the
            // circuit. This keeps a transient TtsHost restart from producing a
            // silent response while still bounding repeated failures.
            _sileroFailureCount++;
            _fallbackReason = result.ErrorCode;
            if (_sileroFailureCount < 2)
            {
                try
                {
                    if (await _silero.RestartOnceAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var retry = await _silero.SynthesizeAsync(text, sileroOptions, cancellationToken).ConfigureAwait(false);
                        if (retry.Success) { _sileroFailureCount = 0; _fallbackReason = null; return retry; }
                        result = retry;
                        _fallbackReason = retry.ErrorCode;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                _sileroFailureCount++;
            }
            if (_fallbackEnabled && _windows.IsReady && _sileroFailureCount >= 2)
            {
                _useFallback = true;
                _fallbackUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                return (await _windows.SynthesizeAsync(text, options with { Rate = _options.Rate, Volume = _options.Volume }, cancellationToken).ConfigureAwait(false)) with { FallbackReason = _fallbackReason };
            }
            return result with { ErrorCode = "VOICE_TTS_UNAVAILABLE" };
        }
        var fallback = await _windows.SynthesizeAsync(text, options with { Rate = _options.Rate, Volume = _options.Volume }, cancellationToken).ConfigureAwait(false);
        return fallback with { FallbackReason = _fallbackReason };
    }

    public TtsRouterSnapshot Snapshot() => new(EngineName, ModelName, VoiceName, VoiceCulture, IsReady, _useFallback, _fallbackReason, ProcessId, LastSynthesisMs, LastModelLoadMs, RestartCount);
    public IReadOnlyList<string> GetRussianVoiceNames() => _windows.GetRussianVoiceNames();
    public TtsOptions CurrentOptions => _options;
    public IReadOnlyList<object> GetTtsVoices() =>
        new object[] { new { id = "aidar", displayName = "Айдар", engine = "SILERO" }, new { id = "eugene", displayName = "Евгений", engine = "SILERO" }, new { id = "baya", displayName = "Байя", engine = "SILERO" }, new { id = "kseniya", displayName = "Ксения", engine = "SILERO" }, new { id = "xenia", displayName = "Ксения (Xenia)", engine = "SILERO" } }
            .Concat(_windows.GetRussianVoiceNames().Select(name => (object)new { id = name, displayName = name, engine = "WINDOWS" })).ToArray();
    public ValueTask DisposeAsync() { return DisposeCoreAsync(); }
    private async ValueTask DisposeCoreAsync() { await _player.DisposeAsync().ConfigureAwait(false); await _silero.DisposeAsync().ConfigureAwait(false); await _windows.DisposeAsync().ConfigureAwait(false); }
}

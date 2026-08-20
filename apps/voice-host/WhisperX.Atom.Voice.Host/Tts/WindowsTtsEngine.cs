using System.Diagnostics;
using System.Globalization;
using System.Speech.Synthesis;

namespace WhisperX.Atom.Voice.Host.Tts;

public sealed class WindowsTtsEngine : ITtsEngine
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly object _gate = new();
    private readonly string _tempRoot;
    private string _requestedVoice = "Microsoft Irina";
    private string _voiceName = "Microsoft Irina";
    private int _rate;
    private int _volume = 90;
    private bool _ready;

    public WindowsTtsEngine(string? tempRoot = null)
    {
        _tempRoot = tempRoot ?? TtsPaths.GetWritableTempRoot();
        Directory.CreateDirectory(_tempRoot);
        _ready = SelectVoice(_requestedVoice);
    }

    public string EngineName => "WINDOWS";
    public string ModelName => "System.Speech";
    public string VoiceName => _voiceName;
    public string VoiceCulture
    {
        get { lock (_gate) { try { return _synthesizer.Voice.Culture.Name; } catch { return "ru-RU"; } } }
    }
    public bool IsReady => _ready;
    public int? ProcessId => null;
    public bool FallbackUsed => !string.Equals(_requestedVoice, _voiceName, StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> GetRussianVoiceNames()
    {
        lock (_gate)
        {
            try { return _synthesizer.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase)).Select(v => v.VoiceInfo.Name).OrderBy(v => v).ToArray(); }
            catch { return []; }
        }
    }

    public bool Configure(string? requestedVoice, int? rate, int? volume)
    {
        _requestedVoice = string.IsNullOrWhiteSpace(requestedVoice) ? "Microsoft Irina" : requestedVoice.Trim();
        _rate = Math.Clamp(rate ?? 0, -10, 10);
        _volume = Math.Clamp(volume ?? 90, 0, 100);
        _ready = SelectVoice(_requestedVoice);
        return _ready;
    }

    public Task<bool> EnsureReadyAsync(CancellationToken cancellationToken) => Task.FromResult(_ready);

    public async Task<TtsSynthesisResult> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken)
    {
        if (!_ready) return TtsSynthesisResult.Failed(EngineName, ModelName, _voiceName, "TTS_MODEL_LOAD_FAILED");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 600) return TtsSynthesisResult.Failed(EngineName, ModelName, _voiceName, "TTS_SYNTHESIS_FAILED");
        var path = TtsPaths.CreateTempWavPath(_tempRoot);
        var started = Stopwatch.GetTimestamp();
        var completion = new TaskCompletionSource<SpeakCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SpeakCompletedEventArgs>? handler = null;
        handler = (_, e) => completion.TrySetResult(e);
        try
        {
            lock (_gate)
            {
                _synthesizer.Rate = _rate;
                // Keep synthesis at full scale. The shared NAudio player is
                // the single owner of playback volume for both engines.
                _synthesizer.Volume = 100;
                _synthesizer.SetOutputToWaveFile(path);
                _synthesizer.SpeakCompleted += handler;
                _synthesizer.SpeakAsync(text);
            }
            using var registration = cancellationToken.Register(() => { try { _synthesizer.SpeakAsyncCancelAll(); } catch { } });
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            var synthesisMs = ElapsedMs(started);
            if (result.Cancelled || result.Error is not null) { TryDelete(path); return TtsSynthesisResult.Failed(EngineName, ModelName, _voiceName, "TTS_SYNTHESIS_FAILED", result.Error?.GetType().Name); }
            return new(true, EngineName, ModelName, _voiceName, path, ReadDurationMs(path), synthesisMs);
        }
        catch (OperationCanceledException) { TryDelete(path); return TtsSynthesisResult.Failed(EngineName, ModelName, _voiceName, "TTS_AUDIO_CANCELLED"); }
        catch { TryDelete(path); return TtsSynthesisResult.Failed(EngineName, ModelName, _voiceName, "TTS_SYNTHESIS_FAILED"); }
        finally
        {
            lock (_gate)
            {
                _synthesizer.SpeakCompleted -= handler;
                _synthesizer.SetOutputToNull();
            }
        }
    }

    private bool SelectVoice(string requested)
    {
        lock (_gate)
        {
            try
            {
                var voices = _synthesizer.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase)).Select(v => v.VoiceInfo).ToArray();
                var selected = voices.FirstOrDefault(v => string.Equals(v.Name, requested, StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault(v => string.Equals(v.Name, "Microsoft Irina", StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault(v => string.Equals(v.Name, "Microsoft Irina Desktop", StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault();
                if (selected is null) return false;
                _synthesizer.SelectVoice(selected.Name);
                _voiceName = selected.Name;
                return true;
            }
            catch { return false; }
        }
    }

    private static long ReadDurationMs(string path)
    {
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(path);
            return (long)reader.TotalTime.TotalMilliseconds;
        }
        catch { return 0; }
    }
    private static long ElapsedMs(long start) => (long)(Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
    internal static void TryDelete(string? path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { } }
    public ValueTask DisposeAsync() { lock (_gate) { try { _synthesizer.SpeakAsyncCancelAll(); } catch { } _synthesizer.Dispose(); } return ValueTask.CompletedTask; }
}

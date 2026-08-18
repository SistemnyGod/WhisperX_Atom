using System.Speech.Synthesis;
using System.Globalization;
using System.Threading.Channels;

namespace WhisperX.Atom.Voice.Host;

internal sealed record SpeechRequest(string Text, TaskCompletionSource Completion);

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
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = completed.Task;
        if (_disposed || QuietMode || !_russianVoiceAvailable || string.IsNullOrWhiteSpace(text))
        {
            completed.TrySetResult();
            return false;
        }
        lock (_gate)
        {
            Volatile.Write(ref _busy, 1);
            if (_queue.Writer.TryWrite(new SpeechRequest(text, completed))) return true;
            if (!_queue.Reader.TryPeek(out _)) Volatile.Write(ref _busy, 0);
            completed.TrySetResult();
            return false;
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    if (QuietMode) continue;
                    Volatile.Write(ref _speaking, 1);
                    await SpeakAsync(request.Text, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex) { Error?.Invoke(ex); }
                finally
                {
                    Volatile.Write(ref _speaking, 0);
                    request.Completion.TrySetResult();
                    lock (_gate)
                    {
                        if (!_queue.Reader.TryPeek(out _)) Volatile.Write(ref _busy, 0);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SpeakCompletedEventArgs>? handler = null;
        handler = (_, _) => completed.TrySetResult();
        lock (_gate)
        {
            _synthesizer.SpeakCompleted += handler;
            try { _synthesizer.SpeakAsync(text); }
            catch
            {
                _synthesizer.SpeakCompleted -= handler;
                throw;
            }
        }
        return AwaitPlaybackAsync(completed.Task, handler, cancellationToken);
    }

    private async Task AwaitPlaybackAsync(Task playback, EventHandler<SpeakCompletedEventArgs> handler, CancellationToken cancellationToken)
    {
        try { await playback.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { _synthesizer.SpeakAsyncCancelAll(); } catch (InvalidOperationException) { }
                }
                _synthesizer.SpeakCompleted -= handler;
            }
        }
    }

    private static int ReadIntEnvironment(string name, int fallback, int minimum, int maximum)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    public bool Test() => TryEnqueue("Голосовой помощник готов");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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

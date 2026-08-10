using System.Speech.Synthesis;
using System.Threading.Channels;
using NAudio.Wave;

namespace WhisperX.Atom.Voice.Host;

internal sealed record SpeechRequest(string Text, byte[]? Wav);

public sealed class SpeechResponder : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly Dictionary<string, byte[]> _responses = new(StringComparer.OrdinalIgnoreCase);
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

    public SpeechResponder()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "VoiceResponses");
        if (Directory.Exists(root))
            foreach (var file in Directory.EnumerateFiles(root, "*.wav"))
                _responses[Path.GetFileNameWithoutExtension(file)] = File.ReadAllBytes(file);
        _worker = Task.Run(ProcessAsync);
    }

    public bool QuietMode { get; set; }
    public bool IsSpeaking => Volatile.Read(ref _speaking) != 0;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public event Action<Exception>? Error;

    public bool TryEnqueue(string text)
    {
        if (_disposed || QuietMode || string.IsNullOrWhiteSpace(text)) return false;
        var key = ResponseKey(text);
        _responses.TryGetValue(key ?? string.Empty, out var wav);
        lock (_gate)
        {
            Volatile.Write(ref _busy, 1);
            if (_queue.Writer.TryWrite(new SpeechRequest(text, wav))) return true;
            if (!_queue.Reader.TryPeek(out _)) Volatile.Write(ref _busy, 0);
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
                    if (request.Wav is not null) await PlayWavAsync(request.Wav, _shutdown.Token);
                    else await Task.Run(() => { lock (_gate) _synthesizer.Speak(request.Text); }, _shutdown.Token);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex) { Error?.Invoke(ex); }
                finally
                {
                    Volatile.Write(ref _speaking, 0);
                    lock (_gate)
                    {
                        if (!_queue.Reader.TryPeek(out _)) Volatile.Write(ref _busy, 0);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private static async Task PlayWavAsync(byte[] wav, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(wav, writable: false);
        using var reader = new WaveFileReader(memory);
        using var output = new WaveOutEvent { DesiredLatency = 60 };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is null) completion.TrySetResult();
            else completion.TrySetException(args.Exception);
        };
        output.Init(reader);
        using var registration = cancellationToken.Register(output.Stop);
        output.Play();
        await completion.Task.WaitAsync(cancellationToken);
    }

    private static string? ResponseKey(string text)
    {
        if (text.Equals("Слушаю", StringComparison.OrdinalIgnoreCase)) return "listening";
        if (text.Equals("Запись начата", StringComparison.OrdinalIgnoreCase)) return "recording-started";
        if (text.Equals("Запись приостановлена", StringComparison.OrdinalIgnoreCase)) return "recording-paused";
        if (text.Equals("Запись продолжена", StringComparison.OrdinalIgnoreCase)) return "recording-resumed";
        if (text.Equals("Совещание завершено", StringComparison.OrdinalIgnoreCase)) return "recording-stopped";
        if (text.StartsWith("Метка", StringComparison.OrdinalIgnoreCase)) return "marker-added";
        if (text.StartsWith("Решение", StringComparison.OrdinalIgnoreCase)) return "decision-added";
        if (text.StartsWith("Поручение", StringComparison.OrdinalIgnoreCase)) return "action-added";
        if (text.StartsWith("Подтвердите остановку", StringComparison.OrdinalIgnoreCase)) return "confirmation-required";
        if (text.Contains("не распознана", StringComparison.OrdinalIgnoreCase) || text.Contains("недоступна", StringComparison.OrdinalIgnoreCase)) return "command-rejected";
        if (text.Contains("ошиб", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Не удалось", StringComparison.OrdinalIgnoreCase)) return "error";
        return null;
    }

    public bool Test() => TryEnqueue("Голосовой помощник готов");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        lock (_gate) _synthesizer.Dispose();
        _shutdown.Dispose();
    }
}
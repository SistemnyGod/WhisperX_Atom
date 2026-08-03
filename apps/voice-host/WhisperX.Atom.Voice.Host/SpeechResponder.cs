using System.Speech.Synthesis;

namespace WhisperX.Atom.Voice.Host;

public sealed class SpeechResponder : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly object _gate = new();
    public bool QuietMode { get; set; }

    public Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (QuietMode || string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _synthesizer.Speak(text);
        }
        return Task.CompletedTask;
    }

    public void Test() => SpeakAsync("Голосовой помощник готов", CancellationToken.None).GetAwaiter().GetResult();
    public void Dispose() => _synthesizer.Dispose();
}

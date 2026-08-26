namespace WhisperX.Atom.Voice.Host.Tts;

public interface ITtsEngine : IAsyncDisposable
{
    string EngineName { get; }
    string ModelName { get; }
    string VoiceName { get; }
    string VoiceCulture { get; }
    bool IsReady { get; }
    int? ProcessId { get; }
    Task<TtsSynthesisResult> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken);
    Task<bool> EnsureReadyAsync(CancellationToken cancellationToken);
}

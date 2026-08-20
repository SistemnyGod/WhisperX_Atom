namespace WhisperX.Atom.Voice.Host.Tts;

public sealed record TtsSynthesisResult(
    bool Success,
    string Engine,
    string Model,
    string Voice,
    string? AudioPath,
    long DurationMs,
    long SynthesisMs,
    string? ErrorCode = null,
    string? FallbackReason = null,
    bool IsStaticCache = false,
    long QueueWaitMs = 0)
{
    public static TtsSynthesisResult Failed(string engine, string model, string voice, string errorCode, string? reason = null)
        => new(false, engine, model, voice, null, 0, 0, errorCode, reason);
}

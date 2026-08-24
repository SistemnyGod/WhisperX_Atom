namespace WhisperX.Atom.Voice;

/// <summary>
/// Ephemeral utterance envelope shared by the Vosk arbiter and an optional
/// second-pass recognizer.  PCM is kept in memory only and must never be
/// serialized into IPC, telemetry or the voice ledger.
/// </summary>
public sealed record VoiceUtteranceEnvelope(
    string UtteranceId,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset? WakeAtUtc,
    DateTimeOffset? SpeechStartedAtUtc,
    DateTimeOffset? SpeechEndedAtUtc,
    byte[] Pcm16kMono,
    string VoskText,
    VoiceIntent VoskIntent,
    double VoskConfidence,
    string Route,
    long Sequence = 0,
    double DurationMs = 0,
    double? QueueWaitMs = null,
    double? PreRollMs = null,
    double? SpeechMs = null,
    double? PostRollMs = null,
    bool Truncated = false);

public enum VoiceRefinementState
{
    Disabled,
    Ready,
    Running,
    Agree,
    Disagree,
    NoSpeech,
    Unavailable,
    Timeout,
    Failed
}

public sealed record VoiceRefinementResult(
    VoiceRefinementState State,
    string? Text = null,
    string Provider = "whisper.cpp",
    string? Model = null,
    double? Confidence = null,
    double ProcessingMs = 0,
    string? ErrorCode = null,
    string? UtteranceId = null,
    long Sequence = 0,
    double? QueueWaitMs = null,
    string? AgreementCategory = null,
    bool WakeWordDetected = false);

public interface IVoiceAsrRefiner : IDisposable
{
    string Provider { get; }
    string Model { get; }
    bool IsAvailable { get; }
    Task<VoiceRefinementResult> RefineAsync(VoiceUtteranceEnvelope utterance, CancellationToken cancellationToken);
}

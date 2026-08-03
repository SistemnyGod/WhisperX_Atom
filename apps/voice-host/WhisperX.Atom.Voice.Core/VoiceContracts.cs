namespace WhisperX.Atom.Voice;

public enum VoiceHostState
{
    Disabled,
    Listening,
    WakeDetected,
    Capturing,
    Recognizing,
    Confirming,
    Executing,
    Responding,
    Cooldown,
    Error
}

public enum VoiceIntent
{
    Unknown,
    StartRecording,
    PauseRecording,
    ResumeRecording,
    AddMarker,
    MarkDecision,
    MarkActionItem,
    GetStatus,
    StopRecording,
    Confirm,
    Cancel,
    HistoryQuestion
}

public sealed record VoiceCommand(
    VoiceIntent Intent,
    string Text,
    double Confidence = 1.0,
    string? Parameter = null,
    DateTimeOffset? CreatedAt = null);

public sealed record VoiceHostSnapshot(
    VoiceHostState State,
    bool Enabled,
    bool PushToTalk,
    string? LastRecognizedText,
    string? LastResponse,
    string? PendingConfirmation,
    DateTimeOffset UpdatedAt);

public sealed record VoiceResponse(string Text, bool Speak = true, bool Success = true);

public sealed record VoiceTransition(VoiceHostState From, VoiceHostState To, DateTimeOffset At, string Reason);

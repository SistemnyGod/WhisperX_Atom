namespace WhisperX.Atom.Voice;

public enum VoiceHostState
{
    Disabled,
    Starting,
    Degraded,
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
    DateTimeOffset UpdatedAt,
    bool IsSpeaking = false,
    bool ModelReady = false,
    bool ModelIntegrityReady = false,
    bool NativeRuntimeReady = false,
    bool MicrophoneReady = false,
    bool RecorderPipeReady = false,
    string? RecorderPipeError = null,
    string Sensitivity = "balanced",
    string? LastIntent = null,
    string? LastErrorCode = null,
    double? WakeLatencyMs = null,
    double? IntentLatencyMs = null,
    double? RecorderAckLatencyMs = null,
    double? TotalLatencyMs = null,
    double? LastCommandLatencyMs = null,
    int AudioQueueDepth = 0,
    long AudioQueueDrops = 0,
    string? EffectiveMicrophoneName = null,
    double? MicrophonePeak = null,
    DateTimeOffset? LastAudioAtUtc = null,
    double? MicrophoneRms = null,
    bool MicrophoneClipping = false,
    string? AudioSignalState = null,
    long AudioTelemetrySequence = 0,
    string? BuildIdentity = null,
    string? WakeWordMode = null,
    int? ProcessId = null,
    string? LastTraceId = null);

public sealed record VoiceResponse(string Text, bool Speak = true, bool Success = true, string? LocalSessionId = null);

public sealed record VoiceTransition(VoiceHostState From, VoiceHostState To, DateTimeOffset At, string Reason);

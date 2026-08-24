namespace WhisperX.Atom.Voice;

public static class VoiceIpcCapabilities
{
    public const string VoiceGainControl = "VOICE_GAIN_CONTROL_V1";
}

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
    // Canonical conversational intent. Keep the old member as an alias so
    // older clients/tests that deserialize or compare HistoryQuestion remain
    // compatible with the same numeric value.
    AssistantQuery,
    HistoryQuestion = AssistantQuery,
    StopSpeaking,
    // Append-only: keep existing numeric values stable for IPC consumers.
    Farewell,
    // Conversational follow-ups are appended so the existing IPC enum values
    // remain stable. They are routed through the same Assistant conversation
    // as a normal question; they never mutate Recorder state.
    RepeatAnswer,
    ShortenAnswer,
    ElaborateAnswer,
    PreviousQuestion,
    // Local, deterministic status fast paths. Keep appended for IPC
    // compatibility; they never create an Assistant query.
    GetServerStatus,
    GetPipelineStatus,
    GetStorageStatus,
    // Append-only summary controls. Existing numeric intent values remain
    // stable for Voice IPC consumers.
    GenerateSummary,
    GetSummaryStatus
}

public sealed record VoiceCommand(
    VoiceIntent Intent,
    string Text,
    double Confidence = 1.0,
    string? Parameter = null,
    DateTimeOffset? CreatedAt = null,
    string? CommandId = null);

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
    string? LastTraceId = null,
    DateTimeOffset? HeartbeatAtUtc = null,
    string? RequestedMicrophoneDeviceId = null,
    string? EffectiveMicrophoneDeviceId = null,
    string? LastCommandId = null,
    int RestartCount = 0,
    string? RestartState = null,
    string? MicrophoneErrorDetail = null,
    string? RequestedVoiceName = null,
    string? EffectiveVoiceName = null,
    string? EffectiveVoiceCulture = null,
    bool VoiceFallbackUsed = false,
    int SpeechQueueDepth = 0,
    long SpeechQueueDrops = 0,
    string LiveAudioMode = "MIC_FALLBACK",
    string LiveRoomTrackState = "WAITING",
    string LiveSystemTrackState = "WAITING",
    long LiveAudioDrops = 0,
    long LiveSegmentsPublished = 0,
    long LiveSegmentsSuppressed = 0,
    string TtsEngine = "SILERO",
    string TtsModel = "v5_5_ru",
    bool TtsReady = false,
    string? TtsVoice = null,
    string? TtsCulture = null,
    int? TtsSampleRate = 48000,
    int? TtsCpuThreads = 4,
    int? TtsHostProcessId = null,
    long TtsModelLoadMs = 0,
    long TtsLastSynthesisMs = 0,
    bool TtsFallbackUsed = false,
    string? TtsFallbackReason = null,
    int TtsRestartCount = 0,
    double? VoiceNoiseFloorDb = null,
    double? VoiceVadThresholdDb = null,
    DateTimeOffset? LastWakeAtUtc = null,
    DateTimeOffset? LastUtteranceAtUtc = null,
    DateTimeOffset? LastAssistantAcceptedAtUtc = null,
    DateTimeOffset? LastTtsStartedAtUtc = null,
    DateTimeOffset? LastTtsFinishedAtUtc = null,
    double? LastTtsQueueWaitMs = null,
    double? LastTtsSynthesisMs = null,
    double? LastTtsPlaybackMs = null,
    string? LastAssistantQueryId = null,
    string? LastRecognizer = null,
    string? LastRecognitionRoute = null,
    double? LastRecognitionConfidence = null,
    string? LastNormalizationReason = null,
    int VoiceProcessingGainDb = 0,
    string? VoiceRefinerState = null,
    string? VoiceRefinerProvider = null,
    string? VoiceRefinerModel = null,
    double? VoiceRefinerProcessingMs = null,
    double? VoiceRefinerQueueWaitMs = null,
    string? VoiceRefinerAgreement = null,
    string? VoiceRefinerError = null,
    IReadOnlyList<string>? Capabilities = null);

public sealed record VoiceResponse(
    string Text,
    bool Speak = true,
    bool Success = true,
    string? LocalSessionId = null,
    string? CommandId = null,
    string? TraceId = null,
    string? QueryId = null,
    string? ResponseId = null,
    string? PlaybackState = null,
    bool AcceptedForPlayback = false,
    string? AnswerStatus = null);

public sealed record VoiceTransition(VoiceHostState From, VoiceHostState To, DateTimeOffset At, string Reason);

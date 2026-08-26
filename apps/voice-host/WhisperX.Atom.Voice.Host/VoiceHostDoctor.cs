namespace WhisperX.Atom.Voice.Host;

public sealed record VoiceHostDoctorResult(
    string State,
    bool ModelReady,
    bool ModelIntegrityReady,
    bool NativeRuntimeReady,
    bool MicrophoneReady,
    bool RecorderPipeReady,
    string? ModelError,
    string? MicrophoneError,
    string? RecorderPipeError,
    string? ErrorCode);
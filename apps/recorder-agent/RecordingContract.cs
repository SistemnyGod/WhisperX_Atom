namespace WhisperX.Atom.Recorder;

public static class RecordingContract
{
    public const int ChunkDurationSeconds = 10;
    public const int MicrophoneSampleRate = 48_000;
    public const int AgentCommandSampleRate = 16_000;

    public static readonly string[] TrackTypes = ["room-microphone", "system-audio"];

    public static string ChunkEndpoint(string sessionId, string trackId, int sequence) => $"/api/v1/recording-sessions/{sessionId}/tracks/{trackId}/chunks/{sequence}";
}

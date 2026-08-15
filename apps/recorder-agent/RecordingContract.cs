namespace WhisperX.Atom.Recorder;

public static class RecordingContract
{
    // Kept at 10 seconds as the compatibility interval for legacy spool
    // rows. New sessions use the configurable raw segment size below.
    public const int ChunkDurationSeconds = 10;
    public const int DefaultChunkDurationSeconds = 30;
    public const int MinimumChunkDurationSeconds = 10;
    public const int MaximumChunkDurationSeconds = 60;
    public const int MicrophoneSampleRate = 48_000;
    public const int AgentCommandSampleRate = 16_000;

    public static readonly string[] TrackTypes = ["room-microphone", "system-audio"];

    public static int GetChunkDurationSeconds()
    {
        var configured = Environment.GetEnvironmentVariable("ATOM_AUDIO_CHUNK_SECONDS");
        return int.TryParse(configured, out var seconds)
            ? Math.Clamp(seconds, MinimumChunkDurationSeconds, MaximumChunkDurationSeconds)
            : DefaultChunkDurationSeconds;
    }

    public static string ChunkEndpoint(string sessionId, string trackId, int sequence) => $"/api/v1/recording-sessions/{sessionId}/tracks/{trackId}/chunks/{sequence}";
}

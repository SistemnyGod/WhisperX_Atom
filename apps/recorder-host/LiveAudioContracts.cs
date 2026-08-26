using System.Text.Json.Serialization;

namespace WhisperX.Atom.Recorder.Host;

internal static class LiveAudioIpc
{
    public const string PipeName = "WhisperXAtomLiveAudioV1";
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int QueueCapacityPerTrack = 64;
}

internal sealed record LiveAudioFrame(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("meetingId")] Guid? MeetingId,
    [property: JsonPropertyName("trackId")] string TrackId,
    [property: JsonPropertyName("trackType")] string TrackType,
    [property: JsonPropertyName("channelRole")] string ChannelRole,
    [property: JsonPropertyName("startMs")] long StartMs,
    [property: JsonPropertyName("durationMs")] int DurationMs,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("sampleRate")] int SampleRate,
    [property: JsonPropertyName("channels")] int Channels,
    [property: JsonPropertyName("pcm16Base64")] string Pcm16Base64,
    [property: JsonPropertyName("gap")] bool Gap = false,
    [property: JsonPropertyName("droppedBefore")] long DroppedBefore = 0,
    // Explicit alias for the recorder-owned session identity.  `sessionId`
    // remains for early LiveAudio v1 clients, while new clients can use the
    // unambiguous localSessionId name when correlating provisional segments.
    [property: JsonPropertyName("localSessionId")] string? LocalSessionId = null);

internal sealed record LiveAudioSession(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("meetingId")] Guid? MeetingId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("capturedAtUtc")] DateTimeOffset CapturedAtUtc,
    [property: JsonPropertyName("systemAudioEnabled")] bool SystemAudioEnabled = false,
    [property: JsonPropertyName("paused")] bool Paused = false,
    [property: JsonPropertyName("localSessionId")] string? LocalSessionId = null);

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Immutable input used by the local durability gate.  The validator knows
/// nothing about SQLite or the filesystem; it only checks the session timeline
/// and the format metadata supplied by the spool reader.
/// </summary>
internal sealed record RecordingTimelineChunk(
    string TrackId,
    int Sequence,
    string TrackType,
    string Encoding,
    long StartSample,
    long SampleCount,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    bool IsDurable,
    string? ExpectedTrackType = null,
    int? ExpectedSampleRate = null,
    int? ExpectedChannels = null,
    string? ExpectedEncoding = null,
    int? ExpectedBitsPerSample = null);

/// <summary>
/// Pure session-level durability policy shared by the recorder pipelines.
/// A session is ready only when every chunk in every track is contiguous,
/// format-compatible and backed by durable bytes (raw PCM or encoded FLAC).
/// </summary>
internal static class RecordingTimelineValidator
{
    public static string? Validate(IReadOnlyList<RecordingTimelineChunk> chunks)
    {
        if (chunks.Count == 0) return null;

        foreach (var group in chunks.GroupBy(chunk => chunk.TrackId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(chunk => chunk.Sequence).ToArray();
            var first = ordered[0];
            if (first.Sequence != 0) return "SESSION_TIMELINE_GAP";

            long expectedStart = first.StartSample;
            var sampleRate = first.ExpectedSampleRate ?? first.SampleRate;
            var channels = first.ExpectedChannels ?? first.Channels;
            var bits = first.ExpectedBitsPerSample ?? first.BitsPerSample;
            var encoding = first.ExpectedEncoding ?? first.Encoding;
            var trackType = first.ExpectedTrackType ?? first.TrackType;

            for (var index = 0; index < ordered.Length; index++)
            {
                var chunk = ordered[index];
                if (chunk.Sequence != index) return "SESSION_TIMELINE_GAP";
                if (chunk.SampleCount <= 0 || chunk.StartSample != expectedStart)
                    return chunk.StartSample < expectedStart
                        ? "SESSION_TIMELINE_OVERLAP"
                        : "SESSION_TIMELINE_GAP";

                if (chunk.SampleRate != sampleRate
                    || chunk.Channels != channels
                    || chunk.BitsPerSample != bits
                    || !string.Equals(chunk.Encoding, encoding, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(chunk.TrackType, trackType, StringComparison.OrdinalIgnoreCase))
                    return "SESSION_TRACK_FORMAT_MISMATCH";

                if (!chunk.IsDurable) return "SESSION_CHUNK_NOT_DURABLE";
                if (chunk.StartSample > long.MaxValue - chunk.SampleCount)
                    return "SESSION_TIMELINE_OVERFLOW";
                expectedStart = chunk.StartSample + chunk.SampleCount;
            }
        }

        return null;
    }
}

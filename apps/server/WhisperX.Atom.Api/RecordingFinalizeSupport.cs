using System.Text.Json;
using Npgsql;

public sealed class RecordingTrackAssembly(Guid id, string trackType, int sampleRate, int channels)
{
    public Guid Id { get; } = id;
    public string TrackType { get; } = trackType;
    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;
    public List<RecordingChunkAssembly> Chunks { get; } = [];
}

public sealed record RecordingChunkAssembly(int Sequence, string StorageKey, long StartSample, long SampleCount, long SizeBytes, string Sha256);
public sealed record RecordingTrackExpectation(int? ExpectedChunkCount, long? ExpectedTotalSamples, long? ExpectedStartSample);
public sealed record RecordingTimelineMismatch(Guid TrackId, string Reason, int? Sequence, long? Expected, long? Actual);

public static class RecordingFinalizeSupport
{
    public static async Task<Dictionary<Guid, RecordingTrackAssembly>> LoadTracksAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId)
    {
        var tracks = new Dictionary<Guid, RecordingTrackAssembly>();
        await using var command = new NpgsqlCommand("""
            SELECT t.id,t.track_type,t.sample_rate,t.channels,
                   c.sequence,c.storage_key,c.start_sample,c.sample_count,c.size_bytes,c.sha256
            FROM recording_tracks t
            LEFT JOIN recording_chunks c ON c.track_id=t.id AND c.status='CONFIRMED'
            WHERE t.session_id=@session
            ORDER BY t.id,c.sequence
            """, connection, transaction);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var trackId = reader.GetGuid(0);
            if (!tracks.TryGetValue(trackId, out var track))
            {
                track = new RecordingTrackAssembly(trackId, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3));
                tracks[trackId] = track;
            }

            if (!reader.IsDBNull(4))
                track.Chunks.Add(new RecordingChunkAssembly(reader.GetInt32(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetString(9)));
        }

        return tracks;
    }

    public static Dictionary<Guid, RecordingTrackExpectation> ReadExpectedTrackExpectations(JsonDocument? manifest, IEnumerable<Guid> trackIds)
    {
        var result = new Dictionary<Guid, RecordingTrackExpectation>();
        if (manifest is null || manifest.RootElement.ValueKind != JsonValueKind.Object || !manifest.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            return result;

        var known = trackIds.ToHashSet();
        foreach (var item in tracks.EnumerateArray())
        {
            if (!item.TryGetProperty("track_id", out var idValue) || !Guid.TryParse(idValue.GetString(), out var trackId) || !known.Contains(trackId))
                continue;
            var countValue = item.TryGetProperty("expected_chunk_count", out var expected) ? expected : item.TryGetProperty("chunk_count", out var legacy) ? legacy : default;
            int? count = countValue.ValueKind == JsonValueKind.Number && countValue.TryGetInt32(out var parsedCount) && parsedCount > 0
                ? Math.Min(parsedCount, 100_000)
                : null;
            var totalSamples = ReadNonNegativeLong(item, "total_samples");
            var startSample = ReadNonNegativeLong(item, "start_sample");
            if (count is not null || totalSamples is not null || startSample is not null)
                result[trackId] = new RecordingTrackExpectation(count, totalSamples, startSample);
        }
        return result;
    }

    public static Dictionary<Guid, int> ReadExpectedChunkCounts(JsonDocument? manifest, IEnumerable<Guid> trackIds)
        => ReadExpectedTrackExpectations(manifest, trackIds)
            .Where(item => item.Value.ExpectedChunkCount is not null)
            .ToDictionary(item => item.Key, item => item.Value.ExpectedChunkCount!.Value);

    public static IReadOnlyList<RecordingTimelineMismatch> FindTimelineMismatches(
        Dictionary<Guid, RecordingTrackAssembly> tracks,
        Dictionary<Guid, RecordingTrackExpectation> expected)
    {
        var mismatches = new List<RecordingTimelineMismatch>();
        foreach (var track in tracks.Values)
        {
            var ordered = track.Chunks.OrderBy(chunk => chunk.Sequence).ToArray();
            if (ordered.Length == 0) continue;

            expected.TryGetValue(track.Id, out var expectation);
            var actualStart = ordered[0].StartSample;
            if (expectation?.ExpectedStartSample is long expectedStart && actualStart != expectedStart)
                mismatches.Add(new RecordingTimelineMismatch(track.Id, "start_sample_mismatch", ordered[0].Sequence, expectedStart, actualStart));

            long? previousEnd = null;
            foreach (var chunk in ordered)
            {
                if (chunk.SampleCount <= 0 || chunk.StartSample < 0 || chunk.StartSample > long.MaxValue - chunk.SampleCount)
                {
                    mismatches.Add(new RecordingTimelineMismatch(track.Id, "invalid_sample_range", chunk.Sequence, null, chunk.StartSample));
                    continue;
                }

                if (previousEnd is long expectedStartForChunk && chunk.StartSample != expectedStartForChunk)
                    mismatches.Add(new RecordingTimelineMismatch(track.Id, "sample_gap", chunk.Sequence, expectedStartForChunk, chunk.StartSample));
                previousEnd = chunk.StartSample + chunk.SampleCount;
            }

            var actualTotal = ordered.Sum(chunk => chunk.SampleCount);
            if (expectation?.ExpectedTotalSamples is long expectedTotal && actualTotal != expectedTotal)
                mismatches.Add(new RecordingTimelineMismatch(track.Id, "total_samples_mismatch", null, expectedTotal, actualTotal));
        }

        return mismatches;
    }

    public static async Task<IReadOnlyList<MissingRecordingChunks>> FindMissingAsync(
        Dictionary<Guid, RecordingTrackAssembly> tracks,
        Dictionary<Guid, int> expected,
        bool verifyHashes = false)
    {
        var missing = new List<MissingRecordingChunks>();
        foreach (var track in tracks.Values)
        {
            var expectedCount = expected.TryGetValue(track.Id, out var count) ? count : track.Chunks.Count == 0 ? 0 : track.Chunks.Max(chunk => chunk.Sequence) + 1;
            var present = track.Chunks.Select(chunk => chunk.Sequence).ToHashSet();
            var gaps = Enumerable.Range(0, Math.Max(expectedCount, 0)).Where(sequence => !present.Contains(sequence)).ToArray();
            if (expectedCount <= 0 || gaps.Length > 0)
                missing.Add(new MissingRecordingChunks(track.Id, gaps.Length > 0 ? gaps : [0]));

            foreach (var chunk in track.Chunks)
            {
                try
                {
                    var path = StorageHelpers.StoragePath(chunk.StorageKey);
                    if (!File.Exists(path) || new FileInfo(path).Length != chunk.SizeBytes)
                    {
                        missing.Add(new MissingRecordingChunks(track.Id, [chunk.Sequence]));
                        continue;
                    }
                    // Upload already computed and verified this SHA before the
                    // immutable storage move.  The normal finalize path only
                    // needs existence/size checks; callers doing an explicit
                    // integrity-recovery pass can request the full re-hash.
                    if (verifyHashes
                        && !string.Equals(await StorageHelpers.ComputeSha256Async(path), chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                        missing.Add(new MissingRecordingChunks(track.Id, [chunk.Sequence]));
                }
                catch (IOException)
                {
                    missing.Add(new MissingRecordingChunks(track.Id, [chunk.Sequence]));
                }
                catch (UnauthorizedAccessException)
                {
                    missing.Add(new MissingRecordingChunks(track.Id, [chunk.Sequence]));
                }
                catch (InvalidOperationException)
                {
                    // A stale/corrupt storage key must be recoverable through
                    // the normal chunk resend path, not become an opaque 500.
                    missing.Add(new MissingRecordingChunks(track.Id, [chunk.Sequence]));
                }
            }
        }

        return missing
            .GroupBy(item => item.TrackId)
            .Select(group => new MissingRecordingChunks(group.Key, group.SelectMany(item => item.Sequences).Distinct().OrderBy(sequence => sequence).ToArray()))
            .ToArray();
    }

    public static JsonDocument BuildNormalizedManifest(Guid sessionId, Dictionary<Guid, RecordingTrackAssembly> tracks, Dictionary<Guid, int> expected, Dictionary<Guid, RecordingTrackExpectation>? expectations = null)
    {
        var payload = new
        {
            session_id = sessionId,
            inferred = (expectations ?? expected.ToDictionary(item => item.Key, item => new RecordingTrackExpectation(item.Value, null, null))).Count != tracks.Count,
            tracks = tracks.Values.Select(track => new
            {
                track_id = track.Id,
                track_type = track.TrackType,
                sample_rate = track.SampleRate,
                channels = track.Channels,
                expected_chunk_count = expected.TryGetValue(track.Id, out var count) ? count : track.Chunks.Max(chunk => chunk.Sequence) + 1,
                start_sample = track.Chunks.Count == 0 ? 0 : track.Chunks.Min(chunk => chunk.StartSample),
                total_samples = track.Chunks.Sum(chunk => chunk.SampleCount)
            }).ToArray()
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload));
    }

    private static long? ReadNonNegativeLong(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed) || parsed < 0)
            return null;
        return parsed;
    }
}

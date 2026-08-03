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

    public static Dictionary<Guid, int> ReadExpectedChunkCounts(JsonDocument? manifest, IEnumerable<Guid> trackIds)
    {
        var result = new Dictionary<Guid, int>();
        if (manifest is null || manifest.RootElement.ValueKind != JsonValueKind.Object || !manifest.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            return result;

        var known = trackIds.ToHashSet();
        foreach (var item in tracks.EnumerateArray())
        {
            if (!item.TryGetProperty("track_id", out var idValue) || !Guid.TryParse(idValue.GetString(), out var trackId) || !known.Contains(trackId))
                continue;
            var countValue = item.TryGetProperty("expected_chunk_count", out var expected) ? expected : item.TryGetProperty("chunk_count", out var legacy) ? legacy : default;
            if (countValue.ValueKind == JsonValueKind.Number && countValue.TryGetInt32(out var count) && count > 0)
                result[trackId] = Math.Min(count, 100_000);
        }
        return result;
    }

    public static IReadOnlyList<MissingRecordingChunks> FindMissing(Dictionary<Guid, RecordingTrackAssembly> tracks, Dictionary<Guid, int> expected)
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
                var path = StorageHelpers.StoragePath(chunk.StorageKey);
                if (!File.Exists(path) || new FileInfo(path).Length != chunk.SizeBytes)
                    missing.Add(new MissingRecordingChunks(track.Id, [chunk.Sequence]));
            }
        }

        return missing
            .GroupBy(item => item.TrackId)
            .Select(group => new MissingRecordingChunks(group.Key, group.SelectMany(item => item.Sequences).Distinct().OrderBy(sequence => sequence).ToArray()))
            .ToArray();
    }

    public static JsonDocument BuildNormalizedManifest(Guid sessionId, Dictionary<Guid, RecordingTrackAssembly> tracks, Dictionary<Guid, int> expected)
    {
        var payload = new
        {
            session_id = sessionId,
            inferred = expected.Count != tracks.Count,
            tracks = tracks.Values.Select(track => new
            {
                track_id = track.Id,
                track_type = track.TrackType,
                sample_rate = track.SampleRate,
                channels = track.Channels,
                expected_chunk_count = expected.TryGetValue(track.Id, out var count) ? count : track.Chunks.Max(chunk => chunk.Sequence) + 1
            }).ToArray()
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload));
    }
}

using Microsoft.Data.Sqlite;

namespace WhisperX.Atom.Recorder;

public sealed record RecordingChunk(
    string Id,
    string SessionId,
    string TrackId,
    int Sequence,
    string LocalPath,
    long StartSample,
    long SampleCount,
    int SampleRate,
    int Channels,
    long SizeBytes,
    string Sha256,
    string Status,
    int Attempts);

public sealed class SpoolStore
{
    private readonly string _connectionString;

    public SpoolStore(string root)
    {
        Directory.CreateDirectory(root);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "agent.db"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS recording_sessions(id TEXT PRIMARY KEY, meeting_id TEXT, state TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT, total_samples INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS recording_chunks(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, track_id TEXT NOT NULL, sequence INTEGER NOT NULL, local_path TEXT NOT NULL, start_sample INTEGER NOT NULL, sample_count INTEGER NOT NULL, sample_rate INTEGER NOT NULL, channels INTEGER NOT NULL, size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL, status TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, confirmed_at TEXT, UNIQUE(track_id, sequence));
            CREATE TABLE IF NOT EXISTS recording_events(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, event_type TEXT NOT NULL, media_time_ms INTEGER, payload_json TEXT NOT NULL, created_at TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateSessionAsync(string sessionId, Guid? meetingId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO recording_sessions(id,meeting_id,state,started_at) VALUES($id,$meeting,'RECORDING',$started)";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$meeting", (object?)meetingId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetSessionStateAsync(string sessionId, string state, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET state=$state, finished_at=CASE WHEN $state IN ('IDLE','FINALIZING','FAILED') THEN COALESCE(finished_at,$finished) ELSE finished_at END WHERE id=$id";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$finished", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddEventAsync(string sessionId, string eventType, long? mediaTimeMs = null, string payloadJson = "{}", CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO recording_events(id,session_id,event_type,media_time_ms,payload_json,created_at) VALUES($id,$session,$type,$media,$payload,$created)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$media", (object?)mediaTimeMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertChunkAsync(RecordingChunk chunk, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_chunks(id,session_id,track_id,sequence,local_path,start_sample,sample_count,sample_rate,channels,size_bytes,sha256,status,attempts,created_at)
            VALUES($id,$session,$track,$sequence,$path,$start,$count,$rate,$channels,$size,$sha,$status,$attempts,$created)
            ON CONFLICT(track_id,sequence) DO UPDATE SET local_path=excluded.local_path,size_bytes=excluded.size_bytes,sha256=excluded.sha256,status=excluded.status;
            """;
        command.Parameters.AddWithValue("$id", chunk.Id); command.Parameters.AddWithValue("$session", chunk.SessionId); command.Parameters.AddWithValue("$track", chunk.TrackId); command.Parameters.AddWithValue("$sequence", chunk.Sequence); command.Parameters.AddWithValue("$path", chunk.LocalPath); command.Parameters.AddWithValue("$start", chunk.StartSample); command.Parameters.AddWithValue("$count", chunk.SampleCount); command.Parameters.AddWithValue("$rate", chunk.SampleRate); command.Parameters.AddWithValue("$channels", chunk.Channels); command.Parameters.AddWithValue("$size", chunk.SizeBytes); command.Parameters.AddWithValue("$sha", chunk.Sha256); command.Parameters.AddWithValue("$status", chunk.Status); command.Parameters.AddWithValue("$attempts", chunk.Attempts); command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecordingChunk>> PendingChunksAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,track_id,sequence,local_path,start_sample,sample_count,sample_rate,channels,size_bytes,sha256,status,attempts FROM recording_chunks WHERE status <> 'CONFIRMED' ORDER BY session_id,track_id,sequence LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<RecordingChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new RecordingChunk(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt64(9), reader.GetString(10), reader.GetString(11), reader.GetInt32(12)));
        return result;
    }

    public async Task MarkConfirmedAsync(string trackId, int sequence, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_chunks SET status='CONFIRMED',confirmed_at=$confirmed WHERE track_id=$track AND sequence=$sequence";
        command.Parameters.AddWithValue("$confirmed", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$track", trackId); command.Parameters.AddWithValue("$sequence", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

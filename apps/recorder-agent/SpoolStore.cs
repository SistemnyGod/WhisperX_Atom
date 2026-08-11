using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public sealed record RecordingTrackInfo(string TrackId, string TrackType, int SampleRate, int Channels);

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
    string TrackType,
    long SizeBytes,
    string Sha256,
    string Status,
    int Attempts);

public sealed record RawRecordingChunk(
    string Id,
    string SessionId,
    string TrackId,
    int Sequence,
    string RawPath,
    string OutputPath,
    long StartSample,
    long SampleCount,
    int SampleRate,
    int Channels,
    string TrackType,
    string Encoding,
    int BitsPerSample,
    string Status,
    long RawSizeBytes,
    string? RawSha256,
    string? Error);

public sealed record RawChunkBacklog(int Pending, int Writing, int Encoding, int Failed, long Bytes);

public sealed record ServerBinding(string LocalSessionId, string LocalTrackId, Guid ServerSessionId, Guid ServerTrackId);
public sealed record RecordingManifestTrack(Guid ServerTrackId, string TrackType, int SampleRate, int Channels, int ExpectedChunkCount, long TotalSamples, long StartSample = 0);
public sealed record PendingCommandResult(Guid CommandId, long Cursor, string Status, JsonElement Result);
public sealed record RecordingEventRow(string Id, string SessionId, string EventType, long? MediaTimeMs, string PayloadJson, DateTimeOffset CreatedAt);
public sealed record RecordingSessionInfo(
    string SessionId,
    Guid? MeetingId,
    string? Title,
    DateTimeOffset? StartedAt,
    string State = "UNKNOWN",
    string LocalFinalizeState = "PENDING",
    string DeliveryState = "NOT_STARTED",
    string? ArchivePath = null,
    string? ErrorCode = null,
    string? ErrorDetail = null,
    int RetryCount = 0,
    DateTimeOffset? NextRetryAtUtc = null);
public sealed record RecordingArchiveChunk(string TrackId, string TrackType, int Sequence, string LocalPath, long StartSample, long SampleCount, int SampleRate, int Channels, long SizeBytes, string Sha256);

public sealed record RecordingManifest(Guid ServerSessionId, IReadOnlyList<RecordingManifestTrack> Tracks);

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
            CREATE TABLE IF NOT EXISTS recording_sessions(id TEXT PRIMARY KEY, meeting_id TEXT, title TEXT, state TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT, total_samples INTEGER NOT NULL DEFAULT 0, local_finalize_state TEXT NOT NULL DEFAULT 'PENDING', delivery_state TEXT NOT NULL DEFAULT 'NOT_STARTED', archive_path TEXT, last_error_code TEXT, last_error_detail TEXT, retry_count INTEGER NOT NULL DEFAULT 0, next_retry_at TEXT);
            CREATE TABLE IF NOT EXISTS recording_chunks(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, track_id TEXT NOT NULL, sequence INTEGER NOT NULL, local_path TEXT NOT NULL, start_sample INTEGER NOT NULL, sample_count INTEGER NOT NULL, sample_rate INTEGER NOT NULL, channels INTEGER NOT NULL, track_type TEXT NOT NULL DEFAULT 'room-microphone', size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL, status TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, confirmed_at TEXT, UNIQUE(track_id, sequence));
            CREATE TABLE IF NOT EXISTS recording_events(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, event_type TEXT NOT NULL, media_time_ms INTEGER, payload_json TEXT NOT NULL, created_at TEXT NOT NULL, synced_at TEXT);
            CREATE TABLE IF NOT EXISTS recording_raw_chunks(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, track_id TEXT NOT NULL, sequence INTEGER NOT NULL, raw_path TEXT NOT NULL, output_path TEXT NOT NULL, start_sample INTEGER NOT NULL, sample_count INTEGER NOT NULL, sample_rate INTEGER NOT NULL, channels INTEGER NOT NULL, track_type TEXT NOT NULL, encoding TEXT NOT NULL, bits_per_sample INTEGER NOT NULL, status TEXT NOT NULL, raw_size_bytes INTEGER NOT NULL DEFAULT 0, raw_sha256 TEXT, error TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, UNIQUE(track_id, sequence));
            CREATE TABLE IF NOT EXISTS server_bindings(local_session_id TEXT NOT NULL, local_track_id TEXT NOT NULL, server_session_id TEXT NOT NULL, server_track_id TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(local_session_id, local_track_id));
            CREATE TABLE IF NOT EXISTS agent_state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS agent_command_results(command_id TEXT PRIMARY KEY, cursor INTEGER NOT NULL, status TEXT NOT NULL, result_json TEXT NOT NULL, created_at TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var migration = connection.CreateCommand();
        migration.CommandText = "ALTER TABLE recording_chunks ADD COLUMN track_type TEXT NOT NULL DEFAULT 'room-microphone'";
        try
        {
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
        await using var eventMigration = connection.CreateCommand();
        eventMigration.CommandText = "ALTER TABLE recording_events ADD COLUMN synced_at TEXT";
        try { await eventMigration.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
        await using var titleMigration = connection.CreateCommand();
        titleMigration.CommandText = "ALTER TABLE recording_sessions ADD COLUMN title TEXT";
        try { await titleMigration.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }

        foreach (var column in new[]
        {
            "ALTER TABLE recording_sessions ADD COLUMN local_finalize_state TEXT NOT NULL DEFAULT 'PENDING'",
            "ALTER TABLE recording_sessions ADD COLUMN delivery_state TEXT NOT NULL DEFAULT 'NOT_STARTED'",
            "ALTER TABLE recording_sessions ADD COLUMN archive_path TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN last_error_code TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN last_error_detail TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE recording_sessions ADD COLUMN next_retry_at TEXT"
        })
        {
            await using var stateMigration = connection.CreateCommand();
            stateMigration.CommandText = column;
            try { await stateMigration.ExecuteNonQueryAsync(cancellationToken); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
        }

    }

    public async Task CreateSessionAsync(string sessionId, Guid? meetingId = null, string? title = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO recording_sessions(id,meeting_id,title,state,started_at,local_finalize_state,delivery_state) VALUES($id,$meeting,$title,'RECORDING',$started,'PENDING','NOT_STARTED')";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$meeting", (object?)meetingId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetMeetingIdAsync(string sessionId, Guid meetingId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET meeting_id=$meeting WHERE id=$session";
        command.Parameters.AddWithValue("$meeting", meetingId.ToString()); command.Parameters.AddWithValue("$session", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetTitleAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM recording_sessions WHERE id=$session";
        command.Parameters.AddWithValue("$session", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string title ? title : null;
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

    public async Task SetFinalizationStateAsync(
        string sessionId,
        string? localFinalizeState = null,
        string? deliveryState = null,
        string? archivePath = null,
        string? errorCode = null,
        string? errorDetail = null,
        int? retryCount = null,
        DateTimeOffset? nextRetryAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET local_finalize_state=COALESCE($local,local_finalize_state), delivery_state=COALESCE($delivery,delivery_state), archive_path=COALESCE($archive,archive_path), last_error_code=$code, last_error_detail=$detail, retry_count=COALESCE($retry,retry_count), next_retry_at=$next WHERE id=$id";
        command.Parameters.AddWithValue("$local", (object?)localFinalizeState ?? DBNull.Value);
        command.Parameters.AddWithValue("$delivery", (object?)deliveryState ?? DBNull.Value);
        command.Parameters.AddWithValue("$archive", (object?)archivePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)errorDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("$retry", (object?)retryCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", (object?)nextRetryAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RecordingSessionInfo?> GetSessionInfoAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT meeting_id,title,started_at,state,local_finalize_state,delivery_state,archive_path,last_error_code,last_error_detail,retry_count,next_retry_at FROM recording_sessions WHERE id=$session";
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        Guid? meetingId = reader.IsDBNull(0) ? null : Guid.TryParse(reader.GetString(0), out var parsed) ? parsed : null;
        DateTimeOffset? startedAt = reader.IsDBNull(2) ? null : DateTimeOffset.TryParse(reader.GetString(2), out var started) ? started : null;
        DateTimeOffset? nextRetry = reader.IsDBNull(10) ? null : DateTimeOffset.TryParse(reader.GetString(10), out var parsedRetry) ? parsedRetry : null;
        return new RecordingSessionInfo(
            sessionId,
            meetingId,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            startedAt,
            reader.IsDBNull(3) ? "UNKNOWN" : reader.GetString(3),
            reader.IsDBNull(4) ? "PENDING" : reader.GetString(4),
            reader.IsDBNull(5) ? "NOT_STARTED" : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            nextRetry);
    }

    public async Task<(int Total, int Confirmed, int Pending)> GetChunkCountsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN status='CONFIRMED' THEN 1 ELSE 0 END),0), COALESCE(SUM(CASE WHEN status<>'CONFIRMED' THEN 1 ELSE 0 END),0) FROM recording_chunks WHERE session_id=$session";
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0, 0);
        return (Convert.ToInt32(reader.GetInt64(0)), Convert.ToInt32(reader.GetInt64(1)), Convert.ToInt32(reader.GetInt64(2)));
    }

    public async Task<IReadOnlyList<RecordingArchiveChunk>> GetArchiveChunksAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT track_id,track_type,sequence,local_path,start_sample,sample_count,sample_rate,channels,size_bytes,sha256 FROM recording_chunks WHERE session_id=$session ORDER BY track_id,sequence";
        command.Parameters.AddWithValue("$session", sessionId);
        var result = new List<RecordingArchiveChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RecordingArchiveChunk(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt64(8), reader.GetString(9)));
        return result;
    }

    public void RegisterRawChunk(RawRecordingChunk chunk)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_raw_chunks(id,session_id,track_id,sequence,raw_path,output_path,start_sample,sample_count,sample_rate,channels,track_type,encoding,bits_per_sample,status,created_at,updated_at)
            VALUES($id,$session,$track,$sequence,$raw,$output,$start,$count,$rate,$channels,$trackType,$encoding,$bits,'WRITING',$created,$created)
            ON CONFLICT(track_id,sequence) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", chunk.Id);
        command.Parameters.AddWithValue("$session", chunk.SessionId);
        command.Parameters.AddWithValue("$track", chunk.TrackId);
        command.Parameters.AddWithValue("$sequence", chunk.Sequence);
        command.Parameters.AddWithValue("$raw", chunk.RawPath);
        command.Parameters.AddWithValue("$output", chunk.OutputPath);
        command.Parameters.AddWithValue("$start", chunk.StartSample);
        command.Parameters.AddWithValue("$count", chunk.SampleCount);
        command.Parameters.AddWithValue("$rate", chunk.SampleRate);
        command.Parameters.AddWithValue("$channels", chunk.Channels);
        command.Parameters.AddWithValue("$trackType", chunk.TrackType);
        command.Parameters.AddWithValue("$encoding", chunk.Encoding);
        command.Parameters.AddWithValue("$bits", chunk.BitsPerSample);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void MarkRawChunkReady(string sessionId, string trackId, int sequence, long sampleCount, long rawSizeBytes, string rawSha256)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_raw_chunks SET status='RAW_READY',sample_count=$sampleCount,raw_size_bytes=$size,raw_sha256=$sha,error=NULL,updated_at=$updated WHERE session_id=$session AND track_id=$track AND sequence=$sequence";
        command.Parameters.AddWithValue("$sampleCount", sampleCount);
        command.Parameters.AddWithValue("$size", rawSizeBytes);
        command.Parameters.AddWithValue("$sha", rawSha256);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$track", trackId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.ExecuteNonQuery();
    }

    public async Task SetRawChunkStateAsync(string sessionId, string trackId, int sequence, string status, long? rawSizeBytes = null, string? rawSha256 = null, string? error = null, long? sampleCount = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_raw_chunks SET status=$status,sample_count=COALESCE($sampleCount,sample_count),raw_size_bytes=COALESCE($size,raw_size_bytes),raw_sha256=COALESCE($sha,raw_sha256),error=$error,updated_at=$updated WHERE session_id=$session AND track_id=$track AND sequence=$sequence";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$sampleCount", (object?)sampleCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)rawSizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha", (object?)rawSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$track", trackId);
        command.Parameters.AddWithValue("$sequence", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RawRecordingChunk>> RawChunksNeedingRecoveryAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,track_id,sequence,raw_path,output_path,start_sample,sample_count,sample_rate,channels,track_type,encoding,bits_per_sample,status,raw_size_bytes,raw_sha256,error FROM recording_raw_chunks WHERE status IN ('WRITING','RAW_READY','ENCODING','ENCODE_FAILED') ORDER BY session_id,track_id,sequence LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var result = new List<RawRecordingChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RawRecordingChunk(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetString(10), reader.GetString(11), reader.GetInt32(12), reader.GetString(13), reader.GetInt64(14), reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16)));
        }
        return result;
    }

    public async Task<RawChunkBacklog> GetRawChunkBacklogAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status,COUNT(*),COALESCE(SUM(raw_size_bytes),0) FROM recording_raw_chunks WHERE status NOT IN ('READY','DISCARDED') GROUP BY status";
        var counts = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) counts[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt64(2));
        var writing = counts.GetValueOrDefault("WRITING").Count;
        var rawReady = counts.GetValueOrDefault("RAW_READY").Count;
        var encoding = counts.GetValueOrDefault("ENCODING").Count;
        var failed = counts.GetValueOrDefault("ENCODE_FAILED").Count;
        return new RawChunkBacklog(writing + rawReady + encoding + failed, writing, encoding, failed, counts.Values.Sum(item => item.Bytes));
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

    public async Task<IReadOnlyList<string>> SessionsWithPendingEventsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT session_id FROM recording_events WHERE synced_at IS NULL ORDER BY session_id";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }
    public async Task<IReadOnlyList<RecordingEventRow>> PendingEventsAsync(string localSessionId, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,event_type,media_time_ms,payload_json,created_at FROM recording_events WHERE session_id=$session AND synced_at IS NULL ORDER BY created_at LIMIT $limit";
        command.Parameters.AddWithValue("$session", localSessionId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var result = new List<RecordingEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new RecordingEventRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))));
        return result;
    }

    public async Task MarkEventsSyncedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var values = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (values.Length == 0) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in values)
        {
            await using var command = new SqliteCommand("UPDATE recording_events SET synced_at=$synced WHERE id=$id", connection, tx);
            command.Parameters.AddWithValue("$synced", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }
    public async Task UpsertServerBindingAsync(ServerBinding binding, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO server_bindings(local_session_id,local_track_id,server_session_id,server_track_id,created_at) VALUES($localSession,$localTrack,$serverSession,$serverTrack,$created) ON CONFLICT(local_session_id,local_track_id) DO UPDATE SET server_session_id=excluded.server_session_id,server_track_id=excluded.server_track_id";
        command.Parameters.AddWithValue("$localSession", binding.LocalSessionId);
        command.Parameters.AddWithValue("$localTrack", binding.LocalTrackId);
        command.Parameters.AddWithValue("$serverSession", binding.ServerSessionId.ToString());
        command.Parameters.AddWithValue("$serverTrack", binding.ServerTrackId.ToString());
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ServerBinding?> GetServerBindingAsync(string localSessionId, string localTrackId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_session_id,local_track_id,server_session_id,server_track_id FROM server_bindings WHERE local_session_id=$session AND local_track_id=$track";
        command.Parameters.AddWithValue("$session", localSessionId);
        command.Parameters.AddWithValue("$track", localTrackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ServerBinding(reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)));
    }

    public async Task<Guid?> GetServerSessionIdAsync(string localSessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT server_session_id FROM server_bindings WHERE local_session_id=$session LIMIT 1";
        command.Parameters.AddWithValue("$session", localSessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && Guid.TryParse(text, out var id) ? id : null;
    }

    public async Task<string?> GetLocalTrackIdAsync(string localSessionId, Guid serverTrackId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_track_id FROM server_bindings WHERE local_session_id=$session AND server_track_id=$track";
        command.Parameters.AddWithValue("$session", localSessionId);
        command.Parameters.AddWithValue("$track", serverTrackId.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text ? text : null;
    }

    public async Task<IReadOnlyList<RecordingChunk>> GetChunksAsync(string localSessionId, string localTrackId, IEnumerable<int> sequences, CancellationToken cancellationToken = default)
    {
        var requested = sequences.Where(sequence => sequence >= 0).Distinct().Take(100_000).ToArray();
        if (requested.Length == 0) return Array.Empty<RecordingChunk>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = new List<string>(requested.Length);
        for (var index = 0; index < requested.Length; index++)
        {
            var name = $"$sequence{index}";
            placeholders.Add(name);
            command.Parameters.AddWithValue(name, requested[index]);
        }
        command.CommandText = $"SELECT id,session_id,track_id,sequence,local_path,start_sample,sample_count,sample_rate,channels,track_type,size_bytes,sha256,status,attempts FROM recording_chunks WHERE session_id=$session AND track_id=$track AND sequence IN ({string.Join(',', placeholders)}) ORDER BY sequence";
        command.Parameters.AddWithValue("$session", localSessionId);
        command.Parameters.AddWithValue("$track", localTrackId);
        var result = new List<RecordingChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RecordingChunk(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9), reader.GetInt64(10), reader.GetString(11), reader.GetString(12), reader.GetInt32(13)));
        return result;
    }


    public async Task<Guid?> GetMeetingIdAsync(string localSessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT meeting_id FROM recording_sessions WHERE id=$session";
        command.Parameters.AddWithValue("$session", localSessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && Guid.TryParse(text, out var id) ? id : null;
    }

    public async Task<IReadOnlyList<RecordingTrackInfo>> GetTrackInfosAsync(string localSessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT track_id, sample_rate, channels, track_type FROM recording_chunks WHERE session_id=$session GROUP BY track_id, sample_rate, channels, track_type ORDER BY track_id";
        command.Parameters.AddWithValue("$session", localSessionId);
        var result = new List<RecordingTrackInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetString(0);
            result.Add(new RecordingTrackInfo(trackId, reader.GetString(3), reader.GetInt32(1), reader.GetInt32(2)));
        }
        return result;
    }

    public async Task<RecordingManifest?> BuildManifestAsync(string localSessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.server_session_id,b.server_track_id,c.track_id,c.sequence,c.sample_rate,c.channels,c.track_type,c.start_sample,c.sample_count
            FROM recording_chunks c
            JOIN server_bindings b ON b.local_session_id=c.session_id AND b.local_track_id=c.track_id
            WHERE c.session_id=$session
            ORDER BY b.server_track_id,c.sequence;
            """;
        command.Parameters.AddWithValue("$session", localSessionId);
        var grouped = new Dictionary<Guid, (Guid ServerSessionId, string TrackType, int SampleRate, int Channels, int MaxSequence, long StartSample, long TotalSamples)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var serverSessionId = Guid.Parse(reader.GetString(0));
            var serverTrackId = Guid.Parse(reader.GetString(1));
            var localTrackId = reader.GetString(2);
            var sequence = reader.GetInt32(3);
            var sampleRate = reader.GetInt32(4);
            var channels = reader.GetInt32(5);
            var trackType = reader.GetString(6);
            var startSample = reader.GetInt64(7);
            var sampleCount = reader.GetInt64(8);
            if (!grouped.TryGetValue(serverTrackId, out var current)) current = (serverSessionId, trackType, sampleRate, channels, -1, long.MaxValue, 0);
            grouped[serverTrackId] = (current.ServerSessionId, current.TrackType, current.SampleRate, current.Channels, Math.Max(current.MaxSequence, sequence), Math.Min(current.StartSample, startSample), current.TotalSamples + sampleCount);
        }
        if (grouped.Count == 0) return null;
        var session = grouped.Values.First().ServerSessionId;
        return new RecordingManifest(session, grouped.Select(item => new RecordingManifestTrack(item.Key, item.Value.TrackType, item.Value.SampleRate, item.Value.Channels, item.Value.MaxSequence + 1, item.Value.TotalSamples, item.Value.StartSample == long.MaxValue ? 0 : item.Value.StartSample)).ToArray());
    }

    public async Task<long> GetCursorAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM agent_state WHERE key='command_cursor'";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && long.TryParse(text, out var cursor) ? cursor : 0;
    }

    public async Task SetCursorAsync(long cursor, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO agent_state(key,value) VALUES('command_cursor',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$value", cursor.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SessionsNeedingRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
         command.CommandText = "SELECT id FROM recording_sessions WHERE state NOT IN ('CANCELLED','FINALIZED') AND ((next_retry_at IS NOT NULL AND next_retry_at <= $now) OR (next_retry_at IS NULL AND local_finalize_state IN ('PENDING','FINALIZING_LOCAL')) OR (next_retry_at IS NULL AND state='RECORDING') OR (next_retry_at IS NULL AND state='FINALIZING' AND (finished_at IS NULL OR finished_at <= $cutoff)) OR (next_retry_at IS NULL AND state='FAILED' AND local_finalize_state<>'LOCAL_FAILED')) AND local_finalize_state<>'LOCAL_FAILED' ORDER BY started_at";
         command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"));
         command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<int> PendingUploadSessionCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM recording_sessions WHERE state NOT IN ('CANCELLED','FINALIZED') AND (state IN ('FINALIZING','FAILED') OR local_finalize_state IN ('PENDING','FINALIZING_LOCAL','LOCAL_FAILED') OR delivery_state IN ('BINDING','UPLOADING','RECONCILING','FINALIZING_SERVER','WAITING_SERVER','DELIVERY_ERROR'))";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<(int Sessions, int Chunks)> CancelServerSessionAsync(Guid serverSessionId, bool discardTransport, CancellationToken cancellationToken = default)
    {
        var localSessionIds = new List<string>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var sessions = connection.CreateCommand())
        {
            sessions.CommandText = "SELECT DISTINCT local_session_id FROM server_bindings WHERE server_session_id=$server";
            sessions.Parameters.AddWithValue("$server", serverSessionId.ToString());
            await using var reader = await sessions.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) localSessionIds.Add(reader.GetString(0));
        }
        if (localSessionIds.Count == 0) return (0, 0);

        var paths = new List<string>();
        var chunks = 0;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var localSessionId in localSessionIds)
        {
            if (discardTransport)
            {
                await using var files = connection.CreateCommand();
                files.Transaction = transaction;
                files.CommandText = "SELECT local_path FROM recording_chunks WHERE session_id=$session UNION ALL SELECT raw_path FROM recording_raw_chunks WHERE session_id=$session UNION ALL SELECT output_path FROM recording_raw_chunks WHERE session_id=$session";
                files.Parameters.AddWithValue("$session", localSessionId);
                await using var reader = await files.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) paths.Add(reader.GetString(0));
            }

            await using var cancel = connection.CreateCommand();
            cancel.Transaction = transaction;
            cancel.CommandText = "UPDATE recording_sessions SET state='CANCELLED', finished_at=COALESCE(finished_at,$finished) WHERE id=$session";
            cancel.Parameters.AddWithValue("$session", localSessionId);
            cancel.Parameters.AddWithValue("$finished", DateTimeOffset.UtcNow.ToString("O"));
            await cancel.ExecuteNonQueryAsync(cancellationToken);

            if (discardTransport)
            {
                await using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM recording_chunks WHERE session_id=$session";
                count.Parameters.AddWithValue("$session", localSessionId);
                chunks += Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
                foreach (var sql in new[]
                {
                    "DELETE FROM recording_events WHERE session_id=$session",
                    "DELETE FROM recording_raw_chunks WHERE session_id=$session",
                    "DELETE FROM recording_chunks WHERE session_id=$session",
                    "DELETE FROM server_bindings WHERE local_session_id=$session",
                })
                {
                    await using var cleanup = connection.CreateCommand();
                    cleanup.Transaction = transaction;
                    cleanup.CommandText = sql;
                    cleanup.Parameters.AddWithValue("$session", localSessionId);
                    await cleanup.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            else
            {
                await using var cancelChunks = connection.CreateCommand();
                cancelChunks.Transaction = transaction;
                cancelChunks.CommandText = "UPDATE recording_chunks SET status='CANCELLED' WHERE session_id=$session AND status<>'CONFIRMED'";
                cancelChunks.Parameters.AddWithValue("$session", localSessionId);
                chunks += await cancelChunks.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);

        if (discardTransport)
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        return (localSessionIds.Count, chunks);
    }

    public async Task<(int Sessions, int Chunks)> CancelLocalSessionAsync(string localSessionId, bool discardTransport, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localSessionId)) return (0, 0);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var paths = new List<string>();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM recording_sessions WHERE id=$session";
            exists.Parameters.AddWithValue("$session", localSessionId);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return (0, 0);
        }
        var chunks = 0;
        if (discardTransport)
        {
            await using var files = connection.CreateCommand();
            files.Transaction = transaction;
            files.CommandText = "SELECT local_path FROM recording_chunks WHERE session_id=$session UNION ALL SELECT raw_path FROM recording_raw_chunks WHERE session_id=$session UNION ALL SELECT output_path FROM recording_raw_chunks WHERE session_id=$session";
            files.Parameters.AddWithValue("$session", localSessionId);
            await using var reader = await files.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) paths.Add(reader.GetString(0));
        }
        await using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = "UPDATE recording_sessions SET state='CANCELLED', finished_at=COALESCE(finished_at,$finished) WHERE id=$session";
            cancel.Parameters.AddWithValue("$session", localSessionId);
            cancel.Parameters.AddWithValue("$finished", DateTimeOffset.UtcNow.ToString("O"));
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }
        if (discardTransport)
        {
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM recording_chunks WHERE session_id=$session";
                count.Parameters.AddWithValue("$session", localSessionId);
                chunks = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
            }
            foreach (var sql in new[]
            {
                "DELETE FROM recording_events WHERE session_id=$session",
                "DELETE FROM recording_raw_chunks WHERE session_id=$session",
                "DELETE FROM recording_chunks WHERE session_id=$session",
                "DELETE FROM server_bindings WHERE local_session_id=$session",
            })
            {
                await using var cleanup = connection.CreateCommand();
                cleanup.Transaction = transaction;
                cleanup.CommandText = sql;
                cleanup.Parameters.AddWithValue("$session", localSessionId);
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            await using var cancelChunks = connection.CreateCommand();
            cancelChunks.Transaction = transaction;
            cancelChunks.CommandText = "UPDATE recording_chunks SET status='CANCELLED' WHERE session_id=$session AND status<>'CONFIRMED'";
            cancelChunks.Parameters.AddWithValue("$session", localSessionId);
            chunks = await cancelChunks.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (discardTransport)
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        return (1, chunks);
    }

    public async Task UpsertChunkAsync(RecordingChunk chunk, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_chunks(id,session_id,track_id,sequence,local_path,start_sample,sample_count,sample_rate,channels,track_type,size_bytes,sha256,status,attempts,created_at)
            VALUES($id,$session,$track,$sequence,$path,$start,$count,$rate,$channels,$trackType,$size,$sha,$status,$attempts,$created)
            ON CONFLICT(track_id,sequence) DO UPDATE SET local_path=excluded.local_path,track_type=excluded.track_type,size_bytes=excluded.size_bytes,sha256=excluded.sha256,status=excluded.status;
            """;
        command.Parameters.AddWithValue("$id", chunk.Id); command.Parameters.AddWithValue("$session", chunk.SessionId); command.Parameters.AddWithValue("$track", chunk.TrackId); command.Parameters.AddWithValue("$sequence", chunk.Sequence); command.Parameters.AddWithValue("$path", chunk.LocalPath); command.Parameters.AddWithValue("$start", chunk.StartSample); command.Parameters.AddWithValue("$count", chunk.SampleCount); command.Parameters.AddWithValue("$rate", chunk.SampleRate); command.Parameters.AddWithValue("$channels", chunk.Channels); command.Parameters.AddWithValue("$trackType", chunk.TrackType); command.Parameters.AddWithValue("$size", chunk.SizeBytes); command.Parameters.AddWithValue("$sha", chunk.Sha256); command.Parameters.AddWithValue("$status", chunk.Status); command.Parameters.AddWithValue("$attempts", chunk.Attempts); command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecordingChunk>> PendingChunksAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.id,c.session_id,c.track_id,c.sequence,c.local_path,c.start_sample,c.sample_count,c.sample_rate,c.channels,c.track_type,c.size_bytes,c.sha256,c.status,c.attempts FROM recording_chunks c JOIN recording_sessions s ON s.id=c.session_id WHERE c.status NOT IN ('CONFIRMED','CANCELLED') AND s.state<>'CANCELLED' ORDER BY c.session_id,c.track_id,c.sequence LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<RecordingChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new RecordingChunk(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9), reader.GetInt64(10), reader.GetString(11), reader.GetString(12), reader.GetInt32(13)));
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

    public async Task SaveCommandResultAsync(Guid commandId, long cursor, string status, object result, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO agent_command_results(command_id,cursor,status,result_json,created_at) VALUES($id,$cursor,$status,$result,$created) ON CONFLICT(command_id) DO NOTHING";
        command.Parameters.AddWithValue("$id", commandId.ToString());
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PendingCommandResult?> GetCommandResultAsync(Guid commandId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT command_id,cursor,status,result_json FROM agent_command_results WHERE command_id=$id";
        command.Parameters.AddWithValue("$id", commandId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        using var document = JsonDocument.Parse(reader.GetString(3));
        return new PendingCommandResult(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2), document.RootElement.Clone());
    }

    public async Task<IReadOnlyList<PendingCommandResult>> PendingCommandResultsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PendingCommandResult>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT command_id,cursor,status,result_json FROM agent_command_results ORDER BY cursor";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(3));
            result.Add(new PendingCommandResult(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2), document.RootElement.Clone()));
        }
        return result;
    }

    public async Task AcknowledgeCommandResultAsync(Guid commandId, long cursor, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = "INSERT INTO agent_state(key,value) VALUES('command_cursor',$value) ON CONFLICT(key) DO UPDATE SET value=CASE WHEN CAST(agent_state.value AS INTEGER) < CAST(excluded.value AS INTEGER) THEN excluded.value ELSE agent_state.value END";
            state.Parameters.AddWithValue("$value", cursor.ToString());
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM agent_command_results WHERE command_id=$id";
            remove.Parameters.AddWithValue("$id", commandId.ToString());
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PurgeFinalizedSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var paths = new List<string>();
        var rawPaths = new List<string>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT local_path FROM recording_chunks WHERE session_id=$session AND status='CONFIRMED'";
            select.Parameters.AddWithValue("$session", sessionId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) paths.Add(reader.GetString(0));
        }
        await using (var rawSelect = connection.CreateCommand())
        {
            rawSelect.CommandText = "SELECT raw_path FROM recording_raw_chunks WHERE session_id=$session AND status='READY'";
            rawSelect.Parameters.AddWithValue("$session", sessionId);
            await using var reader = await rawSelect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rawPaths.Add(reader.GetString(0));
        }
        await SetSessionStateAsync(sessionId, "FINALIZED", cancellationToken);
        foreach (var path in paths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
        foreach (var path in rawPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
        await using var cleanup = connection.CreateCommand();
        cleanup.CommandText = "DELETE FROM recording_raw_chunks WHERE session_id=$session AND status='READY'";
        cleanup.Parameters.AddWithValue("$session", sessionId);
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
}
}

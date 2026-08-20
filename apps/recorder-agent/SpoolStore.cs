using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public sealed record RecordingTrackInfo(
    string TrackId,
    string TrackType,
    int SampleRate,
    int Channels,
    string? EndpointId = null,
    string? DeviceFriendlyName = null,
    string SelectionMode = "DEFAULT",
    string Profile = "ROOM",
    string Encoding = "IeeeFloat",
    int BitsPerSample = 32,
    string? SourceEncoding = null,
    string? SourceSubFormat = null,
    int? ValidBitsPerSample = null);

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
    int Attempts,
    DateTimeOffset? LastAttemptAtUtc = null,
    DateTimeOffset? NextAttemptAtUtc = null,
    string? LastErrorCode = null);

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
    string? Error,
    string? SourceEncoding = null,
    string? SourceSubFormat = null,
    int? ValidBitsPerSample = null,
    int EncodeAttempts = 0,
    DateTimeOffset? NextEncodeAttemptAtUtc = null,
    string? LastEncodeErrorCode = null,
    string? EncodingWorkerId = null,
    DateTimeOffset? EncodingStartedAtUtc = null,
    DateTimeOffset? EncodingLeaseExpiresAtUtc = null);

public sealed record RawChunkBacklog(
    int Pending,
    int Writing,
    int Encoding,
    int Failed,
    long Bytes,
    double? OldestPendingAgeMs = null,
    string Health = "HEALTHY",
    int Ready = 0,
    int ReadyForUpload = 0,
    int Total = 0,
    int Completed = 0,
    int TerminalFailed = 0,
    int FinalizerQueueDepth = 0,
    int FinalizerMaximumDepth = 0,
    int FinalizerCapacity = 0,
    string? LastErrorCode = null);

public sealed record ServerBinding(string LocalSessionId, string LocalTrackId, Guid ServerSessionId, Guid ServerTrackId);
public sealed record RecordingManifestTrack(Guid ServerTrackId, string TrackType, int SampleRate, int Channels, int ExpectedChunkCount, long TotalSamples, long StartSample = 0);
public sealed record PendingCommandResult(Guid CommandId, long Cursor, string Status, JsonElement Result);
public sealed record RecordingEventRow(string Id, string SessionId, string EventType, long? MediaTimeMs, string PayloadJson, DateTimeOffset CreatedAt);
public sealed record PendingRecordingEvent(string Id, string EventType, string PayloadJson, DateTimeOffset CreatedAt);
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
    DateTimeOffset? NextRetryAtUtc = null,
    Guid? MediaAssetId = null,
    Guid? ProcessingJobId = null,
    string? TraceId = null,
    string? PipelineCorrelationId = null,
    DateTimeOffset? ServerAcceptedAtUtc = null,
    DateTimeOffset? MediaValidatedAtUtc = null,
    DateTimeOffset? TransportPurgeAfterUtc = null,
    DateTimeOffset? LocalArchivePurgeAfterUtc = null,
    DateTimeOffset? LocalArchivePurgedAtUtc = null,
    Guid? OwnerUserId = null,
    int? LastErrorHttpStatus = null,
    bool? LastErrorRetryable = null,
    string MeetingBindState = "UNBOUND",
    string DeliveryMode = "AUTO",
    string? ArchiveErrorCode = null,
    string? ArchiveErrorDetail = null,
    int ArchiveRetryCount = 0,
    DateTimeOffset? ArchiveNextRetryAtUtc = null,
    string AcousticProfile = "AUTO",
    string PlayableAudioState = "NOT_REQUIRED",
    string? PlayableAudioPath = null,
    string? PlayableAudioError = null,
    DateTimeOffset? PlayableAudioCreatedAtUtc = null,
    int PlayableAudioRetryCount = 0,
    DateTimeOffset? PlayableAudioNextRetryAtUtc = null);

public sealed record LocalDurabilityOutcome(
    string State,
    bool HasDurableAudio,
    bool HasRecoverablePart,
    int DurableChunkCount,
    string? ErrorCode = null);
public sealed record RecordingArchiveChunk(string TrackId, string TrackType, int Sequence, string LocalPath, long StartSample, long SampleCount, int SampleRate, int Channels, long SizeBytes, string Sha256);
public sealed record ChunkDeliveryMetrics(int Total, int Ready, int Uploading, int Confirmed, int Failed, long BytesPending, double? OldestPendingAgeSeconds);
public sealed record RetentionCandidate(string SessionId, string Category, IReadOnlyList<string> Paths, long Bytes, DateTimeOffset PurgeAfterUtc);
public sealed record PlayableAudioFile(
    string SessionId,
    string TrackId,
    string TrackType,
    string FileName,
    string LocalPath,
    long SizeBytes,
    long SampleCount,
    int SampleRate,
    int Channels,
    string Encoding,
    string Sha256,
    string State = "READY");
public sealed record LocalSessionSummary(
    string SessionId,
    string? Title,
    DateTimeOffset? StartedAt,
    string State,
    string LocalFinalizeState,
    string DeliveryState,
    string PlayableAudioState,
    string? PlayableAudioPath,
    string? PlayableAudioError,
    DateTimeOffset? PlayableAudioCreatedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    DateTimeOffset? PlayableAudioNextRetryAtUtc,
    IReadOnlyList<PlayableAudioFile> PlayableFiles);

public sealed record RecordingManifest(Guid ServerSessionId, IReadOnlyList<RecordingManifestTrack> Tracks);

public sealed class SpoolStore
{
    private const int UploadStaleAfterSeconds = 300;
    private readonly string _connectionString;
    private readonly string _dataRoot;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NormalizeAcousticProfile(string? profile)
    {
        var normalized = string.IsNullOrWhiteSpace(profile) ? "AUTO" : profile.Trim().ToUpperInvariant();
        return normalized is "STANDARD" or "LARGE_ROOM" ? normalized : "AUTO";
    }

    private static DateTimeOffset? ParseDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || !DateTimeOffset.TryParse(reader.GetString(ordinal), out var value) ? null : value;

    public SpoolStore(string root)
    {
        Directory.CreateDirectory(root);
        _dataRoot = Path.GetFullPath(root);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "agent.db"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public Task WaitUntilInitializedAsync(CancellationToken cancellationToken = default) =>
        _initialized.Task.WaitAsync(cancellationToken);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized.Task.IsCompletedSuccessfully) return;
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_initialized.Task.IsCompletedSuccessfully)
        {
            _initializeGate.Release();
            return;
        }
        try
        {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
             CREATE TABLE IF NOT EXISTS recording_sessions(id TEXT PRIMARY KEY, meeting_id TEXT, title TEXT, owner_user_id TEXT, state TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT, total_samples INTEGER NOT NULL DEFAULT 0, local_finalize_state TEXT NOT NULL DEFAULT 'PENDING', delivery_state TEXT NOT NULL DEFAULT 'NOT_REQUESTED', meeting_bind_state TEXT NOT NULL DEFAULT 'UNBOUND', delivery_mode TEXT NOT NULL DEFAULT 'AUTO', acoustic_profile TEXT NOT NULL DEFAULT 'AUTO', archive_path TEXT, last_error_code TEXT, last_error_detail TEXT, retry_count INTEGER NOT NULL DEFAULT 0, next_retry_at TEXT, media_asset_id TEXT, processing_job_id TEXT, trace_id TEXT, pipeline_correlation_id TEXT NOT NULL, server_accepted_at TEXT, media_validated_at TEXT, transport_purge_after TEXT, local_archive_purge_after TEXT, local_archive_purged_at TEXT, archive_error_code TEXT, archive_error_detail TEXT, archive_retry_count INTEGER NOT NULL DEFAULT 0, archive_next_retry_at TEXT, playable_audio_state TEXT NOT NULL DEFAULT 'NOT_REQUIRED', playable_audio_path TEXT, playable_audio_error TEXT, playable_audio_created_at TEXT, playable_audio_retry_count INTEGER NOT NULL DEFAULT 0, playable_audio_next_retry_at TEXT);
            CREATE TABLE IF NOT EXISTS recording_chunks(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, track_id TEXT NOT NULL, sequence INTEGER NOT NULL, local_path TEXT NOT NULL, start_sample INTEGER NOT NULL, sample_count INTEGER NOT NULL, sample_rate INTEGER NOT NULL, channels INTEGER NOT NULL, track_type TEXT NOT NULL DEFAULT 'room-microphone', size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL, status TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, last_attempt_at TEXT, next_attempt_at TEXT, last_error_code TEXT, created_at TEXT NOT NULL, confirmed_at TEXT, UNIQUE(track_id, sequence));
             CREATE TABLE IF NOT EXISTS recording_events(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, event_type TEXT NOT NULL, media_time_ms INTEGER, payload_json TEXT NOT NULL, created_at TEXT NOT NULL, synced_at TEXT);
             CREATE TABLE IF NOT EXISTS recording_pending_events(id TEXT PRIMARY KEY, event_type TEXT NOT NULL, payload_json TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recording_raw_chunks(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, track_id TEXT NOT NULL, sequence INTEGER NOT NULL, raw_path TEXT NOT NULL, output_path TEXT NOT NULL, start_sample INTEGER NOT NULL, sample_count INTEGER NOT NULL, sample_rate INTEGER NOT NULL, channels INTEGER NOT NULL, track_type TEXT NOT NULL, encoding TEXT NOT NULL, bits_per_sample INTEGER NOT NULL, source_encoding TEXT, source_sub_format TEXT, valid_bits_per_sample INTEGER, status TEXT NOT NULL, raw_size_bytes INTEGER NOT NULL DEFAULT 0, raw_sha256 TEXT, error TEXT, raw_purge_after TEXT, encode_attempts INTEGER NOT NULL DEFAULT 0, next_encode_attempt_at TEXT, last_encode_error_code TEXT, encoding_worker_id TEXT, encoding_started_at TEXT, encoding_lease_expires_at TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, UNIQUE(track_id, sequence));
            CREATE TABLE IF NOT EXISTS recording_track_info(track_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, track_type TEXT NOT NULL, sample_rate INTEGER NOT NULL, channels INTEGER NOT NULL, endpoint_id TEXT, device_name TEXT, selection_mode TEXT NOT NULL DEFAULT 'DEFAULT', profile TEXT NOT NULL DEFAULT 'ROOM', encoding TEXT NOT NULL DEFAULT 'IeeeFloat', bits_per_sample INTEGER NOT NULL DEFAULT 32, source_encoding TEXT, source_sub_format TEXT, valid_bits_per_sample INTEGER, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS server_bindings(local_session_id TEXT NOT NULL, local_track_id TEXT NOT NULL, server_session_id TEXT NOT NULL, server_track_id TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(local_session_id, local_track_id));
            CREATE TABLE IF NOT EXISTS agent_state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS agent_command_results(command_id TEXT PRIMARY KEY, cursor INTEGER NOT NULL, status TEXT NOT NULL, result_json TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recording_playable_files(session_id TEXT NOT NULL, track_id TEXT NOT NULL, track_type TEXT NOT NULL, file_name TEXT NOT NULL, local_path TEXT NOT NULL, size_bytes INTEGER NOT NULL, sample_count INTEGER NOT NULL, sample_rate INTEGER NOT NULL, channels INTEGER NOT NULL, encoding TEXT NOT NULL, sha256 TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'READY', created_at TEXT NOT NULL, PRIMARY KEY(session_id, track_id));
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
            "ALTER TABLE recording_sessions ADD COLUMN next_retry_at TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN media_asset_id TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN processing_job_id TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN trace_id TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN pipeline_correlation_id TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN server_accepted_at TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN media_validated_at TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN transport_purge_after TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN local_archive_purge_after TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN local_archive_purged_at TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN owner_user_id TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN last_error_http_status INTEGER",
            "ALTER TABLE recording_sessions ADD COLUMN last_error_retryable INTEGER",
            "ALTER TABLE recording_sessions ADD COLUMN meeting_bind_state TEXT NOT NULL DEFAULT 'UNBOUND'",
            "ALTER TABLE recording_sessions ADD COLUMN delivery_mode TEXT NOT NULL DEFAULT 'AUTO'",
            "ALTER TABLE recording_sessions ADD COLUMN acoustic_profile TEXT NOT NULL DEFAULT 'AUTO'",
            "ALTER TABLE recording_sessions ADD COLUMN archive_error_code TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN archive_error_detail TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN archive_retry_count INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE recording_sessions ADD COLUMN archive_next_retry_at TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN playable_audio_state TEXT NOT NULL DEFAULT 'NOT_REQUIRED'",
            "ALTER TABLE recording_sessions ADD COLUMN playable_audio_path TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN playable_audio_error TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN playable_audio_created_at TEXT",
            "ALTER TABLE recording_sessions ADD COLUMN playable_audio_retry_count INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE recording_sessions ADD COLUMN playable_audio_next_retry_at TEXT"
        })
        {
            await using var stateMigration = connection.CreateCommand();
            stateMigration.CommandText = column;
            try { await stateMigration.ExecuteNonQueryAsync(cancellationToken); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
        }

        await using var rawPurgeMigration = connection.CreateCommand();
        rawPurgeMigration.CommandText = "ALTER TABLE recording_raw_chunks ADD COLUMN raw_purge_after TEXT";
        try { await rawPurgeMigration.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }

        foreach (var column in new[]
        {
            "ALTER TABLE recording_chunks ADD COLUMN last_attempt_at TEXT",
            "ALTER TABLE recording_chunks ADD COLUMN next_attempt_at TEXT",
            "ALTER TABLE recording_chunks ADD COLUMN last_error_code TEXT"
        })
        {
            await using var chunkMigration = connection.CreateCommand();
            chunkMigration.CommandText = column;
            try { await chunkMigration.ExecuteNonQueryAsync(cancellationToken); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
        }

        foreach (var column in new[]
        {
            "ALTER TABLE recording_track_info ADD COLUMN encoding TEXT NOT NULL DEFAULT 'IeeeFloat'",
            "ALTER TABLE recording_track_info ADD COLUMN bits_per_sample INTEGER NOT NULL DEFAULT 32",
            "ALTER TABLE recording_track_info ADD COLUMN source_encoding TEXT",
            "ALTER TABLE recording_track_info ADD COLUMN source_sub_format TEXT",
            "ALTER TABLE recording_track_info ADD COLUMN valid_bits_per_sample INTEGER",
            "ALTER TABLE recording_raw_chunks ADD COLUMN source_encoding TEXT",
            "ALTER TABLE recording_raw_chunks ADD COLUMN source_sub_format TEXT",
            "ALTER TABLE recording_raw_chunks ADD COLUMN valid_bits_per_sample INTEGER"
            ,"ALTER TABLE recording_raw_chunks ADD COLUMN encode_attempts INTEGER NOT NULL DEFAULT 0"
            ,"ALTER TABLE recording_raw_chunks ADD COLUMN next_encode_attempt_at TEXT"
            ,"ALTER TABLE recording_raw_chunks ADD COLUMN last_encode_error_code TEXT"
            ,"ALTER TABLE recording_raw_chunks ADD COLUMN encoding_worker_id TEXT"
            ,"ALTER TABLE recording_raw_chunks ADD COLUMN encoding_started_at TEXT"
            ,"ALTER TABLE recording_raw_chunks ADD COLUMN encoding_lease_expires_at TEXT"
        })
        {
            await using var trackMigration = connection.CreateCommand();
            trackMigration.CommandText = column;
            try { await trackMigration.ExecuteNonQueryAsync(cancellationToken); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
        }

        await RecoverStaleUploadingChunksAsync(cancellationToken);
        await RecoverStalePlayableBuildsAsync(cancellationToken);
        _initialized.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _initialized.TrySetException(ex);
            throw;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<int> RecoverStaleUploadingChunksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            UPDATE recording_chunks
            SET status='FAILED',
                next_attempt_at=$now,
                last_error_code='RECORDER_CRASH_DURING_UPLOAD'
            WHERE status='UPLOADING'
              AND (last_attempt_at IS NULL OR last_attempt_at <= $cutoff)
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$cutoff", now.AddSeconds(-UploadStaleAfterSeconds).ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RecoverStalePlayableBuildsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET playable_audio_state='PENDING',playable_audio_error='RECORDER_RESTART_DURING_PLAYABLE_BUILD',playable_audio_next_retry_at=$now WHERE playable_audio_state='BUILDING'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateSessionAsync(string sessionId, Guid? meetingId = null, string? title = null, string? pipelineCorrelationId = null, CancellationToken cancellationToken = default, Guid? ownerUserId = null, bool localOnly = false, string acousticProfile = "AUTO")
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO recording_sessions(id,meeting_id,title,owner_user_id,state,started_at,local_finalize_state,delivery_state,meeting_bind_state,delivery_mode,acoustic_profile,pipeline_correlation_id,playable_audio_state) VALUES($id,$meeting,$title,$owner,'RECORDING',$started,'PENDING','NOT_REQUESTED','UNBOUND',$mode,$acoustic,$correlation,'PENDING')";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$meeting", (object?)meetingId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$owner", (object?)ownerUserId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$mode", localOnly ? "LOCAL_ONLY" : "AUTO");
        command.Parameters.AddWithValue("$acoustic", NormalizeAcousticProfile(acousticProfile));
        command.Parameters.AddWithValue("$correlation", pipelineCorrelationId ?? Guid.NewGuid().ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetMeetingIdAsync(string sessionId, Guid meetingId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET meeting_id=$meeting,meeting_bind_state='BOUND' WHERE id=$session";
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

    public async Task RefreshSessionTotalSamplesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET total_samples=COALESCE((SELECT MAX(start_sample + sample_count) FROM recording_raw_chunks WHERE session_id=$id AND status<>'DISCARDED' AND sample_count>0),0) WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetArchiveRetryAsync(string sessionId, DateTimeOffset nextRetryAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET archive_next_retry_at=$next WHERE id=$id";
        command.Parameters.AddWithValue("$next", nextRetryAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPlayableAudioStateAsync(
        string sessionId,
        string state,
        string? path = null,
        string? error = null,
        int? retryCount = null,
        DateTimeOffset? nextRetryAtUtc = null,
        bool clearError = false,
        bool clearNextRetry = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET playable_audio_state=$state,playable_audio_path=COALESCE($path,playable_audio_path),playable_audio_error=CASE WHEN $clearError=1 THEN NULL ELSE COALESCE($error,playable_audio_error) END,playable_audio_created_at=CASE WHEN $state='READY' THEN $created ELSE playable_audio_created_at END,playable_audio_retry_count=COALESCE($retry,playable_audio_retry_count),playable_audio_next_retry_at=CASE WHEN $clearNext=1 THEN NULL WHEN $next IS NOT NULL THEN $next ELSE playable_audio_next_retry_at END WHERE id=$id";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$clearError", clearError ? 1 : 0);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$retry", (object?)retryCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", (object?)nextRetryAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$clearNext", clearNextRetry ? 1 : 0);
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplacePlayableFilesAsync(string sessionId, IReadOnlyList<PlayableAudioFile> files, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM recording_playable_files WHERE session_id=$session";
            delete.Parameters.AddWithValue("$session", sessionId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var file in files)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO recording_playable_files(session_id,track_id,track_type,file_name,local_path,size_bytes,sample_count,sample_rate,channels,encoding,sha256,state,created_at) VALUES($session,$track,$type,$name,$path,$size,$samples,$rate,$channels,$encoding,$sha,$state,$created)";
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$track", file.TrackId);
            insert.Parameters.AddWithValue("$type", file.TrackType);
            insert.Parameters.AddWithValue("$name", file.FileName);
            insert.Parameters.AddWithValue("$path", file.LocalPath);
            insert.Parameters.AddWithValue("$size", file.SizeBytes);
            insert.Parameters.AddWithValue("$samples", file.SampleCount);
            insert.Parameters.AddWithValue("$rate", file.SampleRate);
            insert.Parameters.AddWithValue("$channels", file.Channels);
            insert.Parameters.AddWithValue("$encoding", file.Encoding);
            insert.Parameters.AddWithValue("$sha", file.Sha256);
            insert.Parameters.AddWithValue("$state", file.State);
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> SessionsNeedingPlayableAudioAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM recording_sessions WHERE state NOT IN ('RECORDING','PAUSED','CANCELLED','FINALIZED') AND local_finalize_state='LOCAL_READY' AND playable_audio_state IN ('PENDING','RECOVERY_PENDING','FAILED') AND (playable_audio_next_retry_at IS NULL OR playable_audio_next_retry_at <= $now) ORDER BY started_at";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<PlayableAudioFile>> GetPlayableFilesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id,track_id,track_type,file_name,local_path,size_bytes,sample_count,sample_rate,channels,encoding,sha256,state FROM recording_playable_files WHERE session_id=$session ORDER BY file_name";
        command.Parameters.AddWithValue("$session", sessionId);
        var result = new List<PlayableAudioFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new PlayableAudioFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9), reader.GetString(10), reader.GetString(11)));
        return result;
    }

    public async Task<IReadOnlyList<LocalSessionSummary>> ListLocalSessionsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,started_at,state,local_finalize_state,delivery_state,playable_audio_state,playable_audio_path,playable_audio_error,playable_audio_created_at,next_retry_at,playable_audio_next_retry_at FROM recording_sessions WHERE state NOT IN ('CANCELLED') ORDER BY started_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var result = new List<LocalSessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<(string Id, string? Title, DateTimeOffset? StartedAt, string State, string Local, string Delivery, string Playable, string? Path, string? Error, DateTimeOffset? Created, DateTimeOffset? Next, DateTimeOffset? PlayableNext)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset? Parse(int ordinal) => reader.IsDBNull(ordinal) || !DateTimeOffset.TryParse(reader.GetString(ordinal), out var value) ? null : value;
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), Parse(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), Parse(9), Parse(10), Parse(11)));
        }
        foreach (var row in rows)
            result.Add(new LocalSessionSummary(row.Id, row.Title, row.StartedAt, row.State, row.Local, row.Delivery, row.Playable,
                row.Path, row.Error, row.Created, row.Next, row.PlayableNext,
                await GetPlayableFilesAsync(row.Id, cancellationToken).ConfigureAwait(false)));
        return result;
    }

    public async Task SetMeetingBindStateAsync(string sessionId, string state, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET meeting_bind_state=$state WHERE id=$session";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$session", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertTrackInfoAsync(RecordingTrackInfo track, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO recording_track_info(track_id,session_id,track_type,sample_rate,channels,endpoint_id,device_name,selection_mode,profile,encoding,bits_per_sample,source_encoding,source_sub_format,valid_bits_per_sample,created_at) VALUES($track,$session,$type,$rate,$channels,$endpoint,$name,$mode,$profile,$encoding,$bits,$sourceEncoding,$sourceSubFormat,$validBits,$created) ON CONFLICT(track_id) DO UPDATE SET endpoint_id=excluded.endpoint_id,device_name=excluded.device_name,selection_mode=excluded.selection_mode,profile=excluded.profile,encoding=excluded.encoding,bits_per_sample=excluded.bits_per_sample,source_encoding=excluded.source_encoding,source_sub_format=excluded.source_sub_format,valid_bits_per_sample=excluded.valid_bits_per_sample";
        command.Parameters.AddWithValue("$track", track.TrackId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$type", track.TrackType);
        command.Parameters.AddWithValue("$rate", track.SampleRate);
        command.Parameters.AddWithValue("$channels", track.Channels);
        command.Parameters.AddWithValue("$endpoint", (object?)track.EndpointId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)track.DeviceFriendlyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", track.SelectionMode);
        command.Parameters.AddWithValue("$profile", track.Profile);
        command.Parameters.AddWithValue("$encoding", track.Encoding);
        command.Parameters.AddWithValue("$bits", track.BitsPerSample);
        command.Parameters.AddWithValue("$sourceEncoding", (object?)track.SourceEncoding ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceSubFormat", (object?)track.SourceSubFormat ?? DBNull.Value);
        command.Parameters.AddWithValue("$validBits", (object?)track.ValidBitsPerSample ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
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
        int? errorHttpStatus = null,
        bool? errorRetryable = null,
        string? traceId = null,
        bool clearNextRetry = false,
        bool clearError = false,
        bool preserveError = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET local_finalize_state=COALESCE($local,local_finalize_state), delivery_state=COALESCE($delivery,delivery_state), archive_path=COALESCE($archive,archive_path), last_error_code=CASE WHEN $preserveError=1 THEN last_error_code WHEN $clearError=1 THEN NULL ELSE $code END, last_error_detail=CASE WHEN $preserveError=1 THEN last_error_detail WHEN $clearError=1 THEN NULL ELSE $detail END, retry_count=COALESCE($retry,retry_count), next_retry_at=CASE WHEN $clearNext=1 THEN NULL WHEN $next IS NOT NULL THEN $next ELSE next_retry_at END, last_error_http_status=CASE WHEN $preserveError=1 THEN last_error_http_status WHEN $clearError=1 THEN NULL ELSE $httpStatus END, last_error_retryable=CASE WHEN $preserveError=1 THEN last_error_retryable WHEN $clearError=1 THEN NULL ELSE $retryable END, trace_id=COALESCE($trace,trace_id) WHERE id=$id";
        command.Parameters.AddWithValue("$local", (object?)localFinalizeState ?? DBNull.Value);
        command.Parameters.AddWithValue("$delivery", (object?)deliveryState ?? DBNull.Value);
        command.Parameters.AddWithValue("$archive", (object?)archivePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)errorDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("$retry", (object?)retryCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", (object?)nextRetryAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$clearNext", clearNextRetry ? 1 : 0);
        command.Parameters.AddWithValue("$clearError", clearError ? 1 : 0);
        command.Parameters.AddWithValue("$preserveError", preserveError ? 1 : 0);
        command.Parameters.AddWithValue("$httpStatus", (object?)errorHttpStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$retryable", (object?)(errorRetryable.HasValue ? (errorRetryable.Value ? 1 : 0) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$trace", (object?)traceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetArchiveErrorAsync(string sessionId, string errorCode, string detail, int retryCount, DateTimeOffset nextRetryAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET archive_error_code=$code,archive_error_detail=$detail,archive_retry_count=$retry,archive_next_retry_at=$next WHERE id=$id";
        command.Parameters.AddWithValue("$code", errorCode);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$retry", retryCount);
        command.Parameters.AddWithValue("$next", nextRetryAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearArchiveErrorAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET archive_error_code=NULL,archive_error_detail=NULL,archive_retry_count=0,archive_next_retry_at=NULL WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RecordingSessionInfo?> GetSessionInfoAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT meeting_id,title,started_at,state,local_finalize_state,delivery_state,archive_path,last_error_code,last_error_detail,retry_count,next_retry_at,media_asset_id,processing_job_id,trace_id,pipeline_correlation_id,server_accepted_at,media_validated_at,transport_purge_after,local_archive_purge_after,local_archive_purged_at,owner_user_id,last_error_http_status,last_error_retryable,meeting_bind_state,delivery_mode,archive_error_code,archive_error_detail,archive_retry_count,archive_next_retry_at,acoustic_profile,playable_audio_state,playable_audio_path,playable_audio_error,playable_audio_created_at,playable_audio_retry_count,playable_audio_next_retry_at FROM recording_sessions WHERE id=$session";
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        Guid? meetingId = reader.IsDBNull(0) ? null : Guid.TryParse(reader.GetString(0), out var parsed) ? parsed : null;
        DateTimeOffset? startedAt = reader.IsDBNull(2) ? null : DateTimeOffset.TryParse(reader.GetString(2), out var started) ? started : null;
        DateTimeOffset? nextRetry = reader.IsDBNull(10) ? null : DateTimeOffset.TryParse(reader.GetString(10), out var parsedRetry) ? parsedRetry : null;
        Guid? mediaAssetId = reader.IsDBNull(11) ? null : Guid.TryParse(reader.GetString(11), out var parsedAsset) ? parsedAsset : null;
        Guid? processingJobId = reader.IsDBNull(12) ? null : Guid.TryParse(reader.GetString(12), out var parsedJob) ? parsedJob : null;
        DateTimeOffset? serverAcceptedAt = reader.IsDBNull(15) ? null : DateTimeOffset.TryParse(reader.GetString(15), out var acceptedAt) ? acceptedAt : null;
        DateTimeOffset? mediaValidatedAt = reader.IsDBNull(16) ? null : DateTimeOffset.TryParse(reader.GetString(16), out var validatedAt) ? validatedAt : null;
        DateTimeOffset? transportPurgeAfter = reader.IsDBNull(17) ? null : DateTimeOffset.TryParse(reader.GetString(17), out var purgeAfter) ? purgeAfter : null;
        DateTimeOffset? localArchivePurgeAfter = reader.IsDBNull(18) ? null : DateTimeOffset.TryParse(reader.GetString(18), out var archivePurgeAfter) ? archivePurgeAfter : null;
        DateTimeOffset? localArchivePurgedAt = reader.IsDBNull(19) ? null : DateTimeOffset.TryParse(reader.GetString(19), out var archivePurgedAt) ? archivePurgedAt : null;
        Guid? ownerUserId = reader.IsDBNull(20) ? null : Guid.TryParse(reader.GetString(20), out var parsedOwner) ? parsedOwner : null;
        int? lastErrorHttpStatus = reader.IsDBNull(21) ? null : reader.GetInt32(21);
        bool? lastErrorRetryable = reader.IsDBNull(22) ? null : reader.GetInt32(22) != 0;
        var meetingBindState = reader.IsDBNull(23) ? "UNBOUND" : reader.GetString(23);
        var deliveryMode = reader.IsDBNull(24) ? "AUTO" : reader.GetString(24);
        DateTimeOffset? archiveRetryAt = reader.IsDBNull(28) ? null : DateTimeOffset.TryParse(reader.GetString(28), out var parsedArchiveRetry) ? parsedArchiveRetry : null;
        DateTimeOffset? playableCreatedAt = reader.IsDBNull(33) ? null : DateTimeOffset.TryParse(reader.GetString(33), out var parsedPlayableCreated) ? parsedPlayableCreated : null;
        DateTimeOffset? playableNextRetryAt = reader.IsDBNull(35) ? null : DateTimeOffset.TryParse(reader.GetString(35), out var parsedPlayableRetry) ? parsedPlayableRetry : null;
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
            nextRetry,
            mediaAssetId,
            processingJobId,
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            serverAcceptedAt,
            mediaValidatedAt,
            transportPurgeAfter,
            localArchivePurgeAfter,
            localArchivePurgedAt,
            ownerUserId,
            lastErrorHttpStatus,
            lastErrorRetryable,
            meetingBindState,
            deliveryMode,
            reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetString(26),
            reader.IsDBNull(27) ? 0 : reader.GetInt32(27),
            archiveRetryAt,
            reader.IsDBNull(29) ? "AUTO" : reader.GetString(29),
            reader.IsDBNull(30) ? "NOT_REQUIRED" : reader.GetString(30),
            reader.IsDBNull(31) ? null : reader.GetString(31),
            reader.IsDBNull(32) ? null : reader.GetString(32),
            playableCreatedAt,
            reader.IsDBNull(34) ? 0 : reader.GetInt32(34),
            playableNextRetryAt);
    }

    /// <summary>
    /// Determines whether a session crossed the local durability boundary.
    /// This deliberately checks the bytes on disk instead of trusting the
    /// session state: a failed START/STOP must never be reported as
    /// LOCAL_READY merely because a SQLite row was created.
    /// </summary>
    public async Task<LocalDurabilityOutcome> GetLocalDurabilityAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // A closed PCM can exist for a short interval between the atomic file
        // rename and the SQLite promotion. Reconcile only this session before
        // classifying durability; the global orphan scanner remains the
        // periodic safety net for old sessions.
        var orphanPart = await ReconcileExactSessionOrphansAsync(sessionId, cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.raw_path,r.output_path,r.status,r.start_sample,r.sample_count,r.sample_rate,r.channels,
                   r.bits_per_sample,r.track_id,r.sequence,r.track_type,r.encoding,
                   c.local_path,c.status,c.size_bytes,c.sha256,
                   t.track_type,t.sample_rate,t.channels,t.encoding,t.bits_per_sample
            FROM recording_raw_chunks r
            LEFT JOIN recording_chunks c ON c.id=r.id
            LEFT JOIN recording_track_info t ON t.track_id=r.track_id
            WHERE r.session_id=$session AND r.status<>'DISCARDED'
            ORDER BY r.track_id,r.sequence
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        var chunks = new List<RecordingTimelineChunk>();
        var seenChunkKeys = new HashSet<(string TrackId, int Sequence)>();
        var recoverable = orphanPart;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawPath = reader.GetString(0);
            var outputPath = reader.GetString(1);
            var status = reader.GetString(2);
            var startSample = reader.GetInt64(3);
            var sampleCount = reader.GetInt64(4);
            var sampleRate = reader.GetInt32(5);
            var channels = Math.Max(1, reader.GetInt32(6));
            var bits = Math.Max(8, reader.GetInt32(7));
            var trackId = reader.GetString(8);
            var sequence = reader.GetInt32(9);
            var trackType = reader.GetString(10);
            var encoding = reader.GetString(11);
            seenChunkKeys.Add((trackId, sequence));
            var encodedPath = reader.IsDBNull(12) ? null : reader.GetString(12);
            var encodedStatus = reader.IsDBNull(13) ? null : reader.GetString(13);
            var encodedSize = reader.IsDBNull(14) ? (long?)null : reader.GetInt64(14);
            var encodedSha = reader.IsDBNull(15) ? null : reader.GetString(15);
            var blockAlign = Math.Max(1, channels * (bits / 8));
            var expectedBytes = sampleCount > 0 ? sampleCount * blockAlign : 0;
            var rawDurable = false;

            if (File.Exists(rawPath))
            {
                var size = new FileInfo(rawPath).Length;
                // A non-empty, block-aligned PCM file is the durable capture
                // boundary.  The sample-rate value is read intentionally here
                // so malformed legacy rows do not qualify as audio.
                if (startSample >= 0 && sampleCount > 0 && sampleRate > 0 && size > 0 && size % blockAlign == 0 && size == expectedBytes)
                {
                    rawDurable = true;
                }
                // A crash can happen after the stream was closed/renamed but
                // before SQLite was promoted from WRITING.  The raw bytes are
                // still recoverable by RawChunkRecovery even though the old
                // row has sampleCount=0 or an incomplete timeline.
                if (string.Equals(status, "WRITING", StringComparison.OrdinalIgnoreCase) && size > 0)
                    recoverable = true;
            }

            var partPath = rawPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? rawPath : rawPath + ".part";
            if (File.Exists(partPath) && new FileInfo(partPath).Length > 0)
                recoverable = true;

            var encodedDurable = IsUsableFlac(
                    string.IsNullOrWhiteSpace(encodedPath) ? outputPath : encodedPath,
                    sampleRate,
                    channels,
                    bits,
                    sampleCount,
                    encodedStatus,
                    encodedSize,
                    encodedSha);

            chunks.Add(new RecordingTimelineChunk(
                trackId, sequence, trackType, encoding, startSample, sampleCount,
                sampleRate, channels, bits, rawDurable || encodedDurable,
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetInt32(17),
                reader.IsDBNull(18) ? null : reader.GetInt32(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetInt32(20)));
        }

        // Raw rows are intentionally purged after a verified FLAC reaches the
        // archive/delivery boundary.  Keep those encoded chunks in the local
        // durability view even after the raw metadata row is gone; otherwise a
        // delayed delivery retry could incorrectly turn a valid session into
        // NO_AUDIO_CAPTURED.
        await using (var encoded = connection.CreateCommand())
        {
            encoded.CommandText = """
                SELECT c.track_id,c.sequence,c.local_path,c.status,c.start_sample,c.sample_count,
                       c.sample_rate,c.channels,c.track_type,c.size_bytes,c.sha256,
                       t.track_type,t.sample_rate,t.channels,t.encoding,t.bits_per_sample
                FROM recording_chunks c
                LEFT JOIN recording_track_info t ON t.track_id=c.track_id
                WHERE c.session_id=$session
                ORDER BY c.track_id,c.sequence
                """;
            encoded.Parameters.AddWithValue("$session", sessionId);
            await using var encodedReader = await encoded.ExecuteReaderAsync(cancellationToken);
            while (await encodedReader.ReadAsync(cancellationToken))
            {
                var trackId = encodedReader.GetString(0);
                var sequence = encodedReader.GetInt32(1);
                if (!seenChunkKeys.Add((trackId, sequence)))
                    continue;

                var sampleRate = encodedReader.GetInt32(6);
                var channels = Math.Max(1, encodedReader.GetInt32(7));
                var trackType = encodedReader.GetString(8);
                var encodingName = encodedReader.IsDBNull(14) ? "IeeeFloat" : encodedReader.GetString(14);
                var bits = encodedReader.IsDBNull(15) ? 0 : Math.Max(0, encodedReader.GetInt32(15));
                var encodedPath = encodedReader.GetString(2);
                var encodedStatus = encodedReader.GetString(3);
                var encodedDurable = IsUsableFlac(
                    encodedPath,
                    sampleRate,
                    channels,
                    bits,
                    encodedReader.GetInt64(5),
                    encodedStatus,
                    encodedReader.GetInt64(9),
                    encodedReader.IsDBNull(10) ? null : encodedReader.GetString(10));

                chunks.Add(new RecordingTimelineChunk(
                    trackId,
                    sequence,
                    trackType,
                    encodingName,
                    encodedReader.GetInt64(4),
                    encodedReader.GetInt64(5),
                    sampleRate,
                    channels,
                    bits,
                    encodedDurable,
                    encodedReader.IsDBNull(11) ? null : encodedReader.GetString(11),
                    encodedReader.IsDBNull(12) ? null : encodedReader.GetInt32(12),
                    encodedReader.IsDBNull(13) ? null : encodedReader.GetInt32(13),
                    encodedReader.IsDBNull(14) ? null : encodedReader.GetString(14),
                    encodedReader.IsDBNull(15) ? null : encodedReader.GetInt32(15)));
            }
        }

        if (recoverable)
            return new LocalDurabilityOutcome("RECOVERY_PENDING", false, true, chunks.Count(c => c.IsDurable), "RAW_RECOVERY_PENDING");

        var validationError = RecordingTimelineValidator.Validate(chunks);
        if (validationError is not null)
            return new LocalDurabilityOutcome("LOCAL_FAILED", false, false, 0, validationError);

        var durable = chunks.Count(c => c.IsDurable);
        if (durable > 0)
            return new LocalDurabilityOutcome("LOCAL_READY", true, false, durable);
        return new LocalDurabilityOutcome("LOCAL_FAILED", false, false, 0, "NO_AUDIO_CAPTURED");
    }

    private async Task<bool> ReconcileExactSessionOrphansAsync(string sessionId, CancellationToken cancellationToken)
    {
        var sessionRoot = Path.Combine(_dataRoot, "recordings", sessionId);
        if (!Directory.Exists(sessionRoot)) return false;
        var hasRecoverablePart = false;
        string[] trackDirectories;
        try { trackDirectories = Directory.GetDirectories(sessionRoot); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        foreach (var directory in trackDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackId = Path.GetFileName(directory);
            var track = await GetTrackInfoAsync(trackId, cancellationToken).ConfigureAwait(false);
            if (track is null) continue;
            string[] files;
            try { files = Directory.GetFiles(directory, "*.pcm", SearchOption.TopDirectoryOnly); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (Directory.EnumerateFiles(directory, "*.pcm.part", SearchOption.TopDirectoryOnly)
                .Any(path => { try { return new FileInfo(path).Length > 0; } catch { return false; } }))
                hasRecoverablePart = true;
            foreach (var rawPath in files)
            {
                if (!RawChunkFileName.TryParse(rawPath, out var sequence, out var startSample, out var sampleCount)) continue;
                if (await RawChunkExistsAsync(sessionId, trackId, sequence, cancellationToken).ConfigureAwait(false)) continue;
                var info = new FileInfo(rawPath);
                var blockAlign = Math.Max(1, track.Channels * Math.Max(1, track.BitsPerSample / 8));
                if (sampleCount <= 0 || info.Length != checked(sampleCount * blockAlign)) continue;
                var output = Path.Combine(directory, $"{sequence:D8}.flac");
                var candidate = new RawRecordingChunk(
                    Guid.NewGuid().ToString("N"), sessionId, trackId, sequence, rawPath, output,
                    startSample, sampleCount, track.SampleRate, track.Channels, track.TrackType,
                    track.Encoding, track.BitsPerSample, "WRITING", 0, null, null,
                    track.SourceEncoding, track.SourceSubFormat, track.ValidBitsPerSample);
                try
                {
                    RegisterRawChunk(candidate);
                    await PromoteRawChunkReadyAsync(candidate, rawPath, sampleCount, info.Length,
                        ComputeSha256(rawPath), cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (ex.Message == "RAW_CHUNK_ROW_MISSING") { }
            }
        }
        return hasRecoverablePart;
    }

    /// <summary>
    /// Returns the authoritative sample-based end time for technical events.
    /// Client wall-clock timestamps are intentionally ignored; for a completed
    /// session the last durable PCM boundary is the only trusted timeline.
    /// </summary>
    public async Task<long?> GetSessionMediaTimeMsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(end_sample), MAX(sample_rate)
            FROM (
                SELECT start_sample + sample_count AS end_sample, sample_rate
                FROM recording_raw_chunks
                WHERE session_id=$session AND status NOT IN ('DISCARDED','ENCODE_TERMINAL_FAILED') AND sample_count>0
                UNION ALL
                SELECT start_sample + sample_count AS end_sample, sample_rate
                FROM recording_chunks
                WHERE session_id=$session AND status IN ('READY','UPLOADING','CONFIRMED') AND sample_count>0
            ) durable_timeline
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1)) return null;
        var endSample = reader.GetInt64(0);
        var sampleRate = reader.GetInt32(1);
        return endSample >= 0 && sampleRate > 0 ? (long)Math.Round(endSample * 1000d / sampleRate) : null;
    }

    private static bool IsUsableFlac(
        string path,
        int expectedSampleRate,
        int expectedChannels,
        int expectedBits,
        long expectedSamples,
        string? encodedStatus,
        long? encodedSize,
        string? encodedSha)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64) return false;
            if (encodedStatus is not null
                && !string.Equals(encodedStatus, "READY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(encodedStatus, "UPLOADING", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(encodedStatus, "CONFIRMED", StringComparison.OrdinalIgnoreCase)) return false;
            if (encodedSize is long expectedSize && expectedSize != info.Length) return false;
            if (!string.IsNullOrWhiteSpace(encodedSha)
                && !string.Equals(ComputeSha256(path), encodedSha, StringComparison.OrdinalIgnoreCase)) return false;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[4];
            if (!ReadExactly(stream, signature)
                || signature[0] != (byte)'f'
                || signature[1] != (byte)'L'
                || signature[2] != (byte)'a'
                || signature[3] != (byte)'C') return false;

            var foundStreamInfo = false;
            var actualRate = 0;
            var actualChannels = 0;
            var actualBits = 0;
            long actualSamples = 0;
            var lastBlock = false;
            Span<byte> lengthBytes = stackalloc byte[3];
            Span<byte> streamInfo = stackalloc byte[34];
            while (!lastBlock)
            {
                var blockHeader = stream.ReadByte();
                if (blockHeader < 0) return false;
                if (!ReadExactly(stream, lengthBytes)) return false;
                var length = (lengthBytes[0] << 16) | (lengthBytes[1] << 8) | lengthBytes[2];
                if (length > stream.Length - stream.Position) return false;
                lastBlock = (blockHeader & 0x80) != 0;
                var blockType = blockHeader & 0x7f;
                if (blockType == 0)
                {
                    if (foundStreamInfo || length != 34) return false;
                    if (!ReadExactly(stream, streamInfo)) return false;
                    ulong packed = 0;
                    for (var index = 10; index < 18; index++) packed = (packed << 8) | streamInfo[index];
                    actualRate = (int)(packed >> 44);
                    actualChannels = (int)((packed >> 41) & 0x7) + 1;
                    actualBits = (int)((packed >> 36) & 0x1f) + 1;
                    actualSamples = (long)(packed & 0x0f_ff_ff_ff_ffUL);
                    foundStreamInfo = true;
                }
                else
                {
                    stream.Seek(length, SeekOrigin.Current);
                }
            }

            if (!foundStreamInfo || stream.Position >= stream.Length
                || actualRate != expectedSampleRate
                || actualChannels != expectedChannels
                || (expectedBits > 0 && actualBits != expectedBits)
                || actualSamples <= 0
                || expectedSamples <= 0) return false;
            var tolerance = Math.Max(1, expectedSampleRate / 100);
            return Math.Abs(actualSamples - expectedSamples) <= tolerance;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (CryptographicException) { return false; }
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    public async Task SetServerReceiptAsync(string sessionId, Guid? meetingId, Guid? mediaAssetId, Guid? processingJobId, string? traceId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_sessions SET meeting_id=COALESCE($meeting,meeting_id),media_asset_id=COALESCE($asset,media_asset_id),processing_job_id=COALESCE($job,processing_job_id),trace_id=COALESCE($trace,trace_id),server_accepted_at=COALESCE(server_accepted_at,$accepted) WHERE id=$session";
        command.Parameters.AddWithValue("$meeting", (object?)meetingId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$asset", (object?)mediaAssetId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$job", (object?)processingJobId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$trace", (object?)traceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$accepted", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkMediaValidatedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        var policy = StorageRetentionPolicy.FromEnvironment();
        command.CommandText = "UPDATE recording_sessions SET media_validated_at=COALESCE(media_validated_at,$validated), transport_purge_after=COALESCE(transport_purge_after,$purgeAfter), local_archive_purge_after=COALESCE(local_archive_purge_after,$archivePurgeAfter) WHERE id=$session";
        command.Parameters.AddWithValue("$validated", now.ToString("O"));
        command.Parameters.AddWithValue("$purgeAfter", now.Add(policy.TransportGrace).ToString("O"));
        command.Parameters.AddWithValue("$archivePurgeAfter", policy.LocalMasterRetention <= TimeSpan.Zero ? DBNull.Value : now.Add(policy.LocalMasterRetention).ToString("O"));
        command.Parameters.AddWithValue("$session", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<ChunkDeliveryMetrics> GetChunkDeliveryMetricsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status,COUNT(*),COALESCE(SUM(CASE WHEN status<>'CONFIRMED' THEN size_bytes ELSE 0 END),0),MIN(CASE WHEN status<>'CONFIRMED' THEN created_at END) FROM recording_chunks WHERE session_id=$session GROUP BY status";
        command.Parameters.AddWithValue("$session", sessionId);
        var total = 0; var ready = 0; var uploading = 0; var confirmed = 0; var failed = 0; long bytesPending = 0; DateTimeOffset? oldest = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var status = reader.GetString(0); var count = reader.GetInt32(1); total += count;
            switch (status.ToUpperInvariant())
            {
                case "CONFIRMED": confirmed += count; break;
                case "UPLOADING": uploading += count; bytesPending += reader.GetInt64(2); break;
                case "FAILED":
                case "BLOCKED":
                    failed += count;
                    bytesPending += reader.GetInt64(2);
                    break;
                default: ready += count; bytesPending += reader.GetInt64(2); break;
            }
            if (!reader.IsDBNull(3) && DateTimeOffset.TryParse(reader.GetString(3), out var created) && (oldest is null || created < oldest)) oldest = created;
        }
        return new ChunkDeliveryMetrics(total, ready, uploading, confirmed, failed, bytesPending, oldest is null ? null : Math.Max(0, (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds));
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

    /// <summary>
    /// Returns every non-discarded raw chunk for local playable-file recovery.
    /// The raw timeline remains authoritative even when FLAC encoding has not
    /// started or failed; callers must run the continuity validator again.
    /// </summary>
    public async Task<IReadOnlyList<RawRecordingChunk>> GetRawChunksAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,track_id,sequence,raw_path,output_path,start_sample,sample_count,sample_rate,channels,track_type,encoding,bits_per_sample,status,raw_size_bytes,raw_sha256,error,source_encoding,source_sub_format,valid_bits_per_sample,encode_attempts,next_encode_attempt_at,last_encode_error_code,encoding_worker_id,encoding_started_at,encoding_lease_expires_at FROM recording_raw_chunks WHERE session_id=$session AND status<>'DISCARDED' ORDER BY track_id,sequence";
        command.Parameters.AddWithValue("$session", sessionId);
        var result = new List<RawRecordingChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadRawRecordingChunk(reader));
        return result;
    }

    public void RegisterRawChunk(RawRecordingChunk chunk)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_raw_chunks(id,session_id,track_id,sequence,raw_path,output_path,start_sample,sample_count,sample_rate,channels,track_type,encoding,bits_per_sample,source_encoding,source_sub_format,valid_bits_per_sample,status,created_at,updated_at)
            VALUES($id,$session,$track,$sequence,$raw,$output,$start,$count,$rate,$channels,$trackType,$encoding,$bits,$sourceEncoding,$sourceSubFormat,$validBits,'WRITING',$created,$created)
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
        command.Parameters.AddWithValue("$sourceEncoding", (object?)chunk.SourceEncoding ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceSubFormat", (object?)chunk.SourceSubFormat ?? DBNull.Value);
        command.Parameters.AddWithValue("$validBits", (object?)chunk.ValidBitsPerSample ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void MarkRawChunkReady(string sessionId, string trackId, int sequence, long sampleCount, long rawSizeBytes, string rawSha256)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_raw_chunks SET status='RAW_READY',sample_count=$sampleCount,raw_size_bytes=$size,raw_sha256=$sha,error=NULL,next_encode_attempt_at=NULL,last_encode_error_code=NULL,updated_at=$updated WHERE session_id=$session AND track_id=$track AND sequence=$sequence";
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
        command.CommandText = "UPDATE recording_raw_chunks SET status=$status,sample_count=COALESCE($sampleCount,sample_count),raw_size_bytes=COALESCE($size,raw_size_bytes),raw_sha256=COALESCE($sha,raw_sha256),error=$error,last_encode_error_code=CASE WHEN $status IN ('RAW_READY','READY') THEN NULL ELSE last_encode_error_code END,next_encode_attempt_at=CASE WHEN $status IN ('RAW_READY','READY') THEN NULL ELSE next_encode_attempt_at END,raw_purge_after=CASE WHEN $status='READY' THEN COALESCE(raw_purge_after,$rawPurgeAfter) ELSE raw_purge_after END,updated_at=$updated WHERE session_id=$session AND track_id=$track AND sequence=$sequence";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$sampleCount", (object?)sampleCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)rawSizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha", (object?)rawSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawPurgeAfter", status == "READY" ? DateTimeOffset.UtcNow.Add(StorageRetentionPolicy.FromEnvironment().RawRecoveryGrace).ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$track", trackId);
        command.Parameters.AddWithValue("$sequence", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PromoteRawChunkReadyAsync(
        RawRecordingChunk chunk,
        string durableRawPath,
        long sampleCount,
        long rawSizeBytes,
        string rawSha256,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE recording_raw_chunks SET raw_path=$raw,sample_count=$sampleCount,raw_size_bytes=$size,raw_sha256=$sha,error=NULL,last_encode_error_code=NULL,next_encode_attempt_at=NULL,encoding_worker_id=NULL,encoding_started_at=NULL,encoding_lease_expires_at=NULL,status='RAW_READY',updated_at=$updated WHERE session_id=$session AND track_id=$track AND sequence=$sequence";
        command.Parameters.AddWithValue("$raw", durableRawPath);
        command.Parameters.AddWithValue("$sampleCount", sampleCount);
        command.Parameters.AddWithValue("$size", rawSizeBytes);
        command.Parameters.AddWithValue("$sha", rawSha256);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", chunk.SessionId);
        command.Parameters.AddWithValue("$track", chunk.TrackId);
        command.Parameters.AddWithValue("$sequence", chunk.Sequence);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("RAW_CHUNK_ROW_MISSING");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Promotes a closed and atomically renamed PCM file to RAW_READY without
    /// making the realtime STOP path calculate its checksum. The checksum is
    /// filled by the background finalizer and is intentionally nullable until
    /// that work completes; the raw bytes and timeline are already durable.
    /// </summary>
    public async Task PromoteRawChunkDurableAsync(
        RawRecordingChunk chunk,
        string durableRawPath,
        long sampleCount,
        long rawSizeBytes,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE recording_raw_chunks SET raw_path=$raw,sample_count=$sampleCount,raw_size_bytes=$size,error=NULL,last_encode_error_code=NULL,next_encode_attempt_at=NULL,encoding_worker_id=NULL,encoding_started_at=NULL,encoding_lease_expires_at=NULL,status='RAW_READY',updated_at=$updated WHERE session_id=$session AND track_id=$track AND sequence=$sequence";
        command.Parameters.AddWithValue("$raw", durableRawPath);
        command.Parameters.AddWithValue("$sampleCount", sampleCount);
        command.Parameters.AddWithValue("$size", rawSizeBytes);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", chunk.SessionId);
        command.Parameters.AddWithValue("$track", chunk.TrackId);
        command.Parameters.AddWithValue("$sequence", chunk.Sequence);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("RAW_CHUNK_ROW_MISSING");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetRawChunkHashAsync(RawRecordingChunk chunk, string rawSha256, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_raw_chunks SET raw_sha256=$sha,updated_at=$updated WHERE session_id=$session AND track_id=$track AND sequence=$sequence AND status IN ('RAW_READY','ENCODING','READY')";
        command.Parameters.AddWithValue("$sha", rawSha256);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", chunk.SessionId);
        command.Parameters.AddWithValue("$track", chunk.TrackId);
        command.Parameters.AddWithValue("$sequence", chunk.Sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RawChunkExistsAsync(string sessionId, string trackId, int sequence, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM recording_raw_chunks WHERE session_id=$session AND track_id=$track AND sequence=$sequence)";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$track", trackId);
        command.Parameters.AddWithValue("$sequence", sequence);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private const string RawSelectColumns = "id,session_id,track_id,sequence,raw_path,output_path,start_sample,sample_count,sample_rate,channels,track_type,encoding,bits_per_sample,status,raw_size_bytes,raw_sha256,error,source_encoding,source_sub_format,valid_bits_per_sample,encode_attempts,next_encode_attempt_at,last_encode_error_code,encoding_worker_id,encoding_started_at,encoding_lease_expires_at";

    public async Task<IReadOnlyList<RawRecordingChunk>> RawChunksNeedingRecoveryAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RawSelectColumns} FROM recording_raw_chunks WHERE status IN ('WRITING','RAW_READY','ENCODING') OR (status='ENCODE_FAILED' AND (next_encode_attempt_at IS NULL OR next_encode_attempt_at <= $now)) ORDER BY updated_at,session_id,track_id,sequence LIMIT $limit";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var result = new List<RawRecordingChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRawRecordingChunk(reader));
        }
        return result;
    }

    public Task<RawRecordingChunk?> ClaimNextRawChunkForEncodingAsync(string? sessionId = null, CancellationToken cancellationToken = default) =>
        ClaimNextRawChunkForEncodingAsync(sessionId, "legacy", TimeSpan.FromMinutes(2), cancellationToken);

    /// <summary>Atomically claims one due raw chunk for the single global encoder.</summary>
    public async Task<RawRecordingChunk?> ClaimNextRawChunkForEncodingAsync(string? sessionId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var leaseExpires = now.Add(leaseDuration);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = $"SELECT {RawSelectColumns} FROM recording_raw_chunks WHERE (status='RAW_READY' OR (status='ENCODE_FAILED' AND (next_encode_attempt_at IS NULL OR next_encode_attempt_at <= $now))) AND ($session IS NULL OR session_id=$session) ORDER BY updated_at,session_id,track_id,sequence LIMIT 1";
        select.Parameters.AddWithValue("$now", now.ToString("O"));
        select.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var chunk = ReadRawRecordingChunk(reader);
        await reader.DisposeAsync();
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE recording_raw_chunks SET status='ENCODING',encode_attempts=encode_attempts+1,error=NULL,encoding_worker_id=$worker,encoding_started_at=$started,encoding_lease_expires_at=$expires,updated_at=$updated WHERE id=$id AND status IN ('RAW_READY','ENCODE_FAILED')";
        update.Parameters.AddWithValue("$worker", workerId);
        update.Parameters.AddWithValue("$started", now.ToString("O"));
        update.Parameters.AddWithValue("$expires", leaseExpires.ToString("O"));
        update.Parameters.AddWithValue("$updated", now.ToString("O"));
        update.Parameters.AddWithValue("$id", chunk.Id);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await transaction.CommitAsync(cancellationToken);
        return chunk with { Status = "ENCODING", EncodeAttempts = chunk.EncodeAttempts + 1, NextEncodeAttemptAtUtc = null, LastEncodeErrorCode = null, EncodingWorkerId = workerId, EncodingStartedAtUtc = now, EncodingLeaseExpiresAtUtc = leaseExpires };
    }

    public async Task<int> RecoverExpiredRawEncodingLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_raw_chunks SET status='RAW_READY',encoding_worker_id=NULL,encoding_started_at=NULL,encoding_lease_expires_at=NULL,next_encode_attempt_at=NULL,error='ENCODER_LEASE_EXPIRED',updated_at=$now WHERE status='ENCODING' AND encoding_lease_expires_at IS NOT NULL AND encoding_lease_expires_at <= $now";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Extends the ownership window of a raw encoder claim.  Completion and
    /// failure both verify the same worker id, so a late heartbeat from a
    /// crashed/restarted worker cannot keep an already reclaimed chunk locked.
    /// </summary>
    public async Task<bool> RenewRawEncodingLeaseAsync(
        RawRecordingChunk chunk,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = "UPDATE recording_raw_chunks SET encoding_lease_expires_at=$expires,updated_at=$updated WHERE id=$id AND status='ENCODING' AND encoding_worker_id=$worker AND encoding_lease_expires_at IS NOT NULL AND encoding_lease_expires_at > $now";
        command.Parameters.AddWithValue("$expires", now.Add(leaseDuration).ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.Parameters.AddWithValue("$id", chunk.Id);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> OwnsRawEncodingLeaseAsync(RawRecordingChunk chunk, string workerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM recording_raw_chunks WHERE id=$id AND status='ENCODING' AND encoding_worker_id=$worker AND encoding_lease_expires_at IS NOT NULL AND encoding_lease_expires_at > $now)";
        command.Parameters.AddWithValue("$id", chunk.Id);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public Task SetRawEncodingFailureAsync(RawRecordingChunk chunk, string errorCode, CancellationToken cancellationToken = default) =>
        SetRawEncodingFailureAsync(chunk, errorCode, chunk.EncodingWorkerId, cancellationToken);

    public async Task SetRawEncodingFailureAsync(RawRecordingChunk chunk, string errorCode, string? workerId, CancellationToken cancellationToken = default)
    {
        var attempt = Math.Max(1, chunk.EncodeAttempts);
        var baseSeconds = attempt switch { 1 => 2, 2 => 5, 3 => 15, 4 => 30, _ => 60 };
        var delay = Math.Min(300, baseSeconds) + Random.Shared.NextDouble() * Math.Max(1, baseSeconds * 0.2);
        var next = DateTimeOffset.UtcNow.AddSeconds(delay);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_raw_chunks SET status='ENCODE_FAILED',next_encode_attempt_at=$next,last_encode_error_code=$code,error=$error,encoding_worker_id=NULL,encoding_started_at=NULL,encoding_lease_expires_at=NULL,updated_at=$updated WHERE id=$id AND status='ENCODING' AND ($worker IS NULL OR encoding_worker_id=$worker)";
        command.Parameters.AddWithValue("$next", next.ToString("O"));
        command.Parameters.AddWithValue("$code", errorCode);
        command.Parameters.AddWithValue("$error", errorCode);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", chunk.Id);
        command.Parameters.AddWithValue("$worker", (object?)workerId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SetRawEncodingTerminalFailureAsync(RawRecordingChunk chunk, string errorCode, string? workerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_raw_chunks SET status='ENCODE_TERMINAL_FAILED',next_encode_attempt_at=NULL,last_encode_error_code=$code,error=$error,encoding_worker_id=NULL,encoding_started_at=NULL,encoding_lease_expires_at=NULL,updated_at=$updated WHERE id=$id AND status='ENCODING' AND ($worker IS NULL OR encoding_worker_id=$worker)";
        command.Parameters.AddWithValue("$code", errorCode);
        command.Parameters.AddWithValue("$error", errorCode);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", chunk.Id);
        command.Parameters.AddWithValue("$worker", (object?)workerId ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteRawEncodingAsync(RawRecordingChunk raw, string flacPath, string workerId, CancellationToken cancellationToken = default)
    {
        var size = new FileInfo(flacPath).Length;
        var sha = ComputeSha256(flacPath);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var chunk = connection.CreateCommand();
        chunk.Transaction = transaction;
        chunk.CommandText = "INSERT INTO recording_chunks(id,session_id,track_id,sequence,local_path,start_sample,sample_count,sample_rate,channels,track_type,size_bytes,sha256,status,attempts,created_at) VALUES($id,$session,$track,$sequence,$path,$start,$count,$rate,$channels,$trackType,$size,$sha,'READY',0,$created) ON CONFLICT(track_id,sequence) DO UPDATE SET local_path=excluded.local_path,start_sample=excluded.start_sample,sample_count=excluded.sample_count,size_bytes=excluded.size_bytes,sha256=excluded.sha256,status=CASE WHEN recording_chunks.status='CONFIRMED' THEN 'CONFIRMED' ELSE 'READY' END,attempts=CASE WHEN recording_chunks.status='CONFIRMED' THEN recording_chunks.attempts ELSE 0 END,last_attempt_at=CASE WHEN recording_chunks.status='CONFIRMED' THEN recording_chunks.last_attempt_at ELSE NULL END,next_attempt_at=CASE WHEN recording_chunks.status='CONFIRMED' THEN recording_chunks.next_attempt_at ELSE NULL END,last_error_code=CASE WHEN recording_chunks.status='CONFIRMED' THEN recording_chunks.last_error_code ELSE NULL END";
        chunk.Parameters.AddWithValue("$id", raw.Id);
        chunk.Parameters.AddWithValue("$session", raw.SessionId);
        chunk.Parameters.AddWithValue("$track", raw.TrackId);
        chunk.Parameters.AddWithValue("$sequence", raw.Sequence);
        chunk.Parameters.AddWithValue("$path", flacPath);
        chunk.Parameters.AddWithValue("$start", raw.StartSample);
        chunk.Parameters.AddWithValue("$count", raw.SampleCount);
        chunk.Parameters.AddWithValue("$rate", raw.SampleRate);
        chunk.Parameters.AddWithValue("$channels", raw.Channels);
        chunk.Parameters.AddWithValue("$trackType", raw.TrackType);
        chunk.Parameters.AddWithValue("$size", size);
        chunk.Parameters.AddWithValue("$sha", sha);
        chunk.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await chunk.ExecuteNonQueryAsync(cancellationToken);
        await using var rawUpdate = connection.CreateCommand();
        rawUpdate.Transaction = transaction;
        // raw_size_bytes/raw_sha256 describe the source PCM and must not be
        // overwritten with the derived FLAC size/hash. Encoded metadata lives
        // in recording_chunks above.
        rawUpdate.CommandText = "UPDATE recording_raw_chunks SET status='READY',error=NULL,last_encode_error_code=NULL,next_encode_attempt_at=NULL,raw_purge_after=COALESCE(raw_purge_after,$purge),encoding_worker_id=NULL,encoding_started_at=NULL,encoding_lease_expires_at=NULL,updated_at=$updated WHERE id=$id AND status='ENCODING' AND encoding_worker_id=$worker";
        rawUpdate.Parameters.AddWithValue("$purge", DateTimeOffset.UtcNow.Add(StorageRetentionPolicy.FromEnvironment().RawRecoveryGrace).ToString("O"));
        rawUpdate.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        rawUpdate.Parameters.AddWithValue("$id", raw.Id);
        rawUpdate.Parameters.AddWithValue("$worker", workerId);
        if (await rawUpdate.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("RAW_ENCODER_LEASE_LOST");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static RawRecordingChunk ReadRawRecordingChunk(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetString(10), reader.GetString(11), reader.GetInt32(12), reader.GetString(13), reader.GetInt64(14), reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetInt32(19), reader.IsDBNull(20) ? 0 : reader.GetInt32(20), reader.IsDBNull(21) || !DateTimeOffset.TryParse(reader.GetString(21), out var next) ? null : next, reader.IsDBNull(22) ? null : reader.GetString(22), reader.IsDBNull(23) ? null : reader.GetString(23), reader.IsDBNull(24) || !DateTimeOffset.TryParse(reader.GetString(24), out var started) ? null : started, reader.IsDBNull(25) || !DateTimeOffset.TryParse(reader.GetString(25), out var expires) ? null : expires);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public async Task<RawChunkBacklog> GetRawChunkBacklogAsync(string? sessionId = null, CancellationToken cancellationToken = default, bool includeFilesystemScan = false)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var sessionFilter = string.IsNullOrWhiteSpace(sessionId) ? "" : " AND session_id=$session";
        command.CommandText = $"SELECT status,COUNT(*),COALESCE(SUM(raw_size_bytes),0),MIN(updated_at) FROM recording_raw_chunks WHERE status NOT IN ('READY','DISCARDED'){sessionFilter} GROUP BY status";
        if (!string.IsNullOrWhiteSpace(sessionId)) command.Parameters.AddWithValue("$session", sessionId);
        var counts = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? oldest = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt64(2));
            if (!reader.IsDBNull(3) && DateTimeOffset.TryParse(reader.GetString(3), out var timestamp) && (oldest is null || timestamp < oldest)) oldest = timestamp;
        }
        await reader.DisposeAsync();
        var writing = counts.GetValueOrDefault("WRITING").Count;
        var rawReady = counts.GetValueOrDefault("RAW_READY").Count;
        var encoding = counts.GetValueOrDefault("ENCODING").Count;
        var failed = counts.GetValueOrDefault("ENCODE_FAILED").Count;
        var terminalFailed = counts.GetValueOrDefault("ENCODE_TERMINAL_FAILED").Count;
        // Full orphan discovery belongs to startup/Doctor recovery, not to
        // the frequent health/reconciliation path.
        var diskFallback = includeFilesystemScan && string.IsNullOrWhiteSpace(sessionId)
            ? await GetUnregisteredClosedRawBacklogAsync(cancellationToken)
            : (Count: 0, Bytes: 0L, Oldest: (DateTimeOffset?)null);
        writing += diskFallback.Count;
        if (diskFallback.Oldest is not null && (oldest is null || diskFallback.Oldest < oldest)) oldest = diskFallback.Oldest;
        var pending = writing + rawReady + encoding + failed + terminalFailed;
        double? ageMs = oldest is null ? null : Math.Max(0, (DateTimeOffset.UtcNow - oldest.Value).TotalMilliseconds);
        var health = pending > 10 || ageMs is > 120_000d ? "CRITICAL" : pending > 3 ? "LAGGING" : "HEALTHY";
        string? lastErrorCode = null;
        await using var errorCommand = connection.CreateCommand();
        errorCommand.CommandText = $"SELECT last_encode_error_code FROM recording_raw_chunks WHERE status IN ('ENCODE_FAILED','ENCODE_TERMINAL_FAILED') AND last_encode_error_code IS NOT NULL{sessionFilter} ORDER BY updated_at DESC LIMIT 1";
        if (!string.IsNullOrWhiteSpace(sessionId)) errorCommand.Parameters.AddWithValue("$session", sessionId);
        var lastError = await errorCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (lastError is string code) lastErrorCode = code;
        await using var readyCommand = connection.CreateCommand();
        readyCommand.CommandText = $"SELECT COUNT(*) FROM recording_chunks WHERE status='READY'{(string.IsNullOrWhiteSpace(sessionId) ? "" : " AND session_id=$session")}";
        if (!string.IsNullOrWhiteSpace(sessionId)) readyCommand.Parameters.AddWithValue("$session", sessionId);
        var readyForUpload = Convert.ToInt32(await readyCommand.ExecuteScalarAsync(cancellationToken));
        await using var rawTotalsCommand = connection.CreateCommand();
        rawTotalsCommand.CommandText = $"SELECT COUNT(*),COALESCE(SUM(CASE WHEN status='READY' THEN 1 ELSE 0 END),0) FROM recording_raw_chunks WHERE status<>'DISCARDED'{sessionFilter}";
        if (!string.IsNullOrWhiteSpace(sessionId)) rawTotalsCommand.Parameters.AddWithValue("$session", sessionId);
        await using var rawTotals = await rawTotalsCommand.ExecuteReaderAsync(cancellationToken);
        var total = 0;
        var completed = 0;
        if (await rawTotals.ReadAsync(cancellationToken))
        {
            total = rawTotals.GetInt32(0);
            completed = rawTotals.GetInt32(1);
        }
        var configuredCapacity = int.TryParse(Environment.GetEnvironmentVariable("ATOM_RAW_FINALIZER_QUEUE_CAPACITY"), out var capacity)
            ? Math.Clamp(capacity, 2, 32)
            : 4;
        return new RawChunkBacklog(
            pending,
            writing,
            encoding,
            failed,
            counts.Values.Sum(item => item.Bytes) + diskFallback.Bytes,
            ageMs,
            health,
            rawReady,
            readyForUpload,
            total,
            completed,
            terminalFailed,
            0,
            0,
            configuredCapacity,
            lastErrorCode);
    }

    private async Task<(int Count, long Bytes, DateTimeOffset? Oldest)> GetUnregisteredClosedRawBacklogAsync(CancellationToken cancellationToken)
    {
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT raw_path FROM recording_raw_chunks";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) knownPaths.Add(Path.GetFullPath(reader.GetString(0)));
        }

        var dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        var roots = new[]
        {
            Path.Combine(dataRoot, "sessions"),
            Path.Combine(dataRoot, "recordings")
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) return (0, 0, null);

        var count = 0;
        long bytes = 0;
        DateTimeOffset? oldest = null;
        try
        {
            foreach (var root in roots)
            foreach (var path in Directory.EnumerateFiles(root, "*.pcm", SearchOption.AllDirectories))
            {
                if (knownPaths.Contains(Path.GetFullPath(path))) continue;
                var info = new FileInfo(path);
                if (info.Length <= 0) continue;
                count++;
                bytes += info.Length;
                var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (oldest is null || written < oldest) oldest = written;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (count, bytes, oldest);
    }

    public async Task AddEventAsync(string sessionId, string eventType, long? mediaTimeMs = null, string payloadJson = "{}", CancellationToken cancellationToken = default, string? eventId = null, DateTimeOffset? createdAtUtc = null)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO recording_events(id,session_id,event_type,media_time_ms,payload_json,created_at) VALUES($id,$session,$type,$media,$payload,$created)";
        command.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(eventId) ? Guid.NewGuid().ToString("N") : eventId.Trim());
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$media", (object?)mediaTimeMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$created", (createdAtUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Persists a voice START command before a Recorder session exists. The row
    /// is promoted to recording_events once the session receives its first
    /// durable frame. The event id is supplied by the caller and is therefore
    /// safe to replay after a broker/IPC timeout.
    /// </summary>
    public async Task AddPendingEventAsync(string eventId, string eventType, string payloadJson = "{}", DateTimeOffset? createdAtUtc = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("Event id is required.", nameof(eventId));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO recording_pending_events(id,event_type,payload_json,created_at) VALUES($id,$type,$payload,$created)";
        command.Parameters.AddWithValue("$id", eventId.Trim());
        command.Parameters.AddWithValue("$type", eventType.Trim());
        command.Parameters.AddWithValue("$payload", string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        command.Parameters.AddWithValue("$created", (createdAtUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> AttachPendingEventAsync(string eventId, string sessionId, long? mediaTimeMs = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(sessionId)) return false;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new SqliteCommand("SELECT event_type,payload_json,created_at FROM recording_pending_events WHERE id=$id", connection, tx);
        select.Parameters.AddWithValue("$id", eventId.Trim());
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return false;
        var eventType = reader.GetString(0);
        var payload = reader.GetString(1);
        var created = reader.GetString(2);
        await reader.DisposeAsync();
        await using var insert = new SqliteCommand("INSERT OR IGNORE INTO recording_events(id,session_id,event_type,media_time_ms,payload_json,created_at) VALUES($id,$session,$type,$media,$payload,$created)", connection, tx);
        insert.Parameters.AddWithValue("$id", eventId.Trim());
        insert.Parameters.AddWithValue("$session", sessionId);
        insert.Parameters.AddWithValue("$type", eventType);
        insert.Parameters.AddWithValue("$media", (object?)mediaTimeMs ?? DBNull.Value);
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$created", created);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await using var delete = new SqliteCommand("DELETE FROM recording_pending_events WHERE id=$id", connection, tx);
        delete.Parameters.AddWithValue("$id", eventId.Trim());
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task RemovePendingEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recording_pending_events WHERE id=$id";
        command.Parameters.AddWithValue("$id", eventId.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PrunePendingEventsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recording_pending_events WHERE created_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(maxAge).ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddEventIfMissingAsync(string sessionId, string eventType, string payloadJson = "{}", CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_events(id,session_id,event_type,media_time_ms,payload_json,created_at)
            SELECT $id,$session,$type,NULL,$payload,$created
            WHERE NOT EXISTS (
                SELECT 1 FROM recording_events WHERE session_id=$session AND event_type=$type
            )
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$type", eventType);
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
        command.CommandText = """
            SELECT track_id,track_type,sample_rate,channels,endpoint_id,device_name,selection_mode,profile,encoding,bits_per_sample,source_encoding,source_sub_format,valid_bits_per_sample
            FROM recording_track_info WHERE session_id=$session
            UNION ALL
            SELECT c.track_id,c.track_type,c.sample_rate,c.channels,NULL,NULL,'DEFAULT','ROOM','IeeeFloat',32,NULL,NULL,NULL
            FROM recording_chunks c
            WHERE c.session_id=$session AND NOT EXISTS (SELECT 1 FROM recording_track_info i WHERE i.track_id=c.track_id)
            GROUP BY c.track_id,c.track_type,c.sample_rate,c.channels
            ORDER BY track_id
            """;
        command.Parameters.AddWithValue("$session", localSessionId);
        var result = new List<RecordingTrackInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var trackId = reader.GetString(0);
            result.Add(new RecordingTrackInfo(trackId, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? "DEFAULT" : reader.GetString(6), reader.IsDBNull(7) ? "ROOM" : reader.GetString(7),
                reader.IsDBNull(8) ? "IeeeFloat" : reader.GetString(8), reader.IsDBNull(9) ? 32 : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        }
        return result;
    }

    public async Task<RecordingTrackInfo?> GetTrackInfoAsync(string trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT track_id,track_type,sample_rate,channels,endpoint_id,device_name,selection_mode,profile,encoding,bits_per_sample,source_encoding,source_sub_format,valid_bits_per_sample FROM recording_track_info WHERE track_id=$track";
        command.Parameters.AddWithValue("$track", trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new RecordingTrackInfo(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? "DEFAULT" : reader.GetString(6), reader.IsDBNull(7) ? "ROOM" : reader.GetString(7),
            reader.IsDBNull(8) ? "IeeeFloat" : reader.GetString(8), reader.IsDBNull(9) ? 32 : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetInt32(12));
    }

    public async Task<long> GetNextTrackStartSampleAsync(string trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(end_sample),0)
            FROM (
                SELECT start_sample + sample_count AS end_sample FROM recording_raw_chunks WHERE track_id=$track
                UNION ALL
                SELECT start_sample + sample_count AS end_sample FROM recording_chunks WHERE track_id=$track
            )
            """;
        command.Parameters.AddWithValue("$track", trackId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
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
        command.CommandText = "SELECT id FROM recording_sessions WHERE state NOT IN ('CANCELLED','FINALIZED') AND (state<>'FAILED' OR (last_error_retryable=1 AND next_retry_at IS NOT NULL AND next_retry_at <= $now)) AND (((local_finalize_state<>'LOCAL_FAILED') AND ((next_retry_at IS NOT NULL AND next_retry_at <= $now) OR (next_retry_at IS NULL AND local_finalize_state IN ('PENDING','FINALIZING_LOCAL')) OR (next_retry_at IS NULL AND state IN ('RECORDING','PAUSED')) OR (next_retry_at IS NULL AND state='FINALIZING' AND (finished_at IS NULL OR finished_at <= $cutoff)))) OR (local_finalize_state='LOCAL_FAILED' AND delivery_state IN ('RECONCILING','WAITING_SERVER','WAITING_SERVER_ASSEMBLY','PENDING_SERVER') AND (next_retry_at IS NULL OR next_retry_at <= $now)) OR (local_finalize_state='LOCAL_READY' AND delivery_state IN ('PENDING_SERVER','NOT_REQUESTED') AND (next_retry_at IS NULL OR next_retry_at <= $now)) OR (local_finalize_state='LOCAL_READY' AND archive_path IS NULL AND (archive_next_retry_at IS NULL OR archive_next_retry_at <= $now))) ORDER BY started_at";
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
        command.CommandText = "SELECT COUNT(*) FROM recording_sessions WHERE state NOT IN ('CANCELLED','FINALIZED') AND (state<>'FAILED' OR (last_error_retryable=1 AND next_retry_at IS NOT NULL)) AND (delivery_state NOT IN ('DELIVERY_ERROR','DELIVERY_FAILED') OR (last_error_retryable=1 AND next_retry_at IS NOT NULL)) AND (state IN ('FINALIZING') OR local_finalize_state IN ('PENDING','FINALIZING_LOCAL','RECOVERY_PENDING','LOCAL_FAILED') OR delivery_state IN ('BINDING','UPLOADING','RECONCILING','FINALIZING_SERVER','WAITING_SERVER','WAITING_SERVER_ASSEMBLY','PENDING_SERVER','DELIVERY_ERROR','DELIVERY_FAILED'))";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<int> BackgroundPendingUploadSessionCountAsync(string? activeSessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM recording_sessions
            WHERE state NOT IN ('CANCELLED','FINALIZED')
              AND ($active IS NULL OR id <> $active)
              AND (state<>'FAILED' OR (last_error_retryable=1 AND next_retry_at IS NOT NULL))
              AND (delivery_state NOT IN ('DELIVERY_ERROR','DELIVERY_FAILED') OR (last_error_retryable=1 AND next_retry_at IS NOT NULL))
              AND (
                    state IN ('FINALIZING')
                    OR local_finalize_state IN ('PENDING','FINALIZING_LOCAL','RECOVERY_PENDING','LOCAL_FAILED')
                    OR delivery_state IN ('BINDING','UPLOADING','RECONCILING','FINALIZING_SERVER','WAITING_SERVER','WAITING_SERVER_ASSEMBLY','PENDING_SERVER','DELIVERY_ERROR','DELIVERY_FAILED')
                  )
            """;
        command.Parameters.AddWithValue("$active", (object?)activeSessionId ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<int> BackgroundFailedSessionCountAsync(string? activeSessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM recording_sessions
            WHERE state NOT IN ('CANCELLED','FINALIZED')
              AND ($active IS NULL OR id <> $active)
              AND (
                    local_finalize_state = 'LOCAL_FAILED'
                    OR delivery_state IN ('DELIVERY_ERROR','DELIVERY_FAILED')
                    OR state = 'FAILED'
                  )
            """;
        command.Parameters.AddWithValue("$active", (object?)activeSessionId ?? DBNull.Value);
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
            INSERT INTO recording_chunks(id,session_id,track_id,sequence,local_path,start_sample,sample_count,sample_rate,channels,track_type,size_bytes,sha256,status,attempts,last_attempt_at,next_attempt_at,last_error_code,created_at)
            VALUES($id,$session,$track,$sequence,$path,$start,$count,$rate,$channels,$trackType,$size,$sha,$status,$attempts,$lastAttempt,$nextAttempt,$lastError,$created)
            ON CONFLICT(track_id,sequence) DO UPDATE SET local_path=excluded.local_path,track_type=excluded.track_type,size_bytes=excluded.size_bytes,sha256=excluded.sha256,status=excluded.status,attempts=0,last_attempt_at=NULL,next_attempt_at=NULL,last_error_code=NULL;
            """;
        command.Parameters.AddWithValue("$id", chunk.Id); command.Parameters.AddWithValue("$session", chunk.SessionId); command.Parameters.AddWithValue("$track", chunk.TrackId); command.Parameters.AddWithValue("$sequence", chunk.Sequence); command.Parameters.AddWithValue("$path", chunk.LocalPath); command.Parameters.AddWithValue("$start", chunk.StartSample); command.Parameters.AddWithValue("$count", chunk.SampleCount); command.Parameters.AddWithValue("$rate", chunk.SampleRate); command.Parameters.AddWithValue("$channels", chunk.Channels); command.Parameters.AddWithValue("$trackType", chunk.TrackType); command.Parameters.AddWithValue("$size", chunk.SizeBytes); command.Parameters.AddWithValue("$sha", chunk.Sha256); command.Parameters.AddWithValue("$status", chunk.Status); command.Parameters.AddWithValue("$attempts", chunk.Attempts); command.Parameters.AddWithValue("$lastAttempt", (object?)chunk.LastAttemptAtUtc?.ToString("O") ?? DBNull.Value); command.Parameters.AddWithValue("$nextAttempt", (object?)chunk.NextAttemptAtUtc?.ToString("O") ?? DBNull.Value); command.Parameters.AddWithValue("$lastError", (object?)chunk.LastErrorCode ?? DBNull.Value); command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<RecordingChunk>> PendingChunksAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        PendingChunksAsync(null, limit, cancellationToken);

    public async Task<IReadOnlyList<RecordingChunk>> PendingChunksAsync(string? sessionId, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH eligible AS (
                SELECT c.id,c.session_id,c.track_id,c.sequence,c.local_path,c.start_sample,c.sample_count,c.sample_rate,c.channels,c.track_type,c.size_bytes,c.sha256,c.status,c.attempts,c.last_attempt_at,c.next_attempt_at,c.last_error_code,
                       CASE WHEN s.state IN ('RECORDING','PAUSED') THEN 0 WHEN s.state IN ('FINALIZING','FINALIZE_ACCEPTED') THEN 1 ELSE 2 END AS priority,
                       ROW_NUMBER() OVER (PARTITION BY c.session_id ORDER BY c.track_id,c.sequence) AS session_rank
                FROM recording_chunks c
                JOIN recording_sessions s ON s.id=c.session_id
                WHERE c.status NOT IN ('CONFIRMED','CANCELLED','BLOCKED','UPLOADING')
                  AND s.state<>'CANCELLED'
                  AND ($session IS NULL OR c.session_id=$session)
                  AND (c.next_attempt_at IS NULL OR c.next_attempt_at <= $now)
            )
            SELECT id,session_id,track_id,sequence,local_path,start_sample,sample_count,sample_rate,channels,track_type,size_bytes,sha256,status,attempts,last_attempt_at,next_attempt_at,last_error_code
            FROM eligible
            ORDER BY priority,session_rank,session_id,track_id,sequence
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        var result = new List<RecordingChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new RecordingChunk(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
            reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9),
            reader.GetInt64(10), reader.GetString(11), reader.GetString(12), reader.GetInt32(13),
            ParseDate(reader, 14), ParseDate(reader, 15), reader.IsDBNull(16) ? null : reader.GetString(16)));
        return result;
    }

    public async Task<bool> TryBeginChunkUploadAsync(RecordingChunk chunk, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_chunks SET status='UPLOADING',attempts=attempts+1,last_attempt_at=$attempt,next_attempt_at=NULL,last_error_code=NULL WHERE track_id=$track AND sequence=$sequence AND status IN ('READY','FAILED') AND (next_attempt_at IS NULL OR next_attempt_at <= $now)";
        command.Parameters.AddWithValue("$attempt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$track", chunk.TrackId);
        command.Parameters.AddWithValue("$sequence", chunk.Sequence);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task MarkUploadFailedAsync(RecordingChunk chunk, string errorCode, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var retryNumber = Math.Max(1, chunk.Attempts + 1);
        var baseSeconds = retryNumber switch { 1 => 2, 2 => 5, 3 => 15, 4 => 30, _ => 60 };
        var delay = Math.Min(300, baseSeconds) + Random.Shared.NextDouble() * Math.Max(1, baseSeconds * 0.2);
        var next = DateTimeOffset.UtcNow.AddSeconds(delay);
        var terminal = IsTerminalChunkError(errorCode);
        command.CommandText = "UPDATE recording_chunks SET status=$status,next_attempt_at=$next,last_error_code=$error WHERE track_id=$track AND sequence=$sequence AND status IN ('UPLOADING','READY','FAILED')";
        command.Parameters.AddWithValue("$status", terminal ? "BLOCKED" : "FAILED");
        command.Parameters.AddWithValue("$next", terminal ? DBNull.Value : next.ToString("O"));
        command.Parameters.AddWithValue("$error", errorCode);
        command.Parameters.AddWithValue("$track", chunk.TrackId);
        command.Parameters.AddWithValue("$sequence", chunk.Sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> UnblockTerminalUploadsAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_chunks SET status='READY',next_attempt_at=NULL,last_error_code=NULL WHERE status='BLOCKED' AND ($session IS NULL OR session_id=$session)";
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetBlockedChunkErrorAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_error_code FROM recording_chunks WHERE session_id=$session AND status='BLOCKED' ORDER BY track_id,sequence LIMIT 1";
        command.Parameters.AddWithValue("$session", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static bool IsTerminalChunkError(string errorCode) => errorCode.ToUpperInvariant() switch
    {
        "AGENT_AUTH_REJECTED" or
        "AGENT_USER_LINK_REQUIRED" or
        "OWNER_REQUIRED" or
        "OWNER_AUTHORIZATION_REJECTED" or
        "MEETING_OWNER_MISMATCH" or
        "MEETING_NOT_FOUND" or
        "MEETING_CANCELLED" or
        "LOCAL_CHUNK_MISSING" or
        "LOCAL_CHUNK_INVALID" or
        "RECORDING_ARCHIVE_ACCESS_DENIED" or
        "FFMPEG_UNAVAILABLE" => true,
        _ => false
    };

    public async Task MarkConfirmedAsync(string trackId, int sequence, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recording_chunks SET status='CONFIRMED',confirmed_at=$confirmed,next_attempt_at=NULL,last_error_code=NULL WHERE track_id=$track AND sequence=$sequence";
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

    public async Task<IReadOnlyList<RetentionCandidate>> GetTransportPurgeCandidatesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.transport_purge_after,c.local_path,c.size_bytes
            FROM recording_sessions s
            JOIN recording_chunks c ON c.session_id=s.id AND c.status='CONFIRMED'
            WHERE s.media_validated_at IS NOT NULL
              AND s.transport_purge_after IS NOT NULL AND s.transport_purge_after <= $now
              AND s.delivery_state IN ('CONFIRMED','COMPLETED')
              AND s.local_finalize_state='LOCAL_READY' AND s.archive_path IS NOT NULL
              AND s.playable_audio_state IN ('READY','NOT_REQUIRED')
              AND NOT EXISTS (SELECT 1 FROM recording_chunks pending WHERE pending.session_id=s.id AND pending.status<>'CONFIRMED')
              AND NOT EXISTS (SELECT 1 FROM recording_raw_chunks raw WHERE raw.session_id=s.id AND raw.status<>'READY')
            ORDER BY s.transport_purge_after,c.local_path
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        var grouped = new Dictionary<string, (DateTimeOffset PurgeAfter, List<string> Paths, long Bytes)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!DateTimeOffset.TryParse(reader.GetString(1), out var purgeAfter)) continue;
            var entry = grouped.TryGetValue(reader.GetString(0), out var existing)
                ? existing : (PurgeAfter: purgeAfter, Paths: new List<string>(), Bytes: 0L);
            entry.Paths.Add(reader.GetString(2));
            entry.Bytes += reader.GetInt64(3);
            grouped[reader.GetString(0)] = entry;
        }
        return grouped.Select(item => new RetentionCandidate(item.Key, "transport", item.Value.Paths, item.Value.Bytes, item.Value.PurgeAfter)).ToArray();
    }

    public async Task<IReadOnlyList<RetentionCandidate>> GetLocalArchivePurgeCandidatesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,archive_path,local_archive_purge_after
            FROM recording_sessions
            WHERE media_validated_at IS NOT NULL
              AND delivery_state IN ('CONFIRMED','COMPLETED')
              AND local_finalize_state='LOCAL_READY'
              AND local_archive_purge_after IS NOT NULL AND local_archive_purge_after <= $now
              AND local_archive_purged_at IS NULL
            ORDER BY local_archive_purge_after
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        var result = new List<RetentionCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(1) || !DateTimeOffset.TryParse(reader.GetString(2), out var purgeAfter)) continue;
            var directory = reader.GetString(1);
            // Metadata remains in manifest.json; only derived/local audio can be
            // reclaimed after the server copy is confirmed.
            var paths = new[] { Path.Combine(directory, "export", "master.flac"), Path.Combine(directory, "export", "preview.opus") }
                .Where(File.Exists).ToArray();
            var bytes = paths.Sum(path => new FileInfo(path).Length);
            result.Add(new RetentionCandidate(reader.GetString(0), "localArchive", paths, bytes, purgeAfter));
        }
        return result;
    }

    public async Task PurgeEligibleRawRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT r.session_id,r.track_id,r.sequence,r.raw_path
            FROM recording_raw_chunks r
            WHERE r.status='READY' AND (r.error IS NULL OR r.error='')
              AND r.raw_purge_after IS NOT NULL AND r.raw_purge_after <= $now
              AND EXISTS (SELECT 1 FROM recording_chunks c WHERE c.session_id=r.session_id AND c.track_id=r.track_id AND c.sequence=r.sequence AND c.status IN ('READY','UPLOADING','CONFIRMED'))
              AND EXISTS (SELECT 1 FROM recording_sessions s WHERE s.id=r.session_id AND s.playable_audio_state IN ('READY','NOT_REQUIRED'))
            """;
        select.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var rows = new List<(string SessionId, string TrackId, int Sequence, string RawPath)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));

        foreach (var row in rows)
        {
            try { if (File.Exists(row.RawPath)) File.Delete(row.RawPath); }
            catch (IOException) { continue; }
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM recording_raw_chunks WHERE session_id=$session AND track_id=$track AND sequence=$sequence AND status='READY' AND (error IS NULL OR error='') AND raw_purge_after <= $now";
            delete.Parameters.AddWithValue("$session", row.SessionId);
            delete.Parameters.AddWithValue("$track", row.TrackId);
            delete.Parameters.AddWithValue("$sequence", row.Sequence);
            delete.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task PurgeEligibleLocalArchivesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var candidate in await GetLocalArchivePurgeCandidatesAsync(DateTimeOffset.UtcNow, cancellationToken))
        {
            var failed = false;
            foreach (var path in candidate.Paths)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { failed = true; }
            }
            if (failed) continue;
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE recording_sessions SET local_archive_purged_at=$now WHERE id=$session AND local_archive_purged_at IS NULL";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$session", candidate.SessionId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task PurgeFinalizedSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var candidate = (await GetTransportPurgeCandidatesAsync(DateTimeOffset.UtcNow, cancellationToken))
            .SingleOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
        // A 202 from server finalize only queues assembly. Pending, failed, and
        // grace-protected transport remains recoverable on disk.
        if (candidate is null) return;

        var deleteFailed = false;
        foreach (var path in candidate.Paths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { deleteFailed = true; }
        }
        // Keep SQLite rows when any file is locked so the next controlled pass
        // can retry from state rather than assuming a successful purge.
        if (deleteFailed) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var cleanupRaw = connection.CreateCommand())
        {
            cleanupRaw.Transaction = transaction;
            cleanupRaw.CommandText = "DELETE FROM recording_raw_chunks WHERE session_id=$session AND status='READY' AND (error IS NULL OR error='')";
            cleanupRaw.Parameters.AddWithValue("$session", sessionId);
            await cleanupRaw.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var cleanupChunks = connection.CreateCommand())
        {
            cleanupChunks.Transaction = transaction;
            cleanupChunks.CommandText = "DELETE FROM recording_chunks WHERE session_id=$session AND status='CONFIRMED'";
            cleanupChunks.Parameters.AddWithValue("$session", sessionId);
            await cleanupChunks.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var finalize = connection.CreateCommand())
        {
            finalize.Transaction = transaction;
            finalize.CommandText = "UPDATE recording_sessions SET state='FINALIZED' WHERE id=$session";
            finalize.Parameters.AddWithValue("$session", sessionId);
            await finalize.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

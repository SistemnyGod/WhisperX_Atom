using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

public sealed record AgentRow(Guid Id, string Name, Guid? RoomId, string Status, DateTime? LastSeenAt, Guid? InstallationId = null);
public sealed record AgentCommandRow(Guid Id, string CommandType, JsonDocument Payload, long Cursor, string Status);
public sealed record RecordingSessionRow(Guid Id, Guid MeetingId, Guid? AgentId, string State, DateTime? StartedAt, DateTime? FinishedAt);
public sealed record RecordingTrackRow(Guid Id, Guid SessionId, string TrackType, int SampleRate, int Channels, string Codec);
public sealed record SummaryRow(Guid Id, Guid MeetingId, Guid? TranscriptId, int Version, string Status, string ModelName, string PromptVersion, string SourceHash, JsonDocument Content, DateTime CreatedAt);
public sealed record DecisionRow(Guid Id, Guid MeetingId, Guid? SummaryId, string Text, string Status, DateTime CreatedAt);
public sealed record ActionItemRow(Guid Id, Guid MeetingId, Guid? SummaryId, string Task, string? Responsible, DateTime? Deadline, string Status, Guid? EvidenceSegmentId, DateTime CreatedAt);
public sealed record AssistantQueryRow(Guid Id, Guid? MeetingId, string Query, string Status, string? Answer, string? VoiceAnswer, JsonDocument Evidence, string? ErrorCode, DateTime CreatedAt, DateTime? CompletedAt);
public sealed record SearchResultRow(Guid MeetingId, string MeetingTitle, string MeetingStatus, Guid SegmentId, long StartMs, long EndMs, string? Speaker, string Text, double Rank, DateTime MeetingCreatedAt);
public sealed record OperationsSnapshot(long QueuedJobs, long RunningJobs, long FailedJobs24h, long StaleLeases, long ActiveGpuJobs, long FailedGpuJobs24h, long PendingOutbox, long ActiveAgents, long UnavailableAgents, DateTimeOffset CheckedAt);
public sealed record AuditEventRow(Guid Id, Guid? ActorUserId, string? ActorUsername, Guid? MeetingId, string EntityType, Guid? EntityId, string EventType, JsonDocument? BeforeState, JsonDocument? AfterState, DateTime CreatedAt);
public enum ActionItemUpdateResult { NotFound, InvalidTransition, Updated }

public sealed class UnifiedProductStore(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres") ?? configuration["POSTGRES_CONNECTION"] ?? "Host=localhost;Port=5432;Database=whisperx_atom;Username=whisperx;Password=whisperx";

    public async Task<AgentRow?> AuthenticateAgentAsync(Guid agentId, string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,name,room_id,status,last_seen_at,installation_id FROM recorder_agents WHERE id=@id AND enrollment_hash=@hash", connection);
        command.Parameters.AddWithValue("id", agentId);
        command.Parameters.AddWithValue("hash", Hash(token));
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null : ReadAgent(reader);
    }

    public async Task<AgentRow> EnrollAgentAsync(string name, Guid? roomId, string token, string version, JsonDocument capabilities)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO recorder_agents(id,room_id,name,enrollment_hash,version,status,last_seen_at,capabilities)
            VALUES(@id,@room,@name,@hash,@version,'ONLINE',now(),@capabilities::jsonb)
            ON CONFLICT(enrollment_hash) DO UPDATE SET name=excluded.name,room_id=excluded.room_id,version=excluded.version,status='ONLINE',last_seen_at=now(),capabilities=excluded.capabilities
            RETURNING id,name,room_id,status,last_seen_at,installation_id
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("room", (object?)roomId ?? DBNull.Value); command.Parameters.AddWithValue("name", name.Trim()); command.Parameters.AddWithValue("hash", Hash(token)); command.Parameters.AddWithValue("version", version); command.Parameters.AddWithValue("capabilities", capabilities.RootElement.GetRawText());
        await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync(); return ReadAgent(reader);
    }

    public async Task<AgentRow> LinkLocalAgentAsync(Guid installationId, Guid? requestedAgentId, string name, Guid? roomId, string token, string version, JsonDocument capabilities)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var existingId = requestedAgentId ?? Guid.Empty;
        var findSql = requestedAgentId.HasValue
            ? "SELECT id FROM recorder_agents WHERE installation_id=@installation OR id=@agent ORDER BY CASE WHEN installation_id=@installation THEN 0 ELSE 1 END LIMIT 1 FOR UPDATE"
            : "SELECT id FROM recorder_agents WHERE installation_id=@installation LIMIT 1 FOR UPDATE";
        await using (var find = new NpgsqlCommand(findSql, connection, transaction))
        {
            find.Parameters.AddWithValue("installation", installationId);
            if (requestedAgentId.HasValue) find.Parameters.AddWithValue("agent", requestedAgentId.Value);
            var value = await find.ExecuteScalarAsync();
            if (value is Guid found) existingId = found;
        }

        var agentId = existingId == Guid.Empty ? Guid.NewGuid() : existingId;
        await using var upsert = new NpgsqlCommand("""
            INSERT INTO recorder_agents(id,installation_id,room_id,name,enrollment_hash,version,status,last_seen_at,capabilities)
            VALUES(@id,@installation,@room,@name,@hash,@version,'ONLINE',now(),@capabilities::jsonb)
            ON CONFLICT(id) DO UPDATE SET installation_id=excluded.installation_id,room_id=excluded.room_id,name=excluded.name,enrollment_hash=excluded.enrollment_hash,version=excluded.version,status='ONLINE',last_seen_at=now(),capabilities=excluded.capabilities
            RETURNING id,name,room_id,status,last_seen_at,installation_id
            """, connection, transaction);
        upsert.Parameters.AddWithValue("id", agentId);
        upsert.Parameters.AddWithValue("installation", installationId);
        upsert.Parameters.AddWithValue("room", (object?)roomId ?? DBNull.Value);
        upsert.Parameters.AddWithValue("name", name.Trim());
        upsert.Parameters.AddWithValue("hash", Hash(token));
        upsert.Parameters.AddWithValue("version", version);
        upsert.Parameters.AddWithValue("capabilities", capabilities.RootElement.GetRawText());
        await using var reader = await upsert.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = ReadAgent(reader);
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<bool> HeartbeatAsync(Guid agentId, string status, string version, JsonDocument capabilities)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE recorder_agents SET status=@status,version=@version,last_seen_at=now(),capabilities=@capabilities::jsonb WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", agentId); command.Parameters.AddWithValue("status", status); command.Parameters.AddWithValue("version", version); command.Parameters.AddWithValue("capabilities", capabilities.RootElement.GetRawText());
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<AgentRow>> ListAgentsAsync()
    {
        var result = new List<AgentRow>(); await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,name,room_id,status,last_seen_at,installation_id FROM recorder_agents ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(ReadAgent(reader)); return result;
    }

    public async Task<bool> AgentExistsAsync(Guid agentId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM recorder_agents WHERE id=@id)", connection);
        command.Parameters.AddWithValue("id", agentId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task<Guid> CreateCommandAsync(Guid agentId, string commandType, JsonDocument payload)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@agent::text, 0))", connection, tx))
        {
            lockCommand.Parameters.AddWithValue("agent", agentId);
            await lockCommand.ExecuteScalarAsync();
        }
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO agent_commands(id,agent_id,command_type,payload,cursor)
            VALUES(@id,@agent,@type,@payload::jsonb,COALESCE((SELECT MAX(cursor) FROM agent_commands WHERE agent_id=@agent),0)+1) RETURNING id
            """, connection, tx);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("type", commandType); command.Parameters.AddWithValue("payload", payload.RootElement.GetRawText());
        await command.ExecuteScalarAsync();
        await tx.CommitAsync();
        return id;
    }

    public async Task<IReadOnlyList<AgentCommandRow>> PendingCommandsAsync(Guid agentId, long afterCursor)
    {
        var result = new List<AgentCommandRow>(); await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,command_type,payload,cursor,status FROM agent_commands WHERE agent_id=@agent AND cursor>@cursor AND status IN ('PENDING','DELIVERED') ORDER BY cursor LIMIT 50", connection);
        command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("cursor", afterCursor);
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new AgentCommandRow(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<JsonDocument>(2), reader.GetInt64(3), reader.GetString(4))); return result;
    }

    public async Task<bool> CompleteCommandAsync(Guid agentId, Guid commandId, string status, JsonDocument result)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE agent_commands SET status=@status,result=@result::jsonb,completed_at=now() WHERE id=@id AND agent_id=@agent", connection);
        command.Parameters.AddWithValue("id", commandId); command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("status", status); command.Parameters.AddWithValue("result", result.RootElement.GetRawText()); return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<RecordingSessionRow?> CreateRecordingSessionAsync(Guid? meetingId, Guid? agentId, string? title = null, DateTimeOffset? startedAt = null)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var resolvedMeetingId = meetingId ?? Guid.NewGuid();
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? $"Совещание {DateTime.Now:dd.MM.yyyy HH:mm}" : title.Trim();
        await using (var meeting = new NpgsqlCommand("INSERT INTO meetings(id,title,status) VALUES(@id,@title,'RECORDING') ON CONFLICT(id) DO UPDATE SET title=CASE WHEN meetings.title IS NULL OR meetings.title='' THEN excluded.title ELSE meetings.title END, status='RECORDING'", connection, transaction))
        {
            meeting.Parameters.AddWithValue("id", resolvedMeetingId);
            meeting.Parameters.AddWithValue("title", resolvedTitle);
            await meeting.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("INSERT INTO recording_sessions(id,meeting_id,agent_id,state,started_at) VALUES(@id,@meeting,@agent,'RECORDING',COALESCE(@started,now())) RETURNING id,meeting_id,agent_id,state,started_at,finished_at", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("meeting", resolvedMeetingId); command.Parameters.AddWithValue("agent", (object?)agentId ?? DBNull.Value); command.Parameters.AddWithValue("started", (object?)startedAt?.UtcDateTime ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var result = ReadSession(reader);
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<RecordingTrackRow?> CreateRecordingTrackAsync(Guid agentId, Guid sessionId, string trackType, string? deviceId, int sampleRate, int channels)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO recording_tracks(id,session_id,track_type,device_id,sample_rate,channels) SELECT @id,@session,@type,@device,@rate,@channels WHERE EXISTS(SELECT 1 FROM recording_sessions WHERE id=@session AND agent_id=@agent) RETURNING id,session_id,track_type,sample_rate,channels,codec", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("type", trackType); command.Parameters.AddWithValue("device", (object?)deviceId ?? DBNull.Value); command.Parameters.AddWithValue("rate", sampleRate); command.Parameters.AddWithValue("channels", channels);
        await using var reader = await command.ExecuteReaderAsync(); return !await reader.ReadAsync() ? null : new RecordingTrackRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5));
    }

    public async Task<bool> AgentOwnsTrackAsync(Guid agentId, Guid sessionId, Guid trackId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM recording_sessions s JOIN recording_tracks t ON t.session_id=s.id WHERE s.id=@session AND s.agent_id=@agent AND t.id=@track)", connection);
        command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("track", trackId); return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task<bool> RegisterChunkAsync(Guid agentId, Guid sessionId, Guid trackId, int sequence, string storageKey, long startSample, long sampleCount, long sizeBytes, string sha256)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO recording_chunks(id,session_id,track_id,sequence,storage_key,start_sample,sample_count,size_bytes,sha256,status,confirmed_at)
            SELECT @id,@session,@track,@sequence,@key,@start,@count,@size,@sha,'CONFIRMED',now()
            WHERE EXISTS(SELECT 1 FROM recording_sessions s JOIN recording_tracks t ON t.session_id=s.id WHERE s.id=@session AND s.agent_id=@agent AND t.id=@track)
            ON CONFLICT(track_id,sequence) DO UPDATE SET storage_key=excluded.storage_key,start_sample=excluded.start_sample,sample_count=excluded.sample_count,size_bytes=excluded.size_bytes,sha256=excluded.sha256,status='CONFIRMED',confirmed_at=now() WHERE recording_chunks.sha256=excluded.sha256
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("track", trackId); command.Parameters.AddWithValue("sequence", sequence); command.Parameters.AddWithValue("key", storageKey); command.Parameters.AddWithValue("start", startSample); command.Parameters.AddWithValue("count", sampleCount); command.Parameters.AddWithValue("size", sizeBytes); command.Parameters.AddWithValue("sha", sha256); return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<int>?> MissingChunksAsync(Guid agentId, Guid sessionId, Guid trackId, int expectedCount)
    {
        if (!await AgentOwnsTrackAsync(agentId, sessionId, trackId)) return null;
        var result = new List<int>(); await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT sequence FROM recording_chunks WHERE session_id=@session AND track_id=@track", connection); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("track", trackId);
        var present = new HashSet<int>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) present.Add(reader.GetInt32(0)); for (var i = 0; i < expectedCount; i++) if (!present.Contains(i)) result.Add(i); return result;
    }


    public async Task<int?> RegisterRecordingEventsAsync(Guid agentId, Guid sessionId, IReadOnlyList<RecordingEventRequest> events)
    {
        await using var connection = await OpenAsync();
        await using var owns = new NpgsqlCommand("SELECT 1 FROM recording_sessions WHERE id=@session AND agent_id=@agent", connection);
        owns.Parameters.AddWithValue("session", sessionId); owns.Parameters.AddWithValue("agent", agentId);
        if (await owns.ExecuteScalarAsync() is null) return null;
        await using var tx = await connection.BeginTransactionAsync();
        var accepted = 0;
        foreach (var item in events.Take(1000))
        {
            if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.EventType) || item.EventType.Length > 80) continue;
            await using var command = new NpgsqlCommand("INSERT INTO recording_events(id,session_id,event_type,media_time_ms,payload,created_at) VALUES(@id,@session,@type,@time,@payload::jsonb,COALESCE(@created,now())) ON CONFLICT(id) DO NOTHING", connection, tx);
            command.Parameters.AddWithValue("id", item.Id);
            command.Parameters.AddWithValue("session", sessionId);
            command.Parameters.AddWithValue("type", item.EventType.Trim().ToUpperInvariant());
            command.Parameters.AddWithValue("time", (object?)item.MediaTimeMs ?? DBNull.Value);
            command.Parameters.AddWithValue("payload", item.Payload?.RootElement.GetRawText() ?? "{}");
            command.Parameters.AddWithValue("created", (object?)item.CreatedAt?.UtcDateTime ?? DBNull.Value);
            accepted += await command.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return accepted;
    }    public async Task<FinalizeRecordingResult> FinalizeRecordingAsync(Guid agentId, Guid sessionId, JsonDocument? manifest = null, DateTime? finishedAt = null)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var sessionCommand = new NpgsqlCommand("SELECT meeting_id FROM recording_sessions WHERE id=@id AND agent_id=@agent FOR UPDATE", connection, tx);
        sessionCommand.Parameters.AddWithValue("id", sessionId);
        sessionCommand.Parameters.AddWithValue("agent", agentId);
        var meetingValue = await sessionCommand.ExecuteScalarAsync();
        if (meetingValue is not Guid meetingId)
            return new FinalizeRecordingResult(false, false, null, null, null, Array.Empty<MissingRecordingChunks>(), "recording_session_not_found");

        // A second finalize must return the existing pipeline instead of resetting a
        // session that is already ingesting or has reached a terminal state.
        var storageKey = $"/data/recordings/{sessionId:N}";
        await using (var existing = new NpgsqlCommand("SELECT j.id,a.id FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE a.storage_key=@key AND a.source_type='recorder_session' AND j.type='TRANSCRIBE' ORDER BY j.created_at DESC LIMIT 1", connection, tx))
        {
            existing.Parameters.AddWithValue("key", storageKey);
            await using var existingReader = await existing.ExecuteReaderAsync();
            if (await existingReader.ReadAsync())
            {
                var existingJobId = existingReader.GetGuid(0);
                var existingAssetId = existingReader.GetGuid(1);
                await existingReader.CloseAsync();
                await tx.CommitAsync();
                return new FinalizeRecordingResult(true, true, meetingId, existingJobId, existingAssetId, Array.Empty<MissingRecordingChunks>(), null);
            }
        }

        var tracks = await RecordingFinalizeSupport.LoadTracksAsync(connection, tx, sessionId);
        if (tracks.Count == 0)
            return new FinalizeRecordingResult(true, false, meetingId, null, null, Array.Empty<MissingRecordingChunks>(), "recording_tracks_required");

        var expectations = RecordingFinalizeSupport.ReadExpectedTrackExpectations(manifest, tracks.Keys);
        var expected = expectations
            .Where(item => item.Value.ExpectedChunkCount is not null)
            .ToDictionary(item => item.Key, item => item.Value.ExpectedChunkCount!.Value);
        var missing = RecordingFinalizeSupport.FindMissing(tracks, expected);
        if (missing.Count > 0)
            return new FinalizeRecordingResult(true, false, meetingId, null, null, missing, "recording_chunks_incomplete");

        var timelineMismatches = RecordingFinalizeSupport.FindTimelineMismatches(tracks, expectations);
        if (timelineMismatches.Count > 0)
            return new FinalizeRecordingResult(true, false, meetingId, null, null, Array.Empty<MissingRecordingChunks>(), "recording_timeline_inconsistent");

        using var normalizedManifest = RecordingFinalizeSupport.BuildNormalizedManifest(sessionId, tracks, expected, expectations);
        var manifestJson = normalizedManifest.RootElement.GetRawText();
        var manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant();

        var finished = (object?)finishedAt ?? DBNull.Value;

        var assetId = Guid.NewGuid();
        await using var asset = new NpgsqlCommand("""
            INSERT INTO media_assets(id,meeting_id,original_name,storage_key,size_bytes,status,source_type)
            VALUES(@id,@meeting,@name,@key,@size,'INGESTING','recorder_session')
            ON CONFLICT (storage_key) WHERE source_type='recorder_session' AND storage_key IS NOT NULL DO NOTHING
            """, connection, tx);
        asset.Parameters.AddWithValue("id", assetId);
        asset.Parameters.AddWithValue("meeting", meetingId);
        asset.Parameters.AddWithValue("name", $"recording-{sessionId:N}");
        asset.Parameters.AddWithValue("key", storageKey);
        asset.Parameters.AddWithValue("size", tracks.Values.SelectMany(track => track.Chunks).Sum(chunk => chunk.SizeBytes));
        await asset.ExecuteNonQueryAsync();

        await using var assetLookup = new NpgsqlCommand("SELECT id FROM media_assets WHERE storage_key=@key AND source_type='recorder_session'", connection, tx);
        assetLookup.Parameters.AddWithValue("key", storageKey);
        assetId = (Guid)(await assetLookup.ExecuteScalarAsync())!;

        var jobId = Guid.NewGuid();
        await using var job = new NpgsqlCommand("""
            INSERT INTO jobs(id,meeting_id,media_asset_id,type,status,stage)
            VALUES(@id,@meeting,@asset,'TRANSCRIBE','QUEUED','INGEST')
            ON CONFLICT (media_asset_id,type) WHERE media_asset_id IS NOT NULL DO NOTHING
            """, connection, tx);
        job.Parameters.AddWithValue("id", jobId);
        job.Parameters.AddWithValue("meeting", meetingId);
        job.Parameters.AddWithValue("asset", assetId);
        await job.ExecuteNonQueryAsync();

        await using var jobLookup = new NpgsqlCommand("SELECT id FROM jobs WHERE media_asset_id=@asset AND type='TRANSCRIBE'", connection, tx);
        jobLookup.Parameters.AddWithValue("asset", assetId);
        jobId = (Guid)(await jobLookup.ExecuteScalarAsync())!;

        await using var state = new NpgsqlCommand("""
            UPDATE recording_sessions
            SET state='FINALIZING',finished_at=COALESCE(@finished,finished_at,now()),finalized_at=now(),finalize_manifest=@manifest::jsonb,manifest_sha256=@hash,
                total_samples=(SELECT COALESCE(MAX(start_sample+sample_count),0) FROM recording_chunks WHERE session_id=@session)
            WHERE id=@session
            """, connection, tx);
        state.Parameters.AddWithValue("finished", finished);
        state.Parameters.AddWithValue("manifest", manifestJson);
        state.Parameters.AddWithValue("hash", manifestHash);
        state.Parameters.AddWithValue("session", sessionId);
        await state.ExecuteNonQueryAsync();

        await using var updateMeeting = new NpgsqlCommand("UPDATE meetings SET status='INGESTING',finished_at=COALESCE(@finished,finished_at,now()) WHERE id=@id", connection, tx);
        updateMeeting.Parameters.AddWithValue("finished", finished);
        updateMeeting.Parameters.AddWithValue("id", meetingId);
        await updateMeeting.ExecuteNonQueryAsync();

        await using var existingOutbox = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE topic='media.ingest' AND payload->>'job_id'=@job)", connection, tx);
        existingOutbox.Parameters.AddWithValue("job", jobId.ToString());
        if (!(bool)(await existingOutbox.ExecuteScalarAsync())!)
        {
            var payload = JsonSerializer.Serialize(new
            {
                message_id = Guid.NewGuid(), job_id = jobId, meeting_id = meetingId, media_asset_id = assetId,
                stage = "INGEST", attempt = 0, storage_key = storageKey, source_type = "recorder_session", session_id = sessionId
            });
            await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'media.ingest',@payload::jsonb)", connection, tx);
            outbox.Parameters.AddWithValue("id", Guid.NewGuid());
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return new FinalizeRecordingResult(true, true, meetingId, jobId, assetId, Array.Empty<MissingRecordingChunks>(), null);
    }


    public async Task<AssistantQueryRow?> CreateAssistantQueryAsync(Guid? meetingId, string query, Guid? userId)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length is 0 or > 2000) return null;
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        if (meetingId is Guid selected)
        {
            await using var ready = new NpgsqlCommand("SELECT status FROM meetings WHERE id=@meeting", connection, tx);
            ready.Parameters.AddWithValue("meeting", selected);
            var status = await ready.ExecuteScalarAsync();
            if (status is not string text || text != "READY") return null;
        }
        var id = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("INSERT INTO assistant_queries(id,user_id,meeting_id,query,status,evidence) VALUES(@id,@user,@meeting,@query,'QUEUED','[]'::jsonb)", connection, tx);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("user", (object?)userId ?? DBNull.Value); insert.Parameters.AddWithValue("meeting", (object?)meetingId ?? DBNull.Value); insert.Parameters.AddWithValue("query", query);
        await insert.ExecuteNonQueryAsync();
        var payload = JsonSerializer.Serialize(new { message_id = Guid.NewGuid(), query_id = id, meeting_id = meetingId, query, kind = "assistant" });
        await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'llm.assistant',@payload::jsonb)", connection, tx);
        outbox.Parameters.AddWithValue("id", Guid.NewGuid()); outbox.Parameters.AddWithValue("payload", payload);
        await outbox.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return new AssistantQueryRow(id, meetingId, query, "QUEUED", null, null, JsonDocument.Parse("[]"), null, DateTime.UtcNow, null);
    }

    public async Task<AssistantQueryRow?> GetAssistantQueryAsync(Guid id, Guid? userId, bool includeAll)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,query,status,answer,voice_answer,evidence,error_code,created_at,completed_at FROM assistant_queries WHERE id=@id AND (@include_all OR user_id=@user)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("include_all", includeAll);
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null : new AssistantQueryRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<JsonDocument>(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9));
    }    public async Task<SummaryRow?> GetLatestSummaryAsync(Guid meetingId)
    {
        await using var connection = await OpenAsync(); await using var command = new NpgsqlCommand("SELECT id,meeting_id,transcript_id,version,status,model_name,prompt_version,source_hash,content,created_at FROM summaries WHERE meeting_id=@id ORDER BY version DESC LIMIT 1", connection); command.Parameters.AddWithValue("id", meetingId); await using var reader = await command.ExecuteReaderAsync(); return !await reader.ReadAsync() ? null : ReadSummary(reader);
    }

    public async Task<IReadOnlyList<DecisionRow>> ListDecisionsAsync(Guid meetingId)
    {
        var result = new List<DecisionRow>(); await using var connection = await OpenAsync(); await using var command = new NpgsqlCommand("SELECT d.id,d.meeting_id,d.summary_id,d.text,d.status,d.created_at FROM decisions d JOIN summaries s ON s.id=d.summary_id WHERE d.meeting_id=@id AND s.version=(SELECT MAX(s2.version) FROM summaries s2 WHERE s2.meeting_id=d.meeting_id) ORDER BY d.created_at", connection); command.Parameters.AddWithValue("id", meetingId); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new DecisionRow(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetDateTime(5))); return result;
    }

    public async Task<IReadOnlyList<ActionItemRow>> ListActionItemsAsync(Guid meetingId)
    {
        var result = new List<ActionItemRow>(); await using var connection = await OpenAsync(); await using var command = new NpgsqlCommand("SELECT a.id,a.meeting_id,a.summary_id,a.task,a.responsible,a.deadline,a.status,a.evidence_segment_id,a.created_at FROM action_items a JOIN summaries s ON s.id=a.summary_id WHERE a.meeting_id=@id AND s.version=(SELECT MAX(s2.version) FROM summaries s2 WHERE s2.meeting_id=a.meeting_id) ORDER BY a.created_at", connection); command.Parameters.AddWithValue("id", meetingId); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new ActionItemRow(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.IsDBNull(5)?null:reader.GetDateTime(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetGuid(7),reader.GetDateTime(8))); return result;
    }

    public async Task<Guid?> GetActionItemMeetingIdAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT meeting_id FROM action_items WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        var value = await command.ExecuteScalarAsync();
        return value is Guid meetingId ? meetingId : null;
    }

    public async Task<IReadOnlyList<SearchResultRow>> SearchAsync(string query, Guid? meetingId, Guid userId, bool includeAll, int limit, int offset)
    {
        var result = new List<SearchResultRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT m.id,m.title,m.status,s.id,s.start_ms,s.end_ms,
                   COALESCE(ms.display_name,s.speaker_label),s.text,
                   ts_rank_cd(to_tsvector('simple',COALESCE(s.text,'')),websearch_to_tsquery('simple',@query)),
                   m.created_at
            FROM transcript_segments s
            JOIN transcripts t ON t.id=s.transcript_id
            JOIN meetings m ON m.id=t.meeting_id
            LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
            WHERE (@include_all OR m.owner_id=@owner)
              AND (@meeting IS NULL OR m.id=@meeting)
              AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
              AND COALESCE(s.is_hidden,false)=false
              AND to_tsvector('simple',COALESCE(s.text,'')) @@ websearch_to_tsquery('simple',@query)
            ORDER BY ts_rank_cd(to_tsvector('simple',COALESCE(s.text,'')),websearch_to_tsquery('simple',@query)) DESC,
                     m.created_at DESC,s.ordinal
            LIMIT @limit OFFSET @offset
            """, connection);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("meeting", (object?)meetingId ?? DBNull.Value);
        command.Parameters.AddWithValue("owner", userId);
        command.Parameters.AddWithValue("include_all", includeAll);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new SearchResultRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7), reader.GetDouble(8), reader.GetDateTime(9)));
        return result;
    }

    public async Task<OperationsSnapshot> GetOperationsSnapshotAsync()
    {
        await using var connection = await OpenAsync();

        async Task<long> ScalarLongAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        var queuedJobs = await ScalarLongAsync("SELECT COUNT(*) FROM jobs WHERE status='QUEUED'");
        var runningJobs = await ScalarLongAsync("SELECT COUNT(*) FROM jobs WHERE status='RUNNING'");
        var failedJobs24h = await ScalarLongAsync("SELECT COUNT(*) FROM jobs WHERE status='FAILED' AND updated_at >= now()-interval '24 hours'");
        var staleLeases = await ScalarLongAsync("SELECT COUNT(*) FROM jobs WHERE lease_expires_at IS NOT NULL AND lease_expires_at < now() AND status NOT IN ('READY','FAILED','CANCELLED')");
        var activeGpuJobs = await ScalarLongAsync("SELECT COUNT(*) FROM jobs WHERE type IN ('TRANSCRIBE','SUMMARIZE') AND status IN ('QUEUED','RUNNING')");
        var failedGpuJobs24h = await ScalarLongAsync("SELECT COUNT(*) FROM jobs WHERE type IN ('TRANSCRIBE','SUMMARIZE') AND status='FAILED' AND updated_at >= now()-interval '24 hours' AND (error_code LIKE 'GPU_%' OR error_code LIKE 'CUDA_%')");
        var pendingOutbox = await ScalarLongAsync("SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL");
        var activeAgents = await ScalarLongAsync("SELECT COUNT(*) FROM recorder_agents WHERE status <> 'OFFLINE' AND last_seen_at >= now()-interval '90 seconds'");
        var unavailableAgents = await ScalarLongAsync("SELECT COUNT(*) FROM recorder_agents WHERE last_seen_at IS NULL OR last_seen_at < now()-interval '90 seconds'");
        return new OperationsSnapshot(queuedJobs, runningJobs, failedJobs24h, staleLeases, activeGpuJobs, failedGpuJobs24h, pendingOutbox, activeAgents, unavailableAgents, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<AuditEventRow>> ListAuditEventsAsync(Guid? meetingId, string? eventType, int limit, int offset)
    {
        var result = new List<AuditEventRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT a.id,a.actor_user_id,u.username,a.meeting_id,a.entity_type,a.entity_id,a.event_type,a.before_state,a.after_state,a.created_at
            FROM audit_events a
            LEFT JOIN users u ON u.id=a.actor_user_id
            WHERE (@meeting IS NULL OR a.meeting_id=@meeting)
              AND (@event_type IS NULL OR a.event_type=@event_type)
            ORDER BY a.created_at DESC
            LIMIT @limit OFFSET @offset
            """, connection);
        command.Parameters.AddWithValue("meeting", (object?)meetingId ?? DBNull.Value);
        command.Parameters.AddWithValue("event_type", (object?)eventType ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new AuditEventRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<JsonDocument>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<JsonDocument>(8),
                reader.GetDateTime(9)));
        return result;
    }

    public async Task<ActionItemUpdateResult> UpdateActionItemAsync(Guid id, string task, string? responsible, DateTime? deadline, string status, Guid? actorUserId)
    {
        var normalizedTask = task?.Trim();
        var normalizedStatus = status?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedTask) || normalizedTask.Length > 4000 ||
            normalizedStatus is not ("NEEDS_REVIEW" or "OPEN" or "DONE" or "CANCELLED"))
            return ActionItemUpdateResult.InvalidTransition;
        var normalizedResponsible = string.IsNullOrWhiteSpace(responsible) ? null : responsible.Trim();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        ActionItemSnapshot snapshot;
        await using (var lookup = new NpgsqlCommand("SELECT meeting_id,summary_id,task,responsible,deadline,status,evidence_segment_id FROM action_items WHERE id=@id FOR UPDATE", connection, tx))
        {
            lookup.Parameters.AddWithValue("id", id);
            await using var reader = await lookup.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return ActionItemUpdateResult.NotFound;
            snapshot = new ActionItemSnapshot(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6));
        }
        if (!IsAllowedActionItemTransition(snapshot.Status, normalizedStatus!))
            return ActionItemUpdateResult.InvalidTransition;
        await using (var update = new NpgsqlCommand("UPDATE action_items SET task=@task,responsible=@responsible,deadline=@deadline,status=@status,updated_at=now() WHERE id=@id", connection, tx))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("task", normalizedTask);
            update.Parameters.AddWithValue("responsible", (object?)normalizedResponsible ?? DBNull.Value);
            update.Parameters.AddWithValue("deadline", (object?)deadline ?? DBNull.Value);
            update.Parameters.AddWithValue("status", normalizedStatus);
            await update.ExecuteNonQueryAsync();
        }
        await AppendAuditEventAsync(
            connection,
            tx,
            actorUserId,
            snapshot.MeetingId,
            "ACTION_ITEM",
            id,
            "TASK_UPDATED",
            JsonSerializer.Serialize(new { task = snapshot.Task, responsible = snapshot.Responsible, deadline = snapshot.Deadline, status = snapshot.Status, evidenceSegmentId = snapshot.EvidenceSegmentId }),
            JsonSerializer.Serialize(new { task = normalizedTask, responsible = normalizedResponsible, deadline, status = normalizedStatus, evidenceSegmentId = snapshot.EvidenceSegmentId }));
        await tx.CommitAsync();
        return ActionItemUpdateResult.Updated;
    }

    public async Task<JsonDocument> AnswerAssistantAsync(Guid? meetingId, string query)
    {
        query = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return JsonDocument.Parse(JsonSerializer.Serialize(new { answer = "Сформулируйте вопрос.", evidence = Array.Empty<object>(), source = "none" }));

        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        var evidence = new List<object>();
        await using var connection = await OpenAsync();
        if (tokens.Length > 0)
        {
            var conditions = string.Join(" OR ", tokens.Select((_, index) => $"s.text ILIKE @term{index}"));
            await using var search = new NpgsqlCommand(
                $"SELECT s.id,t.meeting_id,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label,'Спикер N'),COALESCE(s.text,'') " +
                "FROM transcript_segments s JOIN transcripts t ON t.id=s.transcript_id " +
                "LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id " +
                $"WHERE (@meeting IS NULL OR t.meeting_id=@meeting) AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id) AND ({conditions}) " +
                "ORDER BY s.start_ms LIMIT 8", connection);
            search.Parameters.AddWithValue("meeting", (object?)meetingId ?? DBNull.Value);
            foreach (var pair in tokens.Select((token, index) => (token, index)))
                search.Parameters.AddWithValue($"term{pair.index}", $"%{pair.token}%");
            await using var reader = await search.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var evidenceMeetingId = reader.GetGuid(1);
                var startMs = reader.GetInt64(2);
                var endMs = reader.GetInt64(3);
                evidence.Add(new
                {
                    meetingId = evidenceMeetingId,
                    segmentId = reader.GetGuid(0),
                    startMs,
                    endMs,
                    timecode = FormatTimecode(startMs),
                    speaker = reader.GetString(4),
                    text = reader.GetString(5),
                });
            }
        }

        var summary = meetingId.HasValue ? await GetLatestSummaryAsync(meetingId.Value) : null;
        var asksForSummary = query.Contains("саммари", StringComparison.OrdinalIgnoreCase) || query.Contains("итог", StringComparison.OrdinalIgnoreCase);
        var answer = evidence.Count > 0
            ? $"Найдены фрагменты стенограммы по запросу: {evidence.Count}."
            : asksForSummary && summary is not null && summary.Content.RootElement.TryGetProperty("summary", out var summaryText)
                ? summaryText.GetString() ?? "Саммари готово, но текст отсутствует."
                : "По запросу ничего не найдено. Проверьте формулировку или дождитесь готовой стенограммы.";
        var source = evidence.Count > 0 ? "transcript_search" : asksForSummary && summary is not null ? "summary" : "none";

        await using var command = new NpgsqlCommand("INSERT INTO assistant_queries(id,meeting_id,query,answer,evidence) VALUES(gen_random_uuid(),@meeting,@query,@answer,@evidence::jsonb)", connection);
        command.Parameters.AddWithValue("meeting", (object?)meetingId ?? DBNull.Value);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("answer", answer);
        command.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(evidence));
        await command.ExecuteNonQueryAsync();
        return JsonDocument.Parse(JsonSerializer.Serialize(new { answer, evidence, source }));
    }
    public async Task<Guid?> QueueSummaryAsync(Guid meetingId, Guid? actorUserId, SummaryRebuildRequest? options = null)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var meetingLock = new NpgsqlCommand("SELECT id FROM meetings WHERE id=@meeting FOR UPDATE", connection, tx))
        {
            meetingLock.Parameters.AddWithValue("meeting", meetingId);
            if (await meetingLock.ExecuteScalarAsync() is not Guid)
                return null;
        }
        await using (var active = new NpgsqlCommand("SELECT id FROM jobs WHERE meeting_id=@meeting AND type='SUMMARIZE' AND status NOT IN ('READY','FAILED','CANCELLED') ORDER BY created_at DESC LIMIT 1", connection, tx))
        {
            active.Parameters.AddWithValue("meeting", meetingId);
            if (await active.ExecuteScalarAsync() is Guid activeJobId)
            {
                await tx.CommitAsync();
                return activeJobId;
            }
        }
        var transcriptSql = options?.TranscriptVersion is int
            ? "SELECT id FROM transcripts WHERE meeting_id=@meeting AND version=@version LIMIT 1"
            : "SELECT id FROM transcripts WHERE meeting_id=@meeting ORDER BY version DESC LIMIT 1";
        await using var transcript = new NpgsqlCommand(transcriptSql, connection, tx);
        transcript.Parameters.AddWithValue("meeting", meetingId);
        if (options?.TranscriptVersion is int transcriptVersion)
            transcript.Parameters.AddWithValue("version", transcriptVersion);
        var transcriptId = await transcript.ExecuteScalarAsync();
        if (transcriptId is not Guid transcriptGuid) return null;
        var jobId = Guid.NewGuid();
        await using var job = new NpgsqlCommand("INSERT INTO jobs(id,meeting_id,type,status,stage,progress) VALUES(@id,@meeting,'SUMMARIZE','QUEUED','TRANSCRIPT_READY',0)", connection, tx);
        job.Parameters.AddWithValue("id", jobId); job.Parameters.AddWithValue("meeting", meetingId); await job.ExecuteNonQueryAsync();
        var payload = JsonSerializer.Serialize(new
        {
            message_id = Guid.NewGuid(),
            job_id = jobId,
            meeting_id = meetingId,
            transcript_id = transcriptGuid,
            summary_profile = string.IsNullOrWhiteSpace(options?.Profile) ? "GENERAL_MEETING" : options.Profile.Trim().ToUpperInvariant(),
            prompt_version = string.IsNullOrWhiteSpace(options?.PromptVersion) ? "summary-v2" : options.PromptVersion.Trim(),
            reason = options?.Reason?.Trim(),
            meeting_context = options?.MeetingContext?.RootElement ?? JsonSerializer.SerializeToElement(new { })
        });
        await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'llm.summarize',@payload::jsonb)", connection, tx);
        outbox.Parameters.AddWithValue("id", Guid.NewGuid()); outbox.Parameters.AddWithValue("payload", payload); await outbox.ExecuteNonQueryAsync();
        await AppendAuditEventAsync(connection, tx, actorUserId, meetingId, "SUMMARY", jobId, "SUMMARY_REBUILD_QUEUED", null, JsonSerializer.Serialize(new { jobId, transcriptId = transcriptGuid, reason = options?.Reason }));
        await using var meeting = new NpgsqlCommand("UPDATE meetings SET status='SUMMARIZING' WHERE id=@meeting", connection, tx);
        meeting.Parameters.AddWithValue("meeting", meetingId); await meeting.ExecuteNonQueryAsync();
        await tx.CommitAsync(); return jobId;
    }
    private static string FormatTimecode(long milliseconds)
    {
        var totalSeconds = Math.Max(0, milliseconds) / 1000;
        return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}";
    }

    private sealed record ActionItemSnapshot(Guid MeetingId, Guid? SummaryId, string Task, string? Responsible, DateTime? Deadline, string Status, Guid? EvidenceSegmentId);

    private static bool IsAllowedActionItemTransition(string current, string next) => current == next || current switch
    {
        "NEEDS_REVIEW" => next is "OPEN" or "CANCELLED",
        "OPEN" => next is "NEEDS_REVIEW" or "DONE" or "CANCELLED",
        "DONE" => next is "OPEN" or "CANCELLED",
        "CANCELLED" => next is "OPEN",
        _ => false
    };

    private static async Task AppendAuditEventAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid? actorUserId, Guid? meetingId, string entityType, Guid? entityId, string eventType, string? beforeState, string? afterState)
    {
        await using var audit = new NpgsqlCommand("INSERT INTO audit_events(id,actor_user_id,meeting_id,entity_type,entity_id,event_type,before_state,after_state) VALUES(gen_random_uuid(),@actor,@meeting,@entity_type,@entity_id,@event_type,@before::jsonb,@after::jsonb)", connection, tx);
        audit.Parameters.AddWithValue("actor", (object?)actorUserId ?? DBNull.Value);
        audit.Parameters.AddWithValue("meeting", (object?)meetingId ?? DBNull.Value);
        audit.Parameters.AddWithValue("entity_type", entityType);
        audit.Parameters.AddWithValue("entity_id", (object?)entityId ?? DBNull.Value);
        audit.Parameters.AddWithValue("event_type", eventType);
        audit.Parameters.AddWithValue("before", (object?)beforeState ?? DBNull.Value);
        audit.Parameters.AddWithValue("after", (object?)afterState ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync() { var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); return connection; }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static AgentRow ReadAgent(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.IsDBNull(4)?null:r.GetDateTime(4),r.IsDBNull(5)?null:r.GetGuid(5));
    private static RecordingSessionRow ReadSession(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.IsDBNull(4)?null:r.GetDateTime(4),r.IsDBNull(5)?null:r.GetDateTime(5));
    private static SummaryRow ReadSummary(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetInt32(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetFieldValue<JsonDocument>(8),r.GetDateTime(9));
}
public sealed record MissingRecordingChunks(Guid TrackId, IReadOnlyList<int> Sequences);
public sealed record FinalizeRecordingResult(bool Found, bool Accepted, Guid? MeetingId, Guid? JobId, Guid? MediaAssetId, IReadOnlyList<MissingRecordingChunks> Missing, string? ErrorCode);

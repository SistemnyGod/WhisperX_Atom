using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

public sealed record AgentRow(Guid Id, string Name, Guid? RoomId, string Status, DateTime? LastSeenAt);
public sealed record AgentCommandRow(Guid Id, string CommandType, JsonDocument Payload, long Cursor, string Status);
public sealed record RecordingSessionRow(Guid Id, Guid MeetingId, Guid? AgentId, string State, DateTime? StartedAt, DateTime? FinishedAt);
public sealed record RecordingTrackRow(Guid Id, Guid SessionId, string TrackType, int SampleRate, int Channels, string Codec);
public sealed record SummaryRow(Guid Id, Guid MeetingId, Guid? TranscriptId, int Version, string Status, string ModelName, string PromptVersion, string SourceHash, JsonDocument Content, DateTime CreatedAt);
public sealed record DecisionRow(Guid Id, Guid MeetingId, Guid? SummaryId, string Text, string Status, DateTime CreatedAt);
public sealed record ActionItemRow(Guid Id, Guid MeetingId, Guid? SummaryId, string Task, string? Responsible, DateTime? Deadline, string Status, Guid? EvidenceSegmentId, DateTime CreatedAt);

public sealed class UnifiedProductStore(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres") ?? configuration["POSTGRES_CONNECTION"] ?? "Host=localhost;Port=5432;Database=whisperx_atom;Username=whisperx;Password=whisperx";

    public async Task<AgentRow?> AuthenticateAgentAsync(Guid agentId, string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,name,room_id,status,last_seen_at FROM recorder_agents WHERE id=@id AND enrollment_hash=@hash", connection);
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
            RETURNING id,name,room_id,status,last_seen_at
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("room", (object?)roomId ?? DBNull.Value); command.Parameters.AddWithValue("name", name.Trim()); command.Parameters.AddWithValue("hash", Hash(token)); command.Parameters.AddWithValue("version", version); command.Parameters.AddWithValue("capabilities", capabilities.RootElement.GetRawText());
        await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync(); return ReadAgent(reader);
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
        await using var command = new NpgsqlCommand("SELECT id,name,room_id,status,last_seen_at FROM recorder_agents ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(ReadAgent(reader)); return result;
    }

    public async Task<Guid> CreateCommandAsync(Guid agentId, string commandType, JsonDocument payload)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO agent_commands(id,agent_id,command_type,payload,cursor)
            VALUES(@id,@agent,@type,@payload::jsonb,COALESCE((SELECT MAX(cursor)+1 FROM agent_commands WHERE agent_id=@agent),1)) RETURNING id
            """, connection);
        var id = Guid.NewGuid(); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("type", commandType); command.Parameters.AddWithValue("payload", payload.RootElement.GetRawText());
        await command.ExecuteScalarAsync(); return id;
    }

    public async Task<IReadOnlyList<AgentCommandRow>> PendingCommandsAsync(Guid agentId, long afterCursor)
    {
        var result = new List<AgentCommandRow>(); await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,command_type,payload,cursor,status FROM agent_commands WHERE agent_id=@agent AND cursor>@cursor AND status IN ('PENDING','DELIVERED') ORDER BY cursor LIMIT 50", connection);
        command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("cursor", afterCursor);
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new AgentCommandRow(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<JsonDocument>(2), reader.GetInt64(3), reader.GetString(4))); return result;
    }

    public async Task<bool> CompleteCommandAsync(Guid commandId, string status, JsonDocument result)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE agent_commands SET status=@status,result=@result::jsonb,completed_at=now() WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", commandId); command.Parameters.AddWithValue("status", status); command.Parameters.AddWithValue("result", result.RootElement.GetRawText()); return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<RecordingSessionRow?> CreateRecordingSessionAsync(Guid meetingId, Guid? agentId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO recording_sessions(id,meeting_id,agent_id,state,started_at) VALUES(@id,@meeting,@agent,'RECORDING',now()) RETURNING id,meeting_id,agent_id,state,started_at,finished_at", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("meeting", meetingId); command.Parameters.AddWithValue("agent", (object?)agentId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(); return !await reader.ReadAsync() ? null : ReadSession(reader);
    }

    public async Task<RecordingTrackRow?> CreateRecordingTrackAsync(Guid sessionId, string trackType, string? deviceId, int sampleRate, int channels)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO recording_tracks(id,session_id,track_type,device_id,sample_rate,channels) VALUES(@id,@session,@type,@device,@rate,@channels) RETURNING id,session_id,track_type,sample_rate,channels,codec", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("type", trackType); command.Parameters.AddWithValue("device", (object?)deviceId ?? DBNull.Value); command.Parameters.AddWithValue("rate", sampleRate); command.Parameters.AddWithValue("channels", channels);
        await using var reader = await command.ExecuteReaderAsync(); return !await reader.ReadAsync() ? null : new RecordingTrackRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5));
    }

    public async Task<bool> RegisterChunkAsync(Guid sessionId, Guid trackId, int sequence, string storageKey, long startSample, long sampleCount, long sizeBytes, string sha256)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO recording_chunks(id,session_id,track_id,sequence,storage_key,start_sample,sample_count,size_bytes,sha256,status,confirmed_at)
            VALUES(@id,@session,@track,@sequence,@key,@start,@count,@size,@sha,'CONFIRMED',now())
            ON CONFLICT(track_id,sequence) DO UPDATE SET storage_key=excluded.storage_key,start_sample=excluded.start_sample,sample_count=excluded.sample_count,size_bytes=excluded.size_bytes,sha256=excluded.sha256,status='CONFIRMED',confirmed_at=now()
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("track", trackId); command.Parameters.AddWithValue("sequence", sequence); command.Parameters.AddWithValue("key", storageKey); command.Parameters.AddWithValue("start", startSample); command.Parameters.AddWithValue("count", sampleCount); command.Parameters.AddWithValue("size", sizeBytes); command.Parameters.AddWithValue("sha", sha256); return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<int>> MissingChunksAsync(Guid sessionId, Guid trackId, int expectedCount)
    {
        var result = new List<int>(); await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT sequence FROM recording_chunks WHERE session_id=@session AND track_id=@track", connection); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("track", trackId);
        var present = new HashSet<int>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) present.Add(reader.GetInt32(0)); for (var i = 0; i < expectedCount; i++) if (!present.Contains(i)) result.Add(i); return result;
    }

    public async Task<bool> FinalizeRecordingAsync(Guid sessionId, DateTime? finishedAt = null)
    {
        await using var connection = await OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("UPDATE recording_sessions SET state='FINALIZING',finished_at=COALESCE(@finished,now()) WHERE id=@id RETURNING meeting_id", connection, tx); command.Parameters.AddWithValue("id", sessionId); command.Parameters.AddWithValue("finished", (object?)finishedAt ?? DBNull.Value); var meeting = await command.ExecuteScalarAsync(); if (meeting is null) return false;
        await using var update = new NpgsqlCommand("UPDATE meetings SET status='INGESTING',finished_at=COALESCE(@finished,now()) WHERE id=@id", connection, tx); update.Parameters.AddWithValue("id", (Guid)meeting); update.Parameters.AddWithValue("finished", (object?)finishedAt ?? DBNull.Value); await update.ExecuteNonQueryAsync(); await tx.CommitAsync(); return true;
    }

    public async Task<SummaryRow?> GetLatestSummaryAsync(Guid meetingId)
    {
        await using var connection = await OpenAsync(); await using var command = new NpgsqlCommand("SELECT id,meeting_id,transcript_id,version,status,model_name,prompt_version,source_hash,content,created_at FROM summaries WHERE meeting_id=@id ORDER BY version DESC LIMIT 1", connection); command.Parameters.AddWithValue("id", meetingId); await using var reader = await command.ExecuteReaderAsync(); return !await reader.ReadAsync() ? null : ReadSummary(reader);
    }

    public async Task<IReadOnlyList<DecisionRow>> ListDecisionsAsync(Guid meetingId)
    {
        var result = new List<DecisionRow>(); await using var connection = await OpenAsync(); await using var command = new NpgsqlCommand("SELECT id,meeting_id,summary_id,text,status,created_at FROM decisions WHERE meeting_id=@id ORDER BY created_at", connection); command.Parameters.AddWithValue("id", meetingId); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new DecisionRow(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetDateTime(5))); return result;
    }

    public async Task<IReadOnlyList<ActionItemRow>> ListActionItemsAsync(Guid meetingId)
    {
        var result = new List<ActionItemRow>(); await using var connection = await OpenAsync(); await using var command = new NpgsqlCommand("SELECT id,meeting_id,summary_id,task,responsible,deadline,status,evidence_segment_id,created_at FROM action_items WHERE meeting_id=@id ORDER BY created_at", connection); command.Parameters.AddWithValue("id", meetingId); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new ActionItemRow(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.IsDBNull(5)?null:reader.GetDateTime(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetGuid(7),reader.GetDateTime(8))); return result;
    }

    public async Task<bool> UpdateActionItemAsync(Guid id, string task, string? responsible, DateTime? deadline, string status)
    {
        await using var connection = await OpenAsync(); await using var command = new NpgsqlCommand("UPDATE action_items SET task=@task,responsible=@responsible,deadline=@deadline,status=@status,updated_at=now() WHERE id=@id", connection); command.Parameters.AddWithValue("id",id); command.Parameters.AddWithValue("task",task.Trim()); command.Parameters.AddWithValue("responsible",(object?)responsible??DBNull.Value); command.Parameters.AddWithValue("deadline",(object?)deadline??DBNull.Value); command.Parameters.AddWithValue("status",status); return await command.ExecuteNonQueryAsync()>0;
    }

    public async Task<JsonDocument> AnswerAssistantAsync(Guid? meetingId, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return JsonDocument.Parse(JsonSerializer.Serialize(new { answer = "\u0421\u0444\u043e\u0440\u043c\u0443\u043b\u0438\u0440\u0443\u0439\u0442\u0435 \u0432\u043e\u043f\u0440\u043e\u0441.", evidence = Array.Empty<object>() }));
        var summary = meetingId.HasValue ? await GetLatestSummaryAsync(meetingId.Value) : null;
        var answer = summary is null
            ? "\u0421\u0430\u043c\u043c\u0430\u0440\u0438 \u0435\u0449\u0451 \u043d\u0435 \u0433\u043e\u0442\u043e\u0432\u043e. \u041f\u043e\u0441\u043b\u0435 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u0438\u044f \u043e\u0431\u0440\u0430\u0431\u043e\u0442\u043a\u0438 \u0432\u043e\u043f\u0440\u043e\u0441 \u043c\u043e\u0436\u043d\u043e \u043f\u043e\u0432\u0442\u043e\u0440\u0438\u0442\u044c."
            : summary.Content.RootElement.TryGetProperty("summary", out var text) ? text.GetString() ?? "\u0421\u0430\u043c\u043c\u0430\u0440\u0438 \u0433\u043e\u0442\u043e\u0432\u043e." : "\u0421\u0430\u043c\u043c\u0430\u0440\u0438 \u0433\u043e\u0442\u043e\u0432\u043e.";
        var evidence = Array.Empty<object>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO assistant_queries(id,meeting_id,query,answer,evidence) VALUES(gen_random_uuid(),@meeting,@query,@answer,@evidence::jsonb)", connection);
        command.Parameters.AddWithValue("meeting", (object?)meetingId ?? DBNull.Value);
        command.Parameters.AddWithValue("query", query.Trim());
        command.Parameters.AddWithValue("answer", answer);
        command.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(evidence));
        await command.ExecuteNonQueryAsync();
        return JsonDocument.Parse(JsonSerializer.Serialize(new { answer, evidence }));
    }

    public async Task<Guid?> QueueSummaryAsync(Guid meetingId)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var transcript = new NpgsqlCommand("SELECT id FROM transcripts WHERE meeting_id=@meeting ORDER BY version DESC LIMIT 1", connection, tx);
        transcript.Parameters.AddWithValue("meeting", meetingId);
        var transcriptId = await transcript.ExecuteScalarAsync();
        if (transcriptId is not Guid transcriptGuid) return null;
        var jobId = Guid.NewGuid();
        await using var job = new NpgsqlCommand("INSERT INTO jobs(id,meeting_id,type,status,stage,progress) VALUES(@id,@meeting,'SUMMARIZE','QUEUED','TRANSCRIPT_READY',0)", connection, tx);
        job.Parameters.AddWithValue("id", jobId); job.Parameters.AddWithValue("meeting", meetingId); await job.ExecuteNonQueryAsync();
        var payload = JsonSerializer.Serialize(new { message_id = Guid.NewGuid(), job_id = jobId, meeting_id = meetingId, transcript_id = transcriptGuid });
        await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'llm.summarize',@payload::jsonb)", connection, tx);
        outbox.Parameters.AddWithValue("id", Guid.NewGuid()); outbox.Parameters.AddWithValue("payload", payload); await outbox.ExecuteNonQueryAsync();
        await using var meeting = new NpgsqlCommand("UPDATE meetings SET status='SUMMARIZING' WHERE id=@meeting", connection, tx);
        meeting.Parameters.AddWithValue("meeting", meetingId); await meeting.ExecuteNonQueryAsync();
        await tx.CommitAsync(); return jobId;
    }
    private async Task<NpgsqlConnection> OpenAsync() { var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); return connection; }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static AgentRow ReadAgent(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.IsDBNull(4)?null:r.GetDateTime(4));
    private static RecordingSessionRow ReadSession(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.IsDBNull(4)?null:r.GetDateTime(4),r.IsDBNull(5)?null:r.GetDateTime(5));
    private static SummaryRow ReadSummary(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetInt32(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetFieldValue<JsonDocument>(8),r.GetDateTime(9));
}

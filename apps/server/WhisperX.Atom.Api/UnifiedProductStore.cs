using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

public sealed record AgentRow(Guid Id, string Name, Guid? RoomId, string Status, DateTime? LastSeenAt, Guid? InstallationId = null)
{
    private static readonly TimeSpan ActiveHeartbeatWindow = TimeSpan.FromSeconds(90);

    public bool IsActive
    {
        get
        {
            if (LastSeenAt is null || string.Equals(Status, "OFFLINE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Status, "REVOKED", StringComparison.OrdinalIgnoreCase))
                return false;

            var seenUtc = DateTime.SpecifyKind(LastSeenAt.Value, DateTimeKind.Utc);
            return DateTime.UtcNow - seenUtc <= ActiveHeartbeatWindow;
        }
    }

    public string EffectiveStatus =>
        string.Equals(Status, "REVOKED", StringComparison.OrdinalIgnoreCase)
            ? "REVOKED"
            : IsActive ? Status : "OFFLINE";

    public int? HeartbeatAgeSeconds
    {
        get
        {
            if (LastSeenAt is null) return null;
            var seenUtc = DateTime.SpecifyKind(LastSeenAt.Value, DateTimeKind.Utc);
            return Math.Max(0, (int)(DateTime.UtcNow - seenUtc).TotalSeconds);
        }
    }
}
public sealed record AgentBootstrapResult(AgentRow Agent, string? Token, bool ReenrollRequired = false, bool Linked = true);
public sealed record AgentCommandRow(Guid Id, string CommandType, JsonDocument Payload, long Cursor, string Status);
public sealed record RecordingSessionRow(Guid Id, Guid MeetingId, Guid? AgentId, string State, DateTime? StartedAt, DateTime? FinishedAt, string? PipelineCorrelationId = null, string? LocalSessionId = null);
public sealed record RecordingSessionCreateResult(RecordingSessionRow? Session, string? ErrorCode = null, bool Retryable = false, bool Created = true);
public sealed record RecordingCorrelationRow(Guid ServerSessionId, string? LocalSessionId, string? PipelineCorrelationId, JsonDocument Timings);
public sealed record RecordingSessionServerStatus(Guid SessionId, Guid MeetingId, string RecordingState, Guid? MediaAssetId, string? MediaStatus, Guid? JobId, string? JobStatus, string? JobStage);
public sealed record RecordingPipelineChain(
    Guid RecordingSessionId,
    Guid MeetingId,
    Guid? MediaAssetId,
    string? MediaStatus,
    Guid? AsrJobId,
    string? AsrJobStatus,
    string? AsrJobStage,
    Guid? TranscriptV1Id,
    string? TranscriptV1Status,
    Guid? EnrichmentJobId,
    string? EnrichmentJobStatus,
    string? EnrichmentJobStage,
    Guid? TranscriptV2Id,
    string? TranscriptV2Status,
    Guid? SummaryJobId,
    string? SummaryJobStatus,
    string? SummaryJobStage,
    Guid? SummaryId,
    string? SummaryStatus,
    string? PipelineCorrelationId,
    JsonDocument? StageTimings = null,
    DateTime? CreatedAt = null,
    DateTime? UpdatedAt = null,
    string? RecordingState = null)
{
    /// <summary>Canonical server-owned state used by all clients.</summary>
    [JsonIgnore]
    public RecordingPipelineSnapshot? SnapshotOverride { get; init; }

    public RecordingPipelineSnapshot Snapshot => SnapshotOverride ?? RecordingPipelineSnapshotResolver.Resolve(this);
};
public sealed record RecordingTrackRow(Guid Id, Guid SessionId, string TrackType, int SampleRate, int Channels, string Codec, string? DeviceId = null, string? DeviceName = null, string? SelectionMode = null, string? RecordingProfile = null, string? Encoding = null, int? BitsPerSample = null, string? SourceEncoding = null, string? SourceSubFormat = null, int? ValidBitsPerSample = null);
public sealed record SummaryRow(Guid Id, Guid MeetingId, Guid? TranscriptId, int Version, string Status, string ModelName, string PromptVersion, string SourceHash, JsonDocument Content, DateTime CreatedAt,
    string? ContentValidity = null, string? GenerationState = null, string? ErrorCode = null);
public sealed record SummaryEligibility(bool HasTranscript, bool Allowed, string? Reason = null);
public sealed record DecisionRow(Guid Id, Guid MeetingId, Guid? SummaryId, string Text, string Status, DateTime CreatedAt);
public sealed record ActionItemRow(Guid Id, Guid MeetingId, Guid? SummaryId, string Task, string? Responsible, DateTime? Deadline, string Status, Guid? EvidenceSegmentId, DateTime CreatedAt);
public sealed record RegistryPage<T>(IReadOnlyList<T> Items, int TotalCount, bool HasMore);
public sealed record SummaryRegistryRow(MeetingRow Meeting, SummaryRow? Summary);
public sealed record SpeakerRegistryRow(MeetingRow Meeting, SpeakerRow Speaker);
public sealed record SpeakerProfileRow(Guid Id, string DisplayName, int EmbeddingDimensions, int Samples, double? Confidence, int MeetingsCount, long DurationMs, string Status, string? EmbeddingModel, DateTime? LastSeenAt);
public sealed record ActionItemRegistryRow(MeetingRow Meeting, ActionItemRow Item);
public sealed record AssistantQueryRow(Guid Id, Guid? MeetingId, string Query, string Status, string? Answer, string? VoiceAnswer, JsonDocument Evidence, string? ErrorCode, DateTime CreatedAt, DateTime? CompletedAt, string AssistantMode = "MEETING_MEMORY", string? RequestedMode = null, double? RouterConfidence = null, string Source = "DESKTOP", string GroundingStatus = "PENDING", JsonDocument? AnswerMetadata = null, Guid? TranscriptId = null, int? TranscriptVersion = null, int RetryCount = 0, DateTime? NextRetryAt = null, bool Retryable = true)
{
    // Timings are optional metadata produced by the existing Assistant
    // pipeline. Exposing only this nested object keeps the Desktop contract
    // additive and avoids a schema migration.
    public JsonElement? Timings => AnswerMetadata?.RootElement.TryGetProperty("timings", out var timings) == true ? timings.Clone() : null;
};
public sealed record AssistantRequestRoute(string ResolvedMode, double Confidence, string? ErrorCode = null, string? Clarification = null);
/// <summary>
/// Cheap, scope-safe retrieval signal used by the API router.  It deliberately
/// contains no transcript text: the worker remains the only component that
/// materializes evidence for an Assistant answer.
/// </summary>
public sealed record AssistantRetrievalProbe(int MatchCount, double BestRank)
{
    public bool HasMatch => MatchCount > 0;
    public bool HasStrongMatch => MatchCount > 0 && BestRank >= 0.05d;
}
public sealed record LiveMeetingAppendResult(Guid RecordingSessionId, int AcceptedCount);
public sealed record AssistantConversationRow(Guid Id, Guid? UserId, string Title, string ScopeType, Guid? MeetingId, bool Archived, DateTime CreatedAt, DateTime UpdatedAt, string AssistantMode = "MEETING_MEMORY");
public sealed record AssistantMessageRow(Guid Id, Guid ConversationId, string Role, string Content, string Status, string? VoiceAnswer, JsonDocument Evidence, string? ErrorCode, Guid? QueryId, DateTime CreatedAt, DateTime? CompletedAt, JsonElement? Timings = null);
public sealed record AssistantMessageCreateResult(AssistantMessageRow UserMessage, AssistantMessageRow AssistantMessage, Guid QueryId);
public sealed record SearchResultRow(Guid MeetingId, string MeetingTitle, string MeetingStatus, Guid SegmentId, long StartMs, long EndMs, string? Speaker, string Text, double Rank, DateTime MeetingCreatedAt);
public sealed record OperationsSnapshot(
    long QueuedJobs,
    long RunningJobs,
    long FailedJobs24h,
    long StaleLeases,
    long ActiveGpuJobs,
    long FailedGpuJobs24h,
    long PendingOutbox,
    long ActiveAgents,
    long UnavailableAgents,
    long StaleRecordingSessions,
    DateTimeOffset CheckedAt,
    long HealthyGpuJobs = 0,
    long OrphanedGpuJobs = 0,
    long ActiveInboxLeases = 0,
    double OldestGpuProgressAgeSeconds = 0,
    long QueuedAssistantQueries = 0,
    long QueuedAsrJobs = 0);
public sealed record LlmRuntimeSnapshot(string? Owner, DateTimeOffset? OwnerHeartbeatAt, bool Active, string? ActiveWorkload, string? ActiveRequestId);
public sealed record WorkerRuntimeRow(string WorkerName, string InstanceId, string Status, DateTime LastSeenAt, Guid? CurrentJobId, string Version, JsonDocument Capabilities, string? LastErrorCode);
public sealed record AuditEventRow(Guid Id, Guid? ActorUserId, string? ActorUsername, Guid? MeetingId, string EntityType, Guid? EntityId, string EventType, JsonDocument? BeforeState, JsonDocument? AfterState, DateTime CreatedAt);
public enum ActionItemUpdateResult { NotFound, InvalidTransition, Updated }

public sealed class UnifiedProductStore(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres") ?? configuration["POSTGRES_CONNECTION"] ?? "Host=localhost;Port=5432;Database=whisperx_atom;Username=whisperx;Password=whisperx";

    public async Task<AgentRow?> AuthenticateAgentAsync(Guid agentId, string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,name,room_id,status,last_seen_at,installation_id FROM recorder_agents WHERE id=@id AND enrollment_hash=@hash AND token_revoked_at IS NULL", connection);
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

    public async Task<AgentBootstrapResult?> BootstrapAgentAsync(Guid installationId, Guid userId, string name, Guid? requestedAgentId, string token, string version, JsonDocument capabilities)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Guid? agentId = null;
        AgentRow? existing = null;
        var tokenRevoked = false;
        await using (var find = new NpgsqlCommand("SELECT id,name,room_id,status,last_seen_at,installation_id,token_revoked_at FROM recorder_agents WHERE installation_id=@installation OR (@agent IS NOT NULL AND id=@agent) ORDER BY CASE WHEN installation_id=@installation THEN 0 ELSE 1 END LIMIT 1 FOR UPDATE", connection, transaction))
        {
            find.Parameters.AddWithValue("installation", installationId);
            // PostgreSQL cannot infer the type of a NULL parameter used in
            // `@agent IS NOT NULL`. Keep the nullable query shape but bind it
            // explicitly as uuid.
            var requestedAgentParameter = find.Parameters.Add("agent", NpgsqlDbType.Uuid);
            requestedAgentParameter.Value = requestedAgentId.HasValue
                ? requestedAgentId.Value
                : DBNull.Value;
            await using var reader = await find.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                agentId = reader.GetGuid(0);
                existing = new AgentRow(agentId.Value, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetGuid(5));
                tokenRevoked = !reader.IsDBNull(6);
            }
        }

        if (existing is not null && tokenRevoked)
        {
            await transaction.CommitAsync();
            return new AgentBootstrapResult(existing, null, true);
        }

        agentId ??= Guid.NewGuid();
        // A Host can retain the machine InstallationId while its local AgentId
        // belongs to an older installation (for example after a restore or
        // failed enrollment). The canonical server Agent is the one already
        // bound to this InstallationId. In that narrow case issue a replacement
        // token so Desktop can atomically repair the local Host identity.
        // Healthy matching Agents retain their DPAPI-protected token.
        var replaceMismatchedLocalCredential = existing is not null
            && existing.InstallationId == installationId
            && requestedAgentId is { } requested
            && requested != existing.Id;
        if (existing is null)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO recorder_agents(id,installation_id,name,enrollment_hash,version,status,last_seen_at,capabilities)
                VALUES(@id,@installation,@name,@hash,@version,'ONLINE',now(),@capabilities::jsonb)
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", agentId.Value);
            insert.Parameters.AddWithValue("installation", installationId);
            insert.Parameters.AddWithValue("name", name.Trim());
            insert.Parameters.AddWithValue("hash", Hash(token));
            insert.Parameters.AddWithValue("version", version);
            insert.Parameters.AddWithValue("capabilities", capabilities.RootElement.GetRawText());
            await insert.ExecuteNonQueryAsync();
        }
        else
        {
            // A repeat bootstrap is normally a link refresh, not token
            // rotation. The explicit mismatched-installation case above is
            // the only exception because the local token cannot authenticate
            // as the canonical Agent.
            await using var update = new NpgsqlCommand("""
                UPDATE recorder_agents
                SET installation_id=@installation,name=@name,version=@version,
                    enrollment_hash=CASE WHEN @replaceCredential THEN @hash ELSE enrollment_hash END,
                    status='ONLINE',last_seen_at=now(),capabilities=@capabilities::jsonb
                WHERE id=@id
                """, connection, transaction);
            update.Parameters.AddWithValue("id", agentId.Value);
            update.Parameters.AddWithValue("installation", installationId);
            update.Parameters.AddWithValue("name", name.Trim());
            update.Parameters.AddWithValue("version", version);
            update.Parameters.AddWithValue("replaceCredential", replaceMismatchedLocalCredential);
            update.Parameters.AddWithValue("hash", Hash(token));
            update.Parameters.AddWithValue("capabilities", capabilities.RootElement.GetRawText());
            await update.ExecuteNonQueryAsync();
        }
        await using (var link = new NpgsqlCommand("INSERT INTO agent_user_links(agent_id,user_id,is_active,last_used_at) VALUES(@agent,@user,true,now()) ON CONFLICT(agent_id,user_id) DO UPDATE SET is_active=true,last_used_at=now()", connection, transaction))
        {
            link.Parameters.AddWithValue("agent", agentId.Value);
            link.Parameters.AddWithValue("user", userId);
            await link.ExecuteNonQueryAsync();
        }
        await using var resultCommand = new NpgsqlCommand("SELECT id,name,room_id,status,last_seen_at,installation_id FROM recorder_agents WHERE id=@id", connection, transaction);
        resultCommand.Parameters.AddWithValue("id", agentId.Value);
        await using var resultReader = await resultCommand.ExecuteReaderAsync();
        if (!await resultReader.ReadAsync()) return null;
        var result = ReadAgent(resultReader);
        await resultReader.CloseAsync();
        await transaction.CommitAsync();
        return new AgentBootstrapResult(result,
            existing is null || replaceMismatchedLocalCredential ? token : null);
    }

    public async Task<bool> AgentUserLinkedAsync(Guid agentId, Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM agent_user_links WHERE agent_id=@agent AND user_id=@user AND is_active)", connection);
        command.Parameters.AddWithValue("agent", agentId);
        command.Parameters.AddWithValue("user", userId);
        return (bool)(await command.ExecuteScalarAsync())!;
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

    public async Task<bool> RevokeAgentTokenAsync(Guid agentId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE recorder_agents SET token_revoked_at=now(),status='REVOKED' WHERE id=@id AND token_revoked_at IS NULL", connection);
        command.Parameters.AddWithValue("id", agentId);
        return await command.ExecuteNonQueryAsync() == 1;
    }

    public async Task<bool> ReplaceAgentTokenAsync(Guid agentId, string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE recorder_agents SET enrollment_hash=@hash,token_revoked_at=NULL,status='OFFLINE' WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", agentId);
        command.Parameters.AddWithValue("hash", Hash(token));
        return await command.ExecuteNonQueryAsync() == 1;
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

    public async Task<RecordingSessionCreateResult> CreateRecordingSessionWithResultAsync(Guid? meetingId, Guid? agentId, Guid? ownerUserId, string? title = null, DateTimeOffset? startedAt = null, string? pipelineCorrelationId = null, string? localSessionId = null, string? acousticProfile = "AUTO")
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // The Agent may retry the same bind after a timeout. Resolve the
        // existing server session before creating a Meeting or recording row.
        // This is the server-side idempotency key for offline recovery.
        if (agentId is Guid lookupAgent && (!string.IsNullOrWhiteSpace(localSessionId) || !string.IsNullOrWhiteSpace(pipelineCorrelationId)))
        {
            var lookupSql = !string.IsNullOrWhiteSpace(localSessionId)
                ? "SELECT id,meeting_id,agent_id,state,started_at,finished_at,pipeline_correlation_id,local_session_id,owner_user_id FROM recording_sessions WHERE agent_id=@agent AND local_session_id=@local FOR UPDATE"
                : "SELECT id,meeting_id,agent_id,state,started_at,finished_at,pipeline_correlation_id,local_session_id,owner_user_id FROM recording_sessions WHERE agent_id=@agent AND pipeline_correlation_id=@correlation FOR UPDATE";
            await using var existing = new NpgsqlCommand(lookupSql, connection, transaction);
            existing.Parameters.AddWithValue("agent", lookupAgent);
            if (!string.IsNullOrWhiteSpace(localSessionId)) existing.Parameters.AddWithValue("local", localSessionId.Trim());
            else existing.Parameters.AddWithValue("correlation", pipelineCorrelationId!.Trim());
            await using var existingReader = await existing.ExecuteReaderAsync();
            if (await existingReader.ReadAsync())
            {
                var existingSession = new RecordingSessionRow(
                    existingReader.GetGuid(0),
                    existingReader.GetGuid(1),
                    existingReader.IsDBNull(2) ? null : existingReader.GetGuid(2),
                    existingReader.GetString(3),
                    existingReader.IsDBNull(4) ? null : existingReader.GetDateTime(4),
                    existingReader.IsDBNull(5) ? null : existingReader.GetDateTime(5),
                    existingReader.IsDBNull(6) ? null : existingReader.GetString(6),
                    existingReader.IsDBNull(7) ? null : existingReader.GetString(7));
                var existingOwner = existingReader.IsDBNull(8) ? (Guid?)null : existingReader.GetGuid(8);
                if (meetingId is Guid requestedMeeting && existingSession.MeetingId != requestedMeeting)
                    return new RecordingSessionCreateResult(null, "MEETING_BINDING_CONFLICT");
                if (ownerUserId is Guid requestedOwner && existingOwner is Guid boundOwner && requestedOwner != boundOwner)
                    return new RecordingSessionCreateResult(null, "MEETING_OWNER_MISMATCH");
                return new RecordingSessionCreateResult(existingSession, Created: false);
            }
        }

        var resolvedMeetingId = meetingId ?? Guid.NewGuid();
        Guid? resolvedOwnerUserId = ownerUserId;
        if (meetingId is Guid existingMeetingId)
        {
            await using var existingMeeting = new NpgsqlCommand("SELECT status,owner_id FROM meetings WHERE id=@id FOR UPDATE", connection, transaction);
            existingMeeting.Parameters.AddWithValue("id", existingMeetingId);
            await using var meetingReader = await existingMeeting.ExecuteReaderAsync();
            if (!await meetingReader.ReadAsync()) return new RecordingSessionCreateResult(null, "MEETING_NOT_FOUND");
            var existingStatus = meetingReader.GetString(0);
            var existingOwner = meetingReader.IsDBNull(1) ? (Guid?)null : meetingReader.GetGuid(1);
            await meetingReader.CloseAsync();
            resolvedOwnerUserId ??= existingOwner;
            if (string.Equals(existingStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                return new RecordingSessionCreateResult(null, "MEETING_CANCELLED");
            if (existingOwner.HasValue && ownerUserId.HasValue && ownerUserId != existingOwner)
                return new RecordingSessionCreateResult(null, "MEETING_OWNER_MISMATCH");
        }
        if (resolvedOwnerUserId is null && agentId is Guid linkedAgent)
        {
            await using var linkedOwner = new NpgsqlCommand("SELECT l.user_id FROM agent_user_links l JOIN users u ON u.id=l.user_id WHERE l.agent_id=@agent AND l.is_active AND u.is_active ORDER BY l.last_used_at DESC NULLS LAST LIMIT 1", connection, transaction);
            linkedOwner.Parameters.AddWithValue("agent", linkedAgent);
            var linkedValue = await linkedOwner.ExecuteScalarAsync();
            if (linkedValue is Guid linkedUser) resolvedOwnerUserId = linkedUser;
        }
        if (resolvedOwnerUserId is not Guid owner)
            return new RecordingSessionCreateResult(null, "OWNER_REQUIRED");
        if (agentId is not Guid authenticatedAgent)
            return new RecordingSessionCreateResult(null, "AGENT_AUTH_REJECTED");
        await using (var link = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM agent_user_links l JOIN users u ON u.id=l.user_id WHERE l.agent_id=@agent AND l.user_id=@user AND l.is_active AND u.is_active)", connection, transaction))
        {
            link.Parameters.AddWithValue("agent", authenticatedAgent);
            link.Parameters.AddWithValue("user", owner);
            if (!((bool)(await link.ExecuteScalarAsync())!))
                // Ownership/link configuration errors are deterministic. The
                // desktop must repair the Agent↔User link instead of retrying
                // the same request forever.
                return new RecordingSessionCreateResult(null, "AGENT_USER_LINK_REQUIRED", false);
        }
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? "Meeting " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") : title.Trim();
        await using (var meeting = new NpgsqlCommand("INSERT INTO meetings(id,owner_id,title,status) VALUES(@id,@owner,@title,'RECORDING') ON CONFLICT(id) DO UPDATE SET owner_id=COALESCE(meetings.owner_id,excluded.owner_id), title=CASE WHEN meetings.title IS NULL OR meetings.title='' THEN excluded.title ELSE meetings.title END, status='RECORDING'", connection, transaction))
        {
            meeting.Parameters.AddWithValue("id", resolvedMeetingId);
            meeting.Parameters.AddWithValue("owner", owner);
            meeting.Parameters.AddWithValue("title", resolvedTitle);
            await meeting.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("INSERT INTO recording_sessions(id,meeting_id,agent_id,owner_user_id,state,started_at,pipeline_correlation_id,local_session_id,acoustic_profile) VALUES(@id,@meeting,@agent,@owner,'RECORDING',COALESCE(@started,now()),@correlation,@local,@acoustic) RETURNING id,meeting_id,agent_id,state,started_at,finished_at,pipeline_correlation_id,local_session_id", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("meeting", resolvedMeetingId);
        command.Parameters.AddWithValue("agent", authenticatedAgent);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("started", (object?)startedAt?.UtcDateTime ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation", (object?)pipelineCorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("local", (object?)localSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("acoustic", NormalizeAcousticProfile(acousticProfile));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new RecordingSessionCreateResult(null, "SERVER_STORAGE_ERROR", true);
        var result = ReadSession(reader);
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return new RecordingSessionCreateResult(result);
    }

    public async Task<RecordingSessionRow?> CreateRecordingSessionAsync(Guid? meetingId, Guid? agentId, Guid? ownerUserId, string? title = null, DateTimeOffset? startedAt = null, string? pipelineCorrelationId = null, string? localSessionId = null)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var resolvedMeetingId = meetingId ?? Guid.NewGuid();
        Guid? resolvedOwnerUserId = ownerUserId;
        if (meetingId is Guid existingMeetingId)
        {
            await using var existingMeeting = new NpgsqlCommand("SELECT status FROM meetings WHERE id=@id FOR UPDATE", connection, transaction);
            existingMeeting.Parameters.AddWithValue("id", existingMeetingId);
            await using var meetingReader = await existingMeeting.ExecuteReaderAsync();
            if (!await meetingReader.ReadAsync()) return null;
            var existingStatus = meetingReader.GetString(0);
            await meetingReader.CloseAsync();
            await using var ownerCommand = new NpgsqlCommand("SELECT owner_id FROM meetings WHERE id=@id", connection, transaction);
            ownerCommand.Parameters.AddWithValue("id", existingMeetingId);
            var ownerValue = await ownerCommand.ExecuteScalarAsync();
            var existingOwner = ownerValue is Guid ownerGuid ? ownerGuid : (Guid?)null;
            resolvedOwnerUserId ??= existingOwner;
            if (string.Equals(existingStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase) || (existingOwner.HasValue && resolvedOwnerUserId != existingOwner))
                return null;
        }
        if (resolvedOwnerUserId is not Guid owner || agentId is not Guid authenticatedAgent || !await AgentUserLinkedAsync(authenticatedAgent, owner))
            return null;
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? $"Совещание {DateTime.Now:dd.MM.yyyy HH:mm}" : title.Trim();
        await using (var meeting = new NpgsqlCommand("INSERT INTO meetings(id,owner_id,title,status) VALUES(@id,@owner,@title,'RECORDING') ON CONFLICT(id) DO UPDATE SET owner_id=COALESCE(meetings.owner_id,excluded.owner_id), title=CASE WHEN meetings.title IS NULL OR meetings.title='' THEN excluded.title ELSE meetings.title END, status='RECORDING'", connection, transaction))
        {
            meeting.Parameters.AddWithValue("id", resolvedMeetingId);
            meeting.Parameters.AddWithValue("owner", owner);
            meeting.Parameters.AddWithValue("title", resolvedTitle);
            await meeting.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("INSERT INTO recording_sessions(id,meeting_id,agent_id,owner_user_id,state,started_at,pipeline_correlation_id,local_session_id) VALUES(@id,@meeting,@agent,@owner,'RECORDING',COALESCE(@started,now()),@correlation,@local) RETURNING id,meeting_id,agent_id,state,started_at,finished_at,pipeline_correlation_id,local_session_id", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("meeting", resolvedMeetingId); command.Parameters.AddWithValue("agent", (object?)agentId ?? DBNull.Value); command.Parameters.AddWithValue("started", (object?)startedAt?.UtcDateTime ?? DBNull.Value); command.Parameters.AddWithValue("correlation", (object?)pipelineCorrelationId ?? DBNull.Value); command.Parameters.AddWithValue("local", (object?)localSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var result = ReadSession(reader);
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<RecordingTrackRow?> CreateRecordingTrackAsync(Guid agentId, Guid sessionId, string trackType, string? deviceId, string? deviceName, string? selectionMode, string? recordingProfile, int sampleRate, int channels, string? encoding, int? bitsPerSample, string? sourceEncoding = null, string? sourceSubFormat = null, int? validBitsPerSample = null)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO recording_tracks(id,session_id,track_type,device_id,device_name,selection_mode,recording_profile,sample_rate,channels,encoding,bits_per_sample,source_encoding,source_sub_format,valid_bits_per_sample) SELECT @id,@session,@type,@device,@name,@mode,@profile,@rate,@channels,@encoding,@bits,@sourceEncoding,@sourceSubFormat,@validBits WHERE EXISTS(SELECT 1 FROM recording_sessions WHERE id=@session AND agent_id=@agent) RETURNING id,session_id,track_type,sample_rate,channels,codec,device_id,device_name,selection_mode,recording_profile,encoding,bits_per_sample,source_encoding,source_sub_format,valid_bits_per_sample", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("type", trackType); command.Parameters.AddWithValue("device", (object?)deviceId ?? DBNull.Value); command.Parameters.AddWithValue("name", (object?)deviceName ?? DBNull.Value); command.Parameters.AddWithValue("mode", (object?)selectionMode ?? DBNull.Value); command.Parameters.AddWithValue("profile", (object?)recordingProfile ?? DBNull.Value); command.Parameters.AddWithValue("rate", sampleRate); command.Parameters.AddWithValue("channels", channels); command.Parameters.AddWithValue("encoding", (object?)encoding ?? DBNull.Value); command.Parameters.AddWithValue("bits", (object?)bitsPerSample ?? DBNull.Value); command.Parameters.AddWithValue("sourceEncoding", (object?)sourceEncoding ?? DBNull.Value); command.Parameters.AddWithValue("sourceSubFormat", (object?)sourceSubFormat ?? DBNull.Value); command.Parameters.AddWithValue("validBits", (object?)validBitsPerSample ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(); return !await reader.ReadAsync() ? null : new RecordingTrackRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetInt32(14));
    }

    public async Task<IReadOnlyList<RecordingTrackRow>> ListRecordingTracksAsync(Guid meetingId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT t.id,t.session_id,t.track_type,t.sample_rate,t.channels,t.codec,t.device_id,t.device_name,t.selection_mode,t.recording_profile,t.encoding,t.bits_per_sample,t.source_encoding,t.source_sub_format,t.valid_bits_per_sample FROM recording_tracks t JOIN recording_sessions s ON s.id=t.session_id WHERE s.meeting_id=@meeting ORDER BY t.created_at", connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        var rows = new List<RecordingTrackRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(new RecordingTrackRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetInt32(14)));
        return rows;
    }

    public async Task<IReadOnlyList<RecordingCorrelationRow>> ListRecordingCorrelationAsync(Guid meetingId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,pipeline_correlation_id,stage_timings,local_session_id FROM recording_sessions WHERE meeting_id=@meeting ORDER BY created_at", connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        var result = new List<RecordingCorrelationRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new RecordingCorrelationRow(reader.GetGuid(0), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(1) ? null : reader.GetString(1), JsonDocument.Parse(reader.IsDBNull(2) ? "{}" : reader.GetString(2))));
        return result;
    }

    public async Task<bool> AgentOwnsTrackAsync(Guid agentId, Guid sessionId, Guid trackId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM recording_sessions s JOIN recording_tracks t ON t.session_id=s.id WHERE s.id=@session AND s.agent_id=@agent AND t.id=@track)", connection);
        command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("track", trackId); return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task<bool> StageChunkAsync(Guid agentId, Guid sessionId, Guid trackId, int sequence, string storageKey, long startSample, long sampleCount, long sizeBytes, string sha256)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO recording_chunks(id,session_id,track_id,sequence,storage_key,start_sample,sample_count,size_bytes,sha256,status,confirmed_at)
            SELECT @id,@session,@track,@sequence,@key,@start,@count,@size,@sha,'STAGING',NULL
            WHERE EXISTS(SELECT 1 FROM recording_sessions s JOIN recording_tracks t ON t.session_id=s.id WHERE s.id=@session AND s.agent_id=@agent AND t.id=@track)
            ON CONFLICT(track_id,sequence) DO UPDATE SET storage_key=excluded.storage_key,start_sample=excluded.start_sample,sample_count=excluded.sample_count,size_bytes=excluded.size_bytes,sha256=excluded.sha256,status=CASE WHEN recording_chunks.status='CONFIRMED' THEN 'CONFIRMED' ELSE 'STAGING' END,confirmed_at=recording_chunks.confirmed_at WHERE recording_chunks.sha256=excluded.sha256
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("track", trackId); command.Parameters.AddWithValue("sequence", sequence); command.Parameters.AddWithValue("key", storageKey); command.Parameters.AddWithValue("start", startSample); command.Parameters.AddWithValue("count", sampleCount); command.Parameters.AddWithValue("size", sizeBytes); command.Parameters.AddWithValue("sha", sha256);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> ConfirmChunkAsync(Guid agentId, Guid sessionId, Guid trackId, int sequence, string storageKey, long startSample, long sampleCount, long sizeBytes, string sha256)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO recording_chunks(id,session_id,track_id,sequence,storage_key,start_sample,sample_count,size_bytes,sha256,status,confirmed_at)
            SELECT @id,@session,@track,@sequence,@key,@start,@count,@size,@sha,'CONFIRMED',now()
            WHERE EXISTS(SELECT 1 FROM recording_sessions s JOIN recording_tracks t ON t.session_id=s.id WHERE s.id=@session AND s.agent_id=@agent AND t.id=@track)
            ON CONFLICT(track_id,sequence) DO UPDATE SET storage_key=excluded.storage_key,start_sample=excluded.start_sample,sample_count=excluded.sample_count,size_bytes=excluded.size_bytes,status='CONFIRMED',confirmed_at=now() WHERE recording_chunks.sha256=excluded.sha256
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("agent", agentId); command.Parameters.AddWithValue("session", sessionId); command.Parameters.AddWithValue("track", trackId); command.Parameters.AddWithValue("sequence", sequence); command.Parameters.AddWithValue("key", storageKey); command.Parameters.AddWithValue("start", startSample); command.Parameters.AddWithValue("count", sampleCount); command.Parameters.AddWithValue("size", sizeBytes); command.Parameters.AddWithValue("sha", sha256);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    // Compatibility wrapper for older callers. New upload paths must stage first and
    // confirm only after the final file is atomically present.
    public async Task<bool> RegisterChunkAsync(Guid agentId, Guid sessionId, Guid trackId, int sequence, string storageKey, long startSample, long sampleCount, long sizeBytes, string sha256)
    {
        if (!await StageChunkAsync(agentId, sessionId, trackId, sequence, storageKey, startSample, sampleCount, sizeBytes, sha256)) return false;
        return await ConfirmChunkAsync(agentId, sessionId, trackId, sequence, storageKey, startSample, sampleCount, sizeBytes, sha256);
    }

    public async Task<IReadOnlyList<int>?> MissingChunksAsync(Guid agentId, Guid sessionId, Guid trackId, int expectedCount)
    {
        if (!await AgentOwnsTrackAsync(agentId, sessionId, trackId)) return null;
        var result = new List<int>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT sequence,storage_key,size_bytes,sha256 FROM recording_chunks WHERE session_id=@session AND track_id=@track AND status='CONFIRMED'", connection);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("track", trackId);

        var confirmed = new List<(int Sequence, string StorageKey, long SizeBytes, string Sha256)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                confirmed.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        }

        // A database row is not sufficient proof that a chunk is deliverable.
        // Only a present, complete and checksum-valid final file counts as
        // CONFIRMED; a crash between storage and database updates must be
        // repaired by the agent's normal resend path.
        var present = new HashSet<int>();
        foreach (var chunk in confirmed)
        {
            try
            {
                var path = StorageHelpers.StoragePath(chunk.StorageKey);
                if (!File.Exists(path) || new FileInfo(path).Length != chunk.SizeBytes) continue;
                if (string.Equals(await StorageHelpers.ComputeSha256Async(path), chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                    present.Add(chunk.Sequence);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
        }

        for (var i = 0; i < expectedCount; i++)
            if (!present.Contains(i)) result.Add(i);
        return result;
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
            var eventType = item.EventType.Trim().ToUpperInvariant();
            command.Parameters.AddWithValue("type", eventType);
            command.Parameters.AddWithValue("time", (object?)item.MediaTimeMs ?? DBNull.Value);
            command.Parameters.AddWithValue("payload", item.Payload?.RootElement.GetRawText() ?? "{}");
            var createdAt = item.CreatedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
            command.Parameters.AddWithValue("created", createdAt.UtcDateTime);
            var inserted = await command.ExecuteNonQueryAsync();
            accepted += inserted;
            if (inserted > 0)
                await RecordPipelineEventTimingAsync(connection, tx, sessionId, eventType, createdAt);
        }
        await tx.CommitAsync();
        return accepted;
    }

    private static readonly IReadOnlyDictionary<string, (string StartEvent, string DurationKey)> PipelineEventDurations =
        new Dictionary<string, (string StartEvent, string DurationKey)>(StringComparer.OrdinalIgnoreCase)
        {
            ["PIPELINE_FLAC_READY"] = ("PIPELINE_LOCAL_READY", "local_ready_to_flac_ms"),
            ["PIPELINE_UPLOAD_STARTED"] = ("PIPELINE_FLAC_READY", "flac_to_upload_started_ms"),
            ["PIPELINE_UPLOAD_READY"] = ("PIPELINE_UPLOAD_STARTED", "upload_ms"),
            ["PIPELINE_FINALIZE_ACCEPTED"] = ("PIPELINE_UPLOAD_READY", "finalize_ms"),
            ["PIPELINE_MEDIA_READY"] = ("PIPELINE_FINALIZE_ACCEPTED", "media_assembly_ms")
        };

    /// <summary>
    /// Folds the idempotent recorder event stream into bounded diagnostic
    /// timestamps. A missing or late event can never change job state; an
    /// accepted event simply makes the server-owned snapshot explain where
    /// time was spent.
    /// </summary>
    private static async Task RecordPipelineEventTimingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        string eventType,
        DateTimeOffset createdAt)
    {
        var createdText = createdAt.ToUniversalTime().ToString("O");
        await using (var marker = new NpgsqlCommand("""
            UPDATE recording_sessions
            SET stage_timings=jsonb_set(
                COALESCE(stage_timings,'{}'::jsonb),
                ARRAY['pipelineEvents',@eventType]::text[],
                to_jsonb(@createdText::text),
                true)
            WHERE id=@session
            """, connection, transaction))
        {
            marker.Parameters.AddWithValue("eventType", eventType);
            marker.Parameters.AddWithValue("createdText", createdText);
            marker.Parameters.AddWithValue("session", sessionId);
            await marker.ExecuteNonQueryAsync();
        }

        if (!PipelineEventDurations.TryGetValue(eventType, out var duration)) return;

        string? startText;
        await using (var lookup = new NpgsqlCommand("""
            SELECT stage_timings #>> ARRAY['pipelineEvents',@startEvent]::text[]
            FROM recording_sessions
            WHERE id=@session
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("startEvent", duration.StartEvent);
            lookup.Parameters.AddWithValue("session", sessionId);
            startText = await lookup.ExecuteScalarAsync() as string;
        }

        if (!DateTimeOffset.TryParse(startText, out var startAt)) return;
        var elapsedMs = Math.Max(0L, (long)Math.Round((createdAt - startAt).TotalMilliseconds));
        await using var timing = new NpgsqlCommand("""
            UPDATE recording_sessions
            SET stage_timings=jsonb_set(
                COALESCE(stage_timings,'{}'::jsonb),
                ARRAY['pipelineDurations',@durationKey]::text[],
                to_jsonb(@elapsedMs::bigint),
                true)
            WHERE id=@session
            """, connection, transaction);
        timing.Parameters.AddWithValue("durationKey", duration.DurationKey);
        timing.Parameters.AddWithValue("elapsedMs", elapsedMs);
        timing.Parameters.AddWithValue("session", sessionId);
        await timing.ExecuteNonQueryAsync();
    }

    public async Task<FinalizeRecordingResult> FinalizeRecordingAsync(Guid agentId, Guid sessionId, JsonDocument? manifest = null, DateTime? finishedAt = null)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var sessionCommand = new NpgsqlCommand("SELECT rs.meeting_id,rs.owner_user_id,COALESCE((SELECT u.is_active FROM users u WHERE u.id=rs.owner_user_id),false),m.status FROM recording_sessions rs JOIN meetings m ON m.id=rs.meeting_id WHERE rs.id=@id AND rs.agent_id=@agent FOR UPDATE", connection, tx);
        sessionCommand.Parameters.AddWithValue("id", sessionId);
        sessionCommand.Parameters.AddWithValue("agent", agentId);
        await using var sessionReader = await sessionCommand.ExecuteReaderAsync();
        if (!await sessionReader.ReadAsync())
            return new FinalizeRecordingResult(false, false, null, null, null, Array.Empty<MissingRecordingChunks>(), "recording_session_not_found");
        var meetingId = sessionReader.GetGuid(0);
        var ownerActive = !sessionReader.IsDBNull(2) && sessionReader.GetBoolean(2);
        var meetingStatus = sessionReader.IsDBNull(3) ? null : sessionReader.GetString(3);
        await sessionReader.CloseAsync();
        // Cancellation/deletion wins over a late recorder finalize. This
        // check must happen before the idempotent existing-job lookup, because
        // a queued worker may still hold an in-memory upload after cancellation.
        if (string.Equals(meetingStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            return new FinalizeRecordingResult(true, false, meetingId, null, null, Array.Empty<MissingRecordingChunks>(), "MEETING_CANCELLED");
        if (!ownerActive)
            return new FinalizeRecordingResult(true, false, meetingId, null, null, Array.Empty<MissingRecordingChunks>(), "OWNER_AUTHORIZATION_REJECTED");

        // A second finalize must return the existing pipeline instead of resetting a
        // session that is already ingesting or has reached a terminal state.
        var storageKey = $"/data/recordings/{sessionId:N}";
        await using (var existing = new NpgsqlCommand("SELECT j.id,a.id FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE a.storage_key=@key AND a.source_type='recorder_session' AND j.type IN ('TRANSCRIBE_ASR','TRANSCRIBE') ORDER BY j.created_at DESC LIMIT 1", connection, tx))
        {
            existing.Parameters.AddWithValue("key", storageKey);
            await using var existingReader = await existing.ExecuteReaderAsync();
            if (await existingReader.ReadAsync())
            {
                var existingJobId = existingReader.GetGuid(0);
                var existingAssetId = existingReader.GetGuid(1);
                await existingReader.CloseAsync();
                await EnsurePipelineLineageAsync(connection, tx, sessionId, meetingId, existingAssetId, existingJobId, null);
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
        // Confirmed upload rows already carry the SHA verified before the
        // atomic storage move. Avoid re-reading every FLAC during the normal
        // finalize path; an explicit integrity-recovery pass can opt into
        // full re-hashing through FindMissingAsync(..., verifyHashes: true).
        var missing = await RecordingFinalizeSupport.FindMissingAsync(tracks, expected, verifyHashes: false);
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
            VALUES(@id,@meeting,@asset,'TRANSCRIBE_ASR','QUEUED','INGEST')
            ON CONFLICT (media_asset_id,type) WHERE media_asset_id IS NOT NULL DO NOTHING
            """, connection, tx);
        job.Parameters.AddWithValue("id", jobId);
        job.Parameters.AddWithValue("meeting", meetingId);
        job.Parameters.AddWithValue("asset", assetId);
        await job.ExecuteNonQueryAsync();

        await using var jobLookup = new NpgsqlCommand("SELECT id FROM jobs WHERE media_asset_id=@asset AND type IN ('TRANSCRIBE_ASR','TRANSCRIBE') ORDER BY CASE WHEN type='TRANSCRIBE_ASR' THEN 0 ELSE 1 END LIMIT 1", connection, tx);
        jobLookup.Parameters.AddWithValue("asset", assetId);
        jobId = (Guid)(await jobLookup.ExecuteScalarAsync())!;
        var pipelineCorrelationId = await GetPipelineCorrelationIdAsync(connection, tx, sessionId);
        await using (var correlationUpdate = new NpgsqlCommand("UPDATE jobs SET pipeline_correlation_id=@correlation WHERE id=@job", connection, tx))
        {
            correlationUpdate.Parameters.AddWithValue("correlation", (object?)pipelineCorrelationId ?? DBNull.Value);
            correlationUpdate.Parameters.AddWithValue("job", jobId);
            await correlationUpdate.ExecuteNonQueryAsync();
        }
        await EnsurePipelineLineageAsync(connection, tx, sessionId, meetingId, assetId, jobId, pipelineCorrelationId);

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
            var acousticProfile = await ReadAcousticProfileAsync(connection, tx, sessionId);
            var payload = JsonSerializer.Serialize(new
            {
                message_id = Guid.NewGuid(), job_id = jobId, meeting_id = meetingId, media_asset_id = assetId,
                stage = "INGEST", attempt = 0, storage_key = storageKey, source_type = "recorder_session", session_id = sessionId, language = "ru", acousticProfile, correlation_id = await GetPipelineCorrelationIdAsync(connection, tx, sessionId)
            });
            await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'media.ingest',@payload::jsonb)", connection, tx);
            outbox.Parameters.AddWithValue("id", Guid.NewGuid());
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return new FinalizeRecordingResult(true, true, meetingId, jobId, assetId, Array.Empty<MissingRecordingChunks>(), null);
    }


    public async Task<RecordingSessionServerStatus?> GetRecordingSessionStatusAsync(Guid agentId, Guid sessionId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT rs.id,rs.meeting_id,rs.state,a.id,a.status,j.id,j.status,j.stage
            FROM recording_sessions rs
            LEFT JOIN media_assets a ON a.storage_key=@storage AND a.source_type='recorder_session'
            LEFT JOIN jobs j ON j.media_asset_id=a.id AND j.type IN ('TRANSCRIBE_ASR','TRANSCRIBE')
            WHERE rs.id=@session AND rs.agent_id=@agent
            ORDER BY j.created_at DESC NULLS LAST
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("agent", agentId);
        command.Parameters.AddWithValue("storage", $"/data/recordings/{sessionId:N}");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new RecordingSessionServerStatus(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    /// <summary>
    /// Returns the durable recorder lineage.  This is intentionally a single
    /// server-side query so a restarted Desktop/agent can resume polling
    /// without reconstructing identifiers from transient outbox messages.
    /// </summary>
    public async Task<RecordingPipelineChain?> GetRecordingPipelineChainAsync(Guid agentId, Guid sessionId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT r.recording_session_id,r.meeting_id,
                   r.media_asset_id,a.status,
                   r.asr_job_id,aj.status,aj.stage,
                   r.transcript_v1_id,v1.status,
                   r.enrichment_job_id,ej.status,ej.stage,
                   r.transcript_v2_id,v2.status,
                   r.summary_job_id,sj.status,sj.stage,
                   r.summary_id,s.status,r.pipeline_correlation_id,rs.state,rs.stage_timings,r.created_at,r.updated_at
            FROM recording_pipeline_runs r
            LEFT JOIN media_assets a ON a.id=r.media_asset_id
            LEFT JOIN jobs aj ON aj.id=r.asr_job_id
            LEFT JOIN transcripts v1 ON v1.id=r.transcript_v1_id
            LEFT JOIN jobs ej ON ej.id=r.enrichment_job_id
            LEFT JOIN transcripts v2 ON v2.id=r.transcript_v2_id
            LEFT JOIN jobs sj ON sj.id=r.summary_job_id
            LEFT JOIN summaries s ON s.id=r.summary_id
            JOIN recording_sessions rs ON rs.id=r.recording_session_id
            WHERE r.recording_session_id=@session AND rs.agent_id=@agent
            """, connection);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("agent", agentId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new RecordingPipelineChain(
            reader.GetGuid(0), reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetGuid(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetGuid(14), reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetGuid(17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(21) ? null : JsonDocument.Parse(reader.GetString(21)),
            reader.GetDateTime(22), reader.GetDateTime(23),
            reader.IsDBNull(20) ? null : reader.GetString(20));
    }

    public async Task<IReadOnlyList<RecordingPipelineChain>> GetMeetingPipelineChainsAsync(Guid meetingId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT r.recording_session_id,r.meeting_id,r.media_asset_id,a.status,
                   r.asr_job_id,aj.status,aj.stage,r.transcript_v1_id,v1.status,
                   r.enrichment_job_id,ej.status,ej.stage,r.transcript_v2_id,v2.status,
                   r.summary_job_id,sj.status,sj.stage,r.summary_id,s.status,r.pipeline_correlation_id,rs.state,rs.stage_timings,r.created_at,r.updated_at
            FROM recording_pipeline_runs r
            LEFT JOIN media_assets a ON a.id=r.media_asset_id
            LEFT JOIN jobs aj ON aj.id=r.asr_job_id
            LEFT JOIN transcripts v1 ON v1.id=r.transcript_v1_id
            LEFT JOIN jobs ej ON ej.id=r.enrichment_job_id
            LEFT JOIN transcripts v2 ON v2.id=r.transcript_v2_id
            LEFT JOIN jobs sj ON sj.id=r.summary_job_id
            LEFT JOIN summaries s ON s.id=r.summary_id
            JOIN recording_sessions rs ON rs.id=r.recording_session_id
            WHERE r.meeting_id=@meeting ORDER BY r.created_at
            """, connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        var result = new List<RecordingPipelineChain>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new RecordingPipelineChain(
                reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12), reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetGuid(14), reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetGuid(17), reader.IsDBNull(18) ? null : reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(21) ? null : JsonDocument.Parse(reader.GetString(21)),
                reader.GetDateTime(22), reader.GetDateTime(23),
                reader.IsDBNull(20) ? null : reader.GetString(20)));
        }
        return result;
    }

    /// <summary>
    /// Returns a short-lived readiness view for pipeline workers.  It is
    /// deliberately derived from the same heartbeat rows as system
    /// readiness, so a queued stage can explain whether it is waiting for a
    /// worker rather than merely waiting on an opaque timeout.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, bool>> GetPipelineWorkerReadinessAsync(string? expectedIdentity = null)
    {
        var now = DateTime.UtcNow;
        var rows = await ListWorkerRuntimeAsync();
        return rows
            .GroupBy(item => item.WorkerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var item = group.OrderByDescending(value => value.LastSeenAt).First();
                    var fresh = now - item.LastSeenAt.ToUniversalTime() <= TimeSpan.FromSeconds(60)
                        && now - item.LastSeenAt.ToUniversalTime() >= TimeSpan.Zero;
                    var active = item.Status is "READY" or "BUSY" or "DEGRADED";
                    var identityMatches = string.IsNullOrWhiteSpace(expectedIdentity)
                        || string.Equals(expectedIdentity, "dev", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(expectedIdentity, item.Version, StringComparison.Ordinal);
                    return fresh && active && identityMatches;
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task EnsurePipelineLineageAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid sessionId, Guid meetingId, Guid? mediaAssetId, Guid? asrJobId, string? correlationId)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO recording_pipeline_runs(recording_session_id,meeting_id,media_asset_id,asr_job_id,pipeline_correlation_id)
            VALUES(@session,@meeting,@asset,@job,@correlation)
            ON CONFLICT(recording_session_id) DO UPDATE SET
              meeting_id=EXCLUDED.meeting_id,
              media_asset_id=COALESCE(EXCLUDED.media_asset_id,recording_pipeline_runs.media_asset_id),
              asr_job_id=COALESCE(EXCLUDED.asr_job_id,recording_pipeline_runs.asr_job_id),
              pipeline_correlation_id=COALESCE(EXCLUDED.pipeline_correlation_id,recording_pipeline_runs.pipeline_correlation_id),
              updated_at=now()
            """, connection, tx);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("meeting", meetingId);
        command.Parameters.AddWithValue("asset", (object?)mediaAssetId ?? DBNull.Value);
        command.Parameters.AddWithValue("job", (object?)asrJobId ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation", (object?)correlationId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static string? NormalizeAssistantMode(string? requestedMode, Guid? meetingId, string scopeType)
    {
        var mode = string.IsNullOrWhiteSpace(requestedMode)
            ? scopeType == "GENERAL" ? "GENERAL_CHAT" : meetingId.HasValue ? "CURRENT_MEETING" : "MEETING_MEMORY"
            : requestedMode.Trim().ToUpperInvariant();
        // Public contract calls this mode MEETING_HISTORY; keep the existing
        // MEETING_MEMORY value in storage for IPC/API v6 compatibility.
        if (mode == "MEETING_HISTORY") mode = "MEETING_MEMORY";
        if (mode is not ("GENERAL_CHAT" or "MEETING_MEMORY" or "CURRENT_MEETING" or "LIVE_MEETING")) return null;
        if (mode == "GENERAL_CHAT" && (scopeType != "GENERAL" || meetingId is not null)) return null;
        if (mode is "CURRENT_MEETING" or "LIVE_MEETING" && (scopeType != "MEETING" || meetingId is null)) return null;
        if (mode == "MEETING_MEMORY" && scopeType == "GENERAL") return null;
        return mode;
    }

    /// <summary>
    /// Stores provisional ASR segments for an active or just-stopped
    /// (FINALIZING) recording. The latter accepts recognizer tail segments
    /// while rows are kept through STOP until a usable V1 exists (with a
    /// seven-day safety deadline). It remains separate from transcript V1/V2
    /// and requires a session owned by the authenticated user (or an operator).
    /// </summary>
    public async Task<LiveMeetingAppendResult?> AppendLiveMeetingSegmentsAsync(
        Guid meetingId,
        Guid userId,
        bool privileged,
        Guid? recordingSessionId,
        IReadOnlyList<LiveMeetingSegmentRequest> segments)
    {
        if (segments is null || segments.Count == 0 || segments.Count > 64) return null;
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var session = new NpgsqlCommand("""
            SELECT id,owner_user_id
            FROM recording_sessions
            WHERE meeting_id=@meeting
              AND (@session IS NULL OR id=@session)
              AND state IN ('RECORDING','PAUSED','STARTING','AWAITING_AGENT_RECONNECT','FINALIZING')
            ORDER BY started_at DESC,id DESC
            LIMIT 1
            FOR UPDATE
            """, connection, tx);
        session.Parameters.AddWithValue("meeting", meetingId);
        session.Parameters.AddWithValue("session", (object?)recordingSessionId ?? DBNull.Value);
        await using var reader = await session.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var activeSessionId = reader.GetGuid(0);
        var owner = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        await reader.CloseAsync();
        if (!privileged && owner != userId) return null;

        var accepted = 0;
        foreach (var item in segments.Take(64))
        {
            if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Text)
                || item.Text.Trim().Length > 2000 || item.StartMs < 0 || item.EndMs <= item.StartMs
                || item.EndMs - item.StartMs > 10 * 60 * 1000
                || item.Confidence is < 0 or > 1
                || item.MeetingId is Guid suppliedMeeting && suppliedMeeting != meetingId)
                continue;
            var sourceTrackType = string.Equals(item.SourceTrackType, "system-audio", StringComparison.OrdinalIgnoreCase)
                ? "system-audio" : "room-microphone";
            var channelRole = sourceTrackType == "system-audio" ? "REMOTE_SYSTEM" : "LOCAL_ROOM";
            if (string.Equals(item.ChannelRole, "MIC_FALLBACK", StringComparison.OrdinalIgnoreCase)
                && sourceTrackType == "room-microphone") channelRole = "MIC_FALLBACK";
            await using var insert = new NpgsqlCommand("""
                INSERT INTO live_meeting_segments(id,meeting_id,recording_session_id,start_ms,end_ms,text,confidence,revision,source_track_type,source_track_id,channel_role,quality_flags,captured_at,expires_at,retention_policy,created_by)
                VALUES(@id,@meeting,@session,@start,@end,@text,@confidence,@revision,@sourceTrackType,@sourceTrackId,@channelRole,@qualityFlags,now(),now()+interval '7 days','UNTIL_V1_READY',@user)
                ON CONFLICT(id) DO UPDATE SET
                  text=EXCLUDED.text,start_ms=EXCLUDED.start_ms,end_ms=EXCLUDED.end_ms,
                  confidence=EXCLUDED.confidence,revision=EXCLUDED.revision,
                  source_track_type=EXCLUDED.source_track_type,source_track_id=EXCLUDED.source_track_id,
                  channel_role=EXCLUDED.channel_role,quality_flags=EXCLUDED.quality_flags,
                  captured_at=EXCLUDED.captured_at,
                  expires_at=GREATEST(live_meeting_segments.expires_at, EXCLUDED.expires_at),
                  retention_policy='UNTIL_V1_READY'
                WHERE live_meeting_segments.meeting_id=EXCLUDED.meeting_id
                  AND live_meeting_segments.recording_session_id=EXCLUDED.recording_session_id
                  AND EXCLUDED.revision >= live_meeting_segments.revision
                """, connection, tx);
            insert.Parameters.AddWithValue("id", item.Id);
            insert.Parameters.AddWithValue("meeting", meetingId);
            insert.Parameters.AddWithValue("session", activeSessionId);
            insert.Parameters.AddWithValue("start", item.StartMs);
            insert.Parameters.AddWithValue("end", item.EndMs);
            insert.Parameters.AddWithValue("text", item.Text.Trim());
            insert.Parameters.AddWithValue("confidence", (object?)item.Confidence ?? DBNull.Value);
            insert.Parameters.AddWithValue("revision", Math.Max(0, item.Revision));
            insert.Parameters.AddWithValue("sourceTrackType", sourceTrackType);
            insert.Parameters.AddWithValue("sourceTrackId", (object?)item.SourceTrackId ?? DBNull.Value);
            insert.Parameters.AddWithValue("channelRole", channelRole);
            insert.Parameters.AddWithValue("qualityFlags", (object?)item.QualityFlags ?? DBNull.Value);
            insert.Parameters.AddWithValue("user", userId);
            accepted += await insert.ExecuteNonQueryAsync();
        }
        await using var prune = new NpgsqlCommand("""
            DELETE FROM live_meeting_segments
            WHERE meeting_id=@meeting AND recording_session_id=@session
              AND (expires_at <= now()
                OR EXISTS (
                  SELECT 1 FROM transcripts t
                  WHERE t.meeting_id=live_meeting_segments.meeting_id
                    AND t.version=1
                    AND t.status IN ('READY','PARTIAL_READY')
                ) OR id IN (
                SELECT id FROM live_meeting_segments
                WHERE meeting_id=@meeting AND recording_session_id=@session
                ORDER BY captured_at DESC,id DESC OFFSET 8192
              ))
              AND NOT EXISTS (
                SELECT 1 FROM assistant_live_query_evidence e
                WHERE e.live_segment_id=live_meeting_segments.id
              )
            """, connection, tx);
        prune.Parameters.AddWithValue("meeting", meetingId);
        prune.Parameters.AddWithValue("session", activeSessionId);
        await prune.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return new LiveMeetingAppendResult(activeSessionId, accepted);
    }

    public async Task<bool> HasLiveMeetingContextAsync(Guid meetingId, Guid? recordingSessionId = null)
    {
        await using var connection = await OpenAsync();
        return await HasLiveMeetingContextAsync(connection, null, meetingId, recordingSessionId);
    }

    public async Task<bool> HasActiveRecordingAsync(Guid meetingId, Guid? recordingSessionId = null)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(
              SELECT 1 FROM recording_sessions
              WHERE meeting_id=@meeting
                AND (@session IS NULL OR id=@session)
                AND state IN ('RECORDING','PAUSED','STARTING','AWAITING_AGENT_RECONNECT'))
            """, connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        command.Parameters.AddWithValue("session", (object?)recordingSessionId ?? DBNull.Value);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    /// <summary>
    /// Probes only fresh provisional rows.  This is intentionally separate
    /// from live_context in the worker: the API needs a small routing signal,
    /// not the text that will become an immutable evidence snapshot.
    /// </summary>
    public async Task<AssistantRetrievalProbe> ProbeLiveAssistantContextAsync(
        Guid meetingId,
        Guid? userId,
        bool includeAll,
        string query,
        Guid? recordingSessionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            WITH hits AS (
                SELECT ts_rank_cd(
                    to_tsvector('russian', COALESCE(l.text,'')),
                    websearch_to_tsquery('russian', @query)) AS rank
                FROM live_meeting_segments l
                JOIN recording_sessions r ON r.id=l.recording_session_id
                JOIN meetings m ON m.id=l.meeting_id
                WHERE l.meeting_id=@meeting
                  AND l.expires_at>now()
                  AND (@session IS NULL OR r.id=@session)
                  AND r.state IN ('RECORDING','PAUSED','STARTING','AWAITING_AGENT_RECONNECT','FINALIZING')
                  AND NOT EXISTS (
                    SELECT 1 FROM transcripts t
                    WHERE t.meeting_id=l.meeting_id
                      AND t.version=1
                      AND t.status IN ('READY','PARTIAL_READY')
                  )
                  AND (@include_all OR m.owner_id=@owner)
                  AND to_tsvector('russian', COALESCE(l.text,''))
                      @@ websearch_to_tsquery('russian', @query)
            )
            SELECT COUNT(*)::int, COALESCE(MAX(rank),0.0) FROM hits
            """, connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        command.Parameters.AddWithValue("session", (object?)recordingSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("owner", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("include_all", includeAll);
        command.Parameters.AddWithValue("query", NormalizeAssistantProbeQuery(query));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return !await reader.ReadAsync(cancellationToken)
            ? new AssistantRetrievalProbe(0, 0)
            : new AssistantRetrievalProbe(reader.GetInt32(0), reader.IsDBNull(1) ? 0d : reader.GetDouble(1));
    }

    /// <summary>
    /// Probes the latest usable transcript for one meeting.  The quality gate
    /// is deliberately identical to Assistant retrieval, so AUTO routing can
    /// never select a meeting whose canonical context the worker would reject.
    /// </summary>
    public async Task<AssistantRetrievalProbe> ProbeCurrentMeetingAssistantContextAsync(
        Guid meetingId,
        Guid? userId,
        bool includeAll,
        string query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            WITH hits AS (
                SELECT ts_rank_cd(
                    to_tsvector('russian', COALESCE(s.text,'')),
                    websearch_to_tsquery('russian', @query)) AS rank
                FROM transcript_segments s
                JOIN transcripts t ON t.id=s.transcript_id
                JOIN meetings m ON m.id=t.meeting_id
                WHERE t.meeting_id=@meeting
                  AND (@include_all OR m.owner_id=@owner)
                  AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                  AND t.status IN ('READY','PARTIAL_READY')
                  AND NOT (COALESCE(t.warnings,'[]'::jsonb) ?| ARRAY[
                      'ASR_LANGUAGE_MISMATCH','AUDIO_SIGNAL_UNUSABLE','NO_SPEECH_DETECTED',
                      'SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY'])
                  AND COALESCE(s.is_hidden,false)=false
                  AND to_tsvector('russian', COALESCE(s.text,''))
                      @@ websearch_to_tsquery('russian', @query)
            )
            SELECT COUNT(*)::int, COALESCE(MAX(rank),0.0) FROM hits
            """, connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        command.Parameters.AddWithValue("owner", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("include_all", includeAll);
        command.Parameters.AddWithValue("query", NormalizeAssistantProbeQuery(query));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return !await reader.ReadAsync(cancellationToken)
            ? new AssistantRetrievalProbe(0, 0)
            : new AssistantRetrievalProbe(reader.GetInt32(0), reader.IsDBNull(1) ? 0d : reader.GetDouble(1));
    }

    /// <summary>
    /// History probe over meetings visible to the caller.  It returns only a
    /// count/rank; the worker later applies hybrid retrieval and evidence
    /// snapshots to the same RBAC boundary.
    /// </summary>
    public async Task<AssistantRetrievalProbe> ProbeMeetingMemoryAssistantContextAsync(
        Guid? userId,
        bool includeAll,
        string query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            WITH hits AS (
                SELECT ts_rank_cd(
                    to_tsvector('russian', COALESCE(s.text,'')),
                    websearch_to_tsquery('russian', @query)) AS rank
                FROM transcript_segments s
                JOIN transcripts t ON t.id=s.transcript_id
                JOIN meetings m ON m.id=t.meeting_id
                WHERE (@include_all OR m.owner_id=@owner)
                  AND m.created_at>=now()-interval '90 days'
                  AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                  AND t.status IN ('READY','PARTIAL_READY')
                  AND NOT (COALESCE(t.warnings,'[]'::jsonb) ?| ARRAY[
                      'ASR_LANGUAGE_MISMATCH','AUDIO_SIGNAL_UNUSABLE','NO_SPEECH_DETECTED',
                      'SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY'])
                  AND COALESCE(s.is_hidden,false)=false
                  AND to_tsvector('russian', COALESCE(s.text,''))
                      @@ websearch_to_tsquery('russian', @query)
            )
            SELECT COUNT(*)::int, COALESCE(MAX(rank),0.0) FROM hits
            """, connection);
        command.Parameters.AddWithValue("owner", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("include_all", includeAll);
        command.Parameters.AddWithValue("query", NormalizeAssistantProbeQuery(query));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return !await reader.ReadAsync(cancellationToken)
            ? new AssistantRetrievalProbe(0, 0)
            : new AssistantRetrievalProbe(reader.GetInt32(0), reader.IsDBNull(1) ? 0d : reader.GetDouble(1));
    }

    private static string NormalizeAssistantProbeQuery(string? query)
    {
        var normalized = string.Join(' ', (query ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length > 1000 ? normalized[..1000] : normalized;
    }

    private static async Task<bool> HasLiveMeetingContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid meetingId,
        Guid? recordingSessionId = null)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(
              SELECT 1 FROM live_meeting_segments l
              JOIN recording_sessions r ON r.id=l.recording_session_id
              WHERE l.meeting_id=@meeting AND l.expires_at>now()
                AND (@session IS NULL OR r.id=@session)
                AND r.state IN ('RECORDING','PAUSED','STARTING','AWAITING_AGENT_RECONNECT','FINALIZING')
                AND NOT EXISTS (
                  SELECT 1 FROM transcripts t
                  WHERE t.meeting_id=l.meeting_id
                    AND t.version=1
                    AND t.status IN ('READY','PARTIAL_READY')
                ))
            """, connection, transaction);
        command.Parameters.AddWithValue("meeting", meetingId);
        command.Parameters.AddWithValue("session", (object?)recordingSessionId ?? DBNull.Value);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    public async Task<AssistantQueryRow?> CreateAssistantQueryAsync(Guid? meetingId, string query, Guid? userId, string? requestedMode = null, string source = "DESKTOP", Guid? conversationId = null, double? routerConfidence = null, string? commandId = null, string? traceId = null)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length is 0 or > 2000) return null;
        var scopeType = string.Equals(requestedMode?.Trim(), "GENERAL_CHAT", StringComparison.OrdinalIgnoreCase)
            ? "GENERAL"
            : meetingId.HasValue ? "MEETING" : "GLOBAL";
        var assistantMode = NormalizeAssistantMode(requestedMode, meetingId, scopeType);
        if (assistantMode is null) return null;
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        if (assistantMode == "LIVE_MEETING" && meetingId is Guid liveMeeting)
        {
            if (!await HasLiveMeetingContextAsync(connection, tx, liveMeeting)) return null;
        }
        else if (meetingId is Guid selected)
        {
            if (!await HasUsableTranscriptAsync(connection, tx, selected)) return null;
        }
        else if (assistantMode == "MEETING_MEMORY" && !await HasAnyUsableTranscriptAsync(connection, tx)) return null;
        var id = Guid.NewGuid();
        var normalizedSource = source.Trim().ToUpperInvariant() is "VOICE" or "SYSTEM" ? source.Trim().ToUpperInvariant() : "DESKTOP";
        await using var insert = new NpgsqlCommand("INSERT INTO assistant_queries(id,user_id,meeting_id,assistant_mode,requested_mode,router_confidence,source,grounding_status,answer_metadata,conversation_id,query,status,evidence) VALUES(@id,@user,@meeting,@mode,@requested,@confidence,@source,'PENDING',@metadata::jsonb,@conversation,@query,'QUEUED','[]'::jsonb)", connection, tx);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("user", (object?)userId ?? DBNull.Value);
        insert.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
        insert.Parameters.AddWithValue("query", query);
        insert.Parameters.AddWithValue("mode", assistantMode);
        insert.Parameters.AddWithValue("requested", (object?)requestedMode ?? DBNull.Value);
        insert.Parameters.AddWithValue("confidence", (object?)routerConfidence ?? DBNull.Value);
        insert.Parameters.AddWithValue("source", normalizedSource);
        var queuedAtUtc = DateTime.UtcNow;
        insert.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new { commandId, traceId, queued_at_utc = queuedAtUtc }));
        insert.Parameters.AddWithValue("conversation", (object?)conversationId ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
        var payload = JsonSerializer.Serialize(new { message_id = Guid.NewGuid(), query_id = id, meeting_id = meetingId, assistant_mode = assistantMode, query, kind = "assistant", queued_at_utc = queuedAtUtc });
        await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'llm.assistant',@payload::jsonb)", connection, tx);
        outbox.Parameters.AddWithValue("id", Guid.NewGuid()); outbox.Parameters.AddWithValue("payload", payload);
        await outbox.ExecuteNonQueryAsync();
        // The conversation keeps the last server-resolved scope. This is
        // metadata only; factual context still comes exclusively from the
        // retrieval snapshot created by the Assistant worker.
        if (conversationId is Guid existingConversation && userId is Guid ownerUser)
        {
            await using var updateConversation = new NpgsqlCommand("""
                UPDATE assistant_conversations
                SET assistant_mode=@mode, updated_at=now()
                WHERE id=@conversation AND user_id=@user AND deleted_at IS NULL
                """, connection, tx);
            updateConversation.Parameters.AddWithValue("mode", assistantMode);
            updateConversation.Parameters.AddWithValue("conversation", existingConversation);
            updateConversation.Parameters.AddWithValue("user", ownerUser);
            await updateConversation.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return new AssistantQueryRow(id, meetingId, query, "QUEUED", null, null, JsonDocument.Parse("[]"), null, DateTime.UtcNow, null, assistantMode, requestedMode, routerConfidence, normalizedSource);
    }

    public static AssistantRequestRoute RouteAssistantRequest(string query, string? requestedMode, Guid? activeMeetingId, bool privileged)
        => AssistantModeResolver.ResolveStatic(query, requestedMode, activeMeetingId, privileged);

    /// <summary>
    /// Short conversational utterances remain available during recording.
    /// They do not require transcript evidence and must not be forced into
    /// the provisional LIVE_MEETING scope.
    /// </summary>
    // Kept as source-compatible shims for older contract tests and extensions.
    // New request handling uses AssistantModeResolver exclusively.
    private static bool IsGeneralConversationQuestion(string text)
        => AssistantModeResolver.IsGeneralConversationQuestion(text);

    private static bool LooksLikeMeetingQuestion(string text)
        => AssistantModeResolver.LooksLikeMeetingQuestion(text);

    public async Task<IReadOnlyList<AssistantConversationRow>> ListAssistantConversationsAsync(Guid userId, bool includeArchived = false)
    {
        var result = new List<AssistantConversationRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT id,user_id,title,scope_type,meeting_id,archived_at IS NOT NULL,created_at,updated_at,assistant_mode
            FROM assistant_conversations
            WHERE user_id=@user AND deleted_at IS NULL AND (@include_archived OR archived_at IS NULL)
            ORDER BY updated_at DESC,id DESC
            """, connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("include_archived", includeArchived);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadConversation(reader));
        return result;
    }

    public async Task<AssistantConversationRow?> GetAssistantConversationAsync(Guid id, Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT id,user_id,title,scope_type,meeting_id,archived_at IS NOT NULL,created_at,updated_at,assistant_mode
            FROM assistant_conversations
            WHERE id=@id AND user_id=@user AND deleted_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadConversation(reader) : null;
    }

    /// <summary>
    /// Returns the latest user-owned conversation for an AUTO follow-up when
    /// Desktop has no local pointer (for example after a restart). Only the
    /// opaque conversation scope is returned; message text remains in the
    /// assistant subsystem and is never copied into a voice-specific store.
    /// </summary>
    public async Task<AssistantConversationRow?> GetLatestAssistantConversationAsync(
        Guid userId,
        Guid? meetingId,
        string? requestedMode = "AUTO")
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT id,user_id,title,scope_type,meeting_id,archived_at IS NOT NULL,created_at,updated_at,assistant_mode
            FROM assistant_conversations
            WHERE user_id=@user AND deleted_at IS NULL AND archived_at IS NULL
              AND (@meeting IS NULL OR meeting_id=@meeting)
              AND (@mode='AUTO' OR @mode IS NULL OR assistant_mode=CASE WHEN @mode='MEETING_HISTORY' THEN 'MEETING_MEMORY' ELSE @mode END)
            ORDER BY updated_at DESC,id DESC
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("user", userId);
        // Npgsql cannot infer the type of a DBNull-only parameter when the
        // SQL references it in both IS NULL and equality predicates.  This
        // path is used by GENERAL_CHAT (no active meeting), so leaving the
        // parameter untyped turns every conversation lookup into PostgreSQL
        // 42P08 and prevents the request from ever reaching the worker.
        var meetingParameter = command.Parameters.Add("meeting", NpgsqlDbType.Uuid);
        meetingParameter.Value = meetingId is Guid value ? value : DBNull.Value;
        command.Parameters.AddWithValue("mode", string.IsNullOrWhiteSpace(requestedMode) ? "AUTO" : requestedMode.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadConversation(reader) : null;
    }

    public async Task<AssistantConversationRow?> CreateAssistantConversationAsync(Guid userId, string? title, string scopeType, Guid? meetingId, string? requestedMode = null)
    {
        scopeType = scopeType.Trim().ToUpperInvariant();
        if (scopeType is not ("MEETING" or "GLOBAL" or "GENERAL")) return null;
        var assistantMode = NormalizeAssistantMode(requestedMode, meetingId, scopeType);
        if (assistantMode is null) return null;
        if (scopeType == "MEETING" && meetingId is null) return null;
        if (scopeType is ("GLOBAL" or "GENERAL") && meetingId is not null) return null;
        if (assistantMode == "LIVE_MEETING" && meetingId is Guid liveMeeting && !await HasLiveMeetingContextAsync(liveMeeting)) return null;
        if (assistantMode != "GENERAL_CHAT" && assistantMode != "LIVE_MEETING" && meetingId is Guid selected && !await HasUsableTranscriptAsync(selected)) return null;
        if (assistantMode != "GENERAL_CHAT" && scopeType == "GLOBAL" && !await HasAnyUsableTranscriptAsync()) return null;
        var id = Guid.NewGuid();
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Новый чат" : title.Trim();
        if (normalizedTitle.Length > 120) normalizedTitle = normalizedTitle[..120];
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO assistant_conversations(id,user_id,title,scope_type,meeting_id,assistant_mode)
            VALUES(@id,@user,@title,@scope,@meeting,@mode)
            RETURNING id,user_id,title,scope_type,meeting_id,archived_at IS NOT NULL,created_at,updated_at,assistant_mode
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("title", normalizedTitle);
        command.Parameters.AddWithValue("scope", scopeType);
        command.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
        command.Parameters.AddWithValue("mode", assistantMode);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadConversation(reader) : null;
    }

    public async Task<IReadOnlyList<AssistantMessageRow>> ListAssistantMessagesAsync(Guid conversationId, Guid userId)
    {
        var result = new List<AssistantMessageRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT m.id,m.conversation_id,m.role,m.content,m.status,m.voice_answer,m.evidence,m.error_code,
                   q.id,m.created_at,m.completed_at,q.answer_metadata
            FROM assistant_messages m
            JOIN assistant_conversations c ON c.id=m.conversation_id
            LEFT JOIN assistant_queries q ON q.user_message_id=m.id OR q.assistant_message_id=m.id
            WHERE m.conversation_id=@conversation AND c.user_id=@user AND c.deleted_at IS NULL
            ORDER BY m.created_at,m.id
            """, connection);
        command.Parameters.AddWithValue("conversation", conversationId);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadMessage(reader));
        return result;
    }

    public async Task<AssistantMessageCreateResult?> CreateAssistantMessageAsync(Guid conversationId, Guid userId, string query, Guid? retryOf = null)
    {
        query = query.Trim();
        if (query.Length is 0 or > 2000) return null;
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var conversation = new NpgsqlCommand("""
            SELECT title,scope_type,meeting_id,assistant_mode FROM assistant_conversations
            WHERE id=@id AND user_id=@user AND deleted_at IS NULL
            FOR UPDATE
            """, connection, transaction);
        conversation.Parameters.AddWithValue("id", conversationId);
        conversation.Parameters.AddWithValue("user", userId);
        await using var contextReader = await conversation.ExecuteReaderAsync();
        if (!await contextReader.ReadAsync()) return null;
        var title = contextReader.GetString(0);
        var scopeType = contextReader.GetString(1);
        var meetingId = contextReader.IsDBNull(2) ? (Guid?)null : contextReader.GetGuid(2);
        var assistantMode = contextReader.GetString(3);
        await contextReader.CloseAsync();
        if (assistantMode == "LIVE_MEETING" && meetingId is Guid liveMeeting && !await HasLiveMeetingContextAsync(connection, transaction, liveMeeting)) return null;
        if (assistantMode != "GENERAL_CHAT" && assistantMode != "LIVE_MEETING" && meetingId is Guid selected && !await HasUsableTranscriptAsync(connection, transaction, selected)) return null;
        if (assistantMode != "GENERAL_CHAT" && scopeType == "GLOBAL" && !await HasAnyUsableTranscriptAsync(connection, transaction)) return null;

        var userMessageId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var queryId = Guid.NewGuid();
        await using (var insertUser = new NpgsqlCommand("INSERT INTO assistant_messages(id,conversation_id,role,content,status,evidence,retry_of) VALUES(@id,@conversation,'USER',@content,'READY','[]'::jsonb,@retry)", connection, transaction))
        {
            insertUser.Parameters.AddWithValue("id", userMessageId);
            insertUser.Parameters.AddWithValue("conversation", conversationId);
            insertUser.Parameters.AddWithValue("content", query);
            insertUser.Parameters.AddWithValue("retry", (object?)retryOf ?? DBNull.Value);
            await insertUser.ExecuteNonQueryAsync();
        }
        await using (var insertAssistant = new NpgsqlCommand("INSERT INTO assistant_messages(id,conversation_id,role,content,status,evidence) VALUES(@id,@conversation,'ASSISTANT','', 'QUEUED','[]'::jsonb)", connection, transaction))
        {
            insertAssistant.Parameters.AddWithValue("id", assistantMessageId);
            insertAssistant.Parameters.AddWithValue("conversation", conversationId);
            await insertAssistant.ExecuteNonQueryAsync();
        }
        await using (var insertQuery = new NpgsqlCommand("INSERT INTO assistant_queries(id,user_id,meeting_id,assistant_mode,conversation_id,user_message_id,assistant_message_id,query,status,evidence) VALUES(@id,@user,@meeting,@mode,@conversation,@user_message,@assistant_message,@query,'QUEUED','[]'::jsonb)", connection, transaction))
        {
            insertQuery.Parameters.AddWithValue("id", queryId);
            insertQuery.Parameters.AddWithValue("user", userId);
            insertQuery.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
            insertQuery.Parameters.AddWithValue("mode", assistantMode);
            insertQuery.Parameters.AddWithValue("conversation", conversationId);
            insertQuery.Parameters.AddWithValue("user_message", userMessageId);
            insertQuery.Parameters.AddWithValue("assistant_message", assistantMessageId);
            insertQuery.Parameters.AddWithValue("query", query);
            await insertQuery.ExecuteNonQueryAsync();
        }
        var payload = JsonSerializer.Serialize(new
        {
            message_id = Guid.NewGuid(), query_id = queryId, conversation_id = conversationId,
            user_message_id = userMessageId, assistant_message_id = assistantMessageId,
            meeting_id = meetingId, assistant_mode = assistantMode, query, kind = "assistant"
        });
        await using (var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'llm.assistant',@payload::jsonb)", connection, transaction))
        {
            outbox.Parameters.AddWithValue("id", Guid.NewGuid());
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync();
        }
        if (title == "Новый чат")
        {
            var generatedTitle = query.Length > 80 ? query[..80].TrimEnd() + "…" : query;
            await using var updateTitle = new NpgsqlCommand("UPDATE assistant_conversations SET title=@title,updated_at=now() WHERE id=@id", connection, transaction);
            updateTitle.Parameters.AddWithValue("title", generatedTitle);
            updateTitle.Parameters.AddWithValue("id", conversationId);
            await updateTitle.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        var now = DateTime.UtcNow;
        var userRow = new AssistantMessageRow(userMessageId, conversationId, "USER", query, "READY", null, JsonDocument.Parse("[]"), null, queryId, now, now);
        var assistantRow = new AssistantMessageRow(assistantMessageId, conversationId, "ASSISTANT", string.Empty, "QUEUED", null, JsonDocument.Parse("[]"), null, queryId, now, null);
        return new AssistantMessageCreateResult(userRow, assistantRow, queryId);
    }

    public async Task<bool> UpdateAssistantConversationAsync(Guid id, Guid userId, string? title, bool? archived)
    {
        var normalizedTitle = title?.Trim();
        if (normalizedTitle is not null && (normalizedTitle.Length is 0 or > 120)) return false;
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE assistant_conversations
            SET title=COALESCE(@title,title), archived_at=CASE WHEN @archived IS NULL THEN archived_at WHEN @archived THEN COALESCE(archived_at,now()) ELSE NULL END, updated_at=now()
            WHERE id=@id AND user_id=@user AND deleted_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("title", (object?)normalizedTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("archived", (object?)archived ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAssistantConversationAsync(Guid id, Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE assistant_conversations SET deleted_at=now(),updated_at=now() WHERE id=@id AND user_id=@user AND deleted_at IS NULL", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", userId);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    private async Task<bool> HasUsableTranscriptAsync(Guid meetingId)
    {
        await using var connection = await OpenAsync();
        return await HasUsableTranscriptAsync(connection, null, meetingId);
    }

    private static async Task<bool> HasUsableTranscriptAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid meetingId)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM transcripts t WHERE t.meeting_id=@meeting AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id) AND t.status IN ('READY','PARTIAL_READY'))", connection, transaction);
        command.Parameters.AddWithValue("meeting", meetingId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> HasAnyUsableTranscriptAsync()
    {
        await using var connection = await OpenAsync();
        return await HasAnyUsableTranscriptAsync(connection, null);
    }

    private static async Task<bool> HasAnyUsableTranscriptAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM transcripts WHERE status IN ('READY','PARTIAL_READY'))", connection, transaction);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static AssistantConversationRow ReadConversation(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetBoolean(5), reader.GetDateTime(6), reader.GetDateTime(7), reader.GetString(8));

    private static AssistantMessageRow ReadMessage(NpgsqlDataReader reader)
    {
        JsonElement? timings = null;
        if (!reader.IsDBNull(11))
        {
            var metadata = reader.GetFieldValue<JsonDocument>(11);
            if (metadata.RootElement.TryGetProperty("timings", out var value)) timings = value.Clone();
        }
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<JsonDocument>(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.GetDateTime(9), reader.IsDBNull(10) ? null : reader.GetDateTime(10), timings);
    }

    public async Task<AssistantQueryRow?> GetAssistantQueryAsync(Guid id, Guid? userId, bool includeAll)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,query,status,answer,voice_answer,evidence,error_code,created_at,completed_at,assistant_mode,requested_mode,router_confidence,source,grounding_status,answer_metadata,transcript_id,transcript_version,retry_count,next_retry_at,retryable FROM assistant_queries WHERE id=@id AND (@include_all OR user_id=@user)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("include_all", includeAll);
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null : new AssistantQueryRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<JsonDocument>(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetDouble(12), reader.IsDBNull(13) ? "DESKTOP" : reader.GetString(13), reader.IsDBNull(14) ? "PENDING" : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetFieldValue<JsonDocument>(15), reader.IsDBNull(16) ? null : reader.GetGuid(16), reader.IsDBNull(17) ? null : reader.GetInt32(17), reader.IsDBNull(18) ? 0 : reader.GetInt32(18), reader.IsDBNull(19) ? null : reader.GetDateTime(19), reader.IsDBNull(20) || reader.GetBoolean(20));
    }    public async Task<SummaryRow?> GetLatestSummaryAsync(Guid meetingId)
    {
        await using var connection = await OpenAsync(); await using var command = new NpgsqlCommand("SELECT id,meeting_id,transcript_id,version,status,model_name,prompt_version,source_hash,content,created_at FROM summaries WHERE meeting_id=@id ORDER BY version DESC LIMIT 1", connection); command.Parameters.AddWithValue("id", meetingId); await using var reader = await command.ExecuteReaderAsync(); return !await reader.ReadAsync() ? null : ReadSummary(reader);
    }

    public async Task<RegistryPage<SummaryRegistryRow>> ListSummaryRegistryPageAsync(int page, int pageSize, string? search, string? status, Guid? meetingId, string? sort, Guid ownerId, bool includeAll)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (page - 1) * pageSize;
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        await using var connection = await OpenAsync();
        const string filter = "(@include_all OR m.owner_id=@owner) AND (@meeting IS NULL OR m.id=@meeting) AND (@search IS NULL OR m.title ILIKE '%' || @search || '%' OR COALESCE(s.content::text,'') ILIKE '%' || @search || '%') AND (@status IS NULL OR COALESCE(s.status,'SUMMARY_NOT_READY')=@status)";
        await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM meetings m LEFT JOIN LATERAL (SELECT status,content FROM summaries WHERE meeting_id=m.id ORDER BY version DESC LIMIT 1) s ON true WHERE {filter}", connection);
        AddRegistryParameters(count, includeAll, ownerId, meetingId, normalizedSearch, normalizedStatus);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync());
        var order = string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase) ? "m.created_at ASC" : "m.created_at DESC";
        await using var command = new NpgsqlCommand($"""
            SELECT m.id,m.title,m.description,m.status,m.created_at,
                   s.id,s.meeting_id,s.transcript_id,s.version,s.status,s.model_name,s.prompt_version,s.source_hash,s.content,s.created_at
            FROM meetings m
            LEFT JOIN LATERAL (SELECT id,meeting_id,transcript_id,version,status,model_name,prompt_version,source_hash,content,created_at FROM summaries WHERE meeting_id=m.id ORDER BY version DESC LIMIT 1) s ON true
            WHERE {filter} ORDER BY {order} LIMIT @limit OFFSET @offset
            """, connection);
        AddRegistryParameters(command, includeAll, ownerId, meetingId, normalizedSearch, normalizedStatus);
        command.Parameters.AddWithValue("limit", pageSize); command.Parameters.AddWithValue("offset", offset);
        var items = new List<SummaryRegistryRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var meeting = new MeetingRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
            SummaryRow? summary = null;
            if (!reader.IsDBNull(5))
            {
                var content = reader.GetFieldValue<JsonDocument>(13);
                summary = ReadSummary(reader, 5, content);
            }
            items.Add(new SummaryRegistryRow(meeting, summary));
        }
        return new RegistryPage<SummaryRegistryRow>(items, total, offset + items.Count < total);
    }

    public async Task<RegistryPage<SpeakerRegistryRow>> ListSpeakerRegistryPageAsync(int page, int pageSize, string? search, string? status, Guid? meetingId, string? sort, Guid ownerId, bool includeAll)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200); var offset = (page - 1) * pageSize;
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        await using var connection = await OpenAsync();
        const string filter = "(@include_all OR m.owner_id=@owner) AND (@meeting IS NULL OR m.id=@meeting) AND (@search IS NULL OR ms.display_name ILIKE '%' || @search || '%' OR ms.stable_key ILIKE '%' || @search || '%' OR m.title ILIKE '%' || @search || '%') AND (@status IS NULL OR m.status=@status)";
        await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM meeting_speakers ms JOIN meetings m ON m.id=ms.meeting_id WHERE {filter}", connection);
        AddRegistryParameters(count, includeAll, ownerId, meetingId, normalizedSearch, normalizedStatus);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync());
        var order = string.Equals(sort, "recent", StringComparison.OrdinalIgnoreCase) ? "m.created_at DESC" : "LOWER(ms.display_name),m.created_at DESC";
        await using var command = new NpgsqlCommand($"SELECT m.id,m.title,m.description,m.status,m.created_at,ms.id,ms.stable_key,COALESCE(ms.display_name,''),ms.speaker_profile_id,ms.profile_confidence,COALESCE(ms.profile_match_status,'UNMATCHED'),ms.profile_match_reason,ms.profile_suggestion_name FROM meeting_speakers ms JOIN meetings m ON m.id=ms.meeting_id WHERE {filter} ORDER BY {order} LIMIT @limit OFFSET @offset", connection);
        AddRegistryParameters(command, includeAll, ownerId, meetingId, normalizedSearch, normalizedStatus); command.Parameters.AddWithValue("limit", pageSize); command.Parameters.AddWithValue("offset", offset);
        var items = new List<SpeakerRegistryRow>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var meeting = new MeetingRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
            items.Add(new SpeakerRegistryRow(meeting, new SpeakerRow(reader.GetGuid(5), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.IsDBNull(9) ? null : reader.GetDouble(9), reader.IsDBNull(10) ? "UNMATCHED" : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12))));
        }
        return new RegistryPage<SpeakerRegistryRow>(items, total, offset + items.Count < total);
    }

    public async Task<RegistryPage<SpeakerProfileRow>> ListSpeakerProfilesAsync(int page, int pageSize, string? search, string? status, Guid ownerId, bool includeAll)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200); var offset = (page - 1) * pageSize;
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        await using var connection = await OpenAsync();
        const string filter = "(@include_all OR owner_user_id=@owner) AND (@search IS NULL OR display_name ILIKE '%' || @search || '%') AND (@status IS NULL OR status=@status)";
        await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM speaker_profiles WHERE {filter}", connection);
        AddProfileParameters(count, includeAll, ownerId, normalizedSearch, normalizedStatus);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync());
        await using var command = new NpgsqlCommand($"SELECT id,display_name,embedding_dimensions,samples,confidence,meetings_count,duration_ms,status,embedding_model,last_seen_at FROM speaker_profiles WHERE {filter} ORDER BY LOWER(display_name),updated_at DESC LIMIT @limit OFFSET @offset", connection);
        AddProfileParameters(command, includeAll, ownerId, normalizedSearch, normalizedStatus); command.Parameters.AddWithValue("limit", pageSize); command.Parameters.AddWithValue("offset", offset);
        var items = new List<SpeakerProfileRow>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(new SpeakerProfileRow(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9)));
        return new RegistryPage<SpeakerProfileRow>(items, total, offset + items.Count < total);
    }

    public async Task<SpeakerProfileRow?> CreateSpeakerProfileAsync(Guid ownerId, string displayName, JsonDocument? embedding, string? embeddingModel)
    {
        var name = displayName.Trim(); if (name.Length is < 1 or > 200) return null;
        var values = embedding is null ? "[]" : embedding.RootElement.GetRawText();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO speaker_profiles(owner_user_id,display_name,embedding_centroid,embedding_dimensions,embedding_model) VALUES(@owner,@name,@embedding::jsonb,COALESCE(jsonb_array_length(@embedding::jsonb),0),@model) RETURNING id,display_name,embedding_dimensions,samples,confidence,meetings_count,duration_ms,status,embedding_model,last_seen_at", connection);
        command.Parameters.AddWithValue("owner", ownerId); command.Parameters.AddWithValue("name", name); command.Parameters.AddWithValue("embedding", values); command.Parameters.AddWithValue("model", (object?)embeddingModel?.Trim() ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(); return await reader.ReadAsync() ? ReadSpeakerProfile(reader) : null;
    }

    public async Task<bool> RenameSpeakerProfileAsync(Guid id, Guid ownerId, bool includeAll, string displayName)
    {
        var name = displayName.Trim(); if (name.Length is < 1 or > 200) return false;
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE speaker_profiles SET display_name=@name,updated_at=now() WHERE id=@id AND (@include_all OR owner_user_id=@owner)", connection);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("owner", ownerId); command.Parameters.AddWithValue("include_all", includeAll); command.Parameters.AddWithValue("name", name);
        return await command.ExecuteNonQueryAsync() == 1;
    }

    public async Task<bool> EnrollSpeakerProfileAsync(Guid id, Guid ownerId, bool includeAll, JsonDocument embedding, long durationMs)
    {
        if (embedding.RootElement.ValueKind != JsonValueKind.Array || embedding.RootElement.GetArrayLength() is < 1 or > 2048 || durationMs < 0) return false;
        var incoming = JsonSerializer.Deserialize<double[]>(embedding.RootElement.GetRawText());
        if (incoming is null || incoming.Length == 0 || incoming.Any(value => double.IsNaN(value) || double.IsInfinity(value))) return false;
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var read = new NpgsqlCommand("SELECT embedding_centroid,samples FROM speaker_profiles WHERE id=@id AND (@include_all OR owner_user_id=@owner) FOR UPDATE", connection, transaction);
        read.Parameters.AddWithValue("id", id); read.Parameters.AddWithValue("owner", ownerId); read.Parameters.AddWithValue("include_all", includeAll);
        await using var reader = await read.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return false;
        var existingJson = reader.IsDBNull(0) ? "[]" : reader.GetFieldValue<JsonDocument>(0).RootElement.GetRawText();
        var samples = reader.GetInt32(1);
        await reader.CloseAsync();
        var previous = JsonSerializer.Deserialize<double[]>(existingJson);
        var merged = incoming;
        if (previous is { Length: > 0 } && previous.Length == incoming.Length && samples > 0)
        {
            merged = previous.Select((value, index) => (value * samples + incoming[index]) / (samples + 1)).ToArray();
            var norm = Math.Sqrt(merged.Sum(value => value * value));
            if (norm > 1e-12) merged = merged.Select(value => value / norm).ToArray();
        }
        var values = JsonSerializer.Serialize(merged);
        await using var command = new NpgsqlCommand("UPDATE speaker_profiles SET embedding_centroid=@embedding::jsonb,embedding_dimensions=jsonb_array_length(@embedding::jsonb),samples=samples+1,duration_ms=duration_ms+@duration,updated_at=now(),last_seen_at=now() WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("embedding", values); command.Parameters.AddWithValue("duration", durationMs);
        var updated = await command.ExecuteNonQueryAsync() == 1;
        if (updated) await transaction.CommitAsync();
        return updated;
    }

    private static void AddProfileParameters(NpgsqlCommand command, bool includeAll, Guid ownerId, string? search, string? status)
    {
        command.Parameters.AddWithValue("include_all", includeAll); command.Parameters.AddWithValue("owner", ownerId); command.Parameters.AddWithValue("search", (object?)search ?? DBNull.Value); command.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
    }

    private static SpeakerProfileRow ReadSpeakerProfile(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9));

    public async Task<RegistryPage<ActionItemRegistryRow>> ListActionItemRegistryPageAsync(int page, int pageSize, string? search, string? status, Guid? meetingId, string? sort, Guid ownerId, bool includeAll)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200); var offset = (page - 1) * pageSize;
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        await using var connection = await OpenAsync();
        const string filter = "(@include_all OR m.owner_id=@owner) AND (@meeting IS NULL OR m.id=@meeting) AND (@search IS NULL OR a.task ILIKE '%' || @search || '%' OR COALESCE(a.responsible,'') ILIKE '%' || @search || '%' OR m.title ILIKE '%' || @search || '%') AND (@status IS NULL OR a.status=@status)";
        const string joins = "action_items a JOIN meetings m ON m.id=a.meeting_id";
        await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM {joins} WHERE {filter}", connection);
        AddRegistryParameters(count, includeAll, ownerId, meetingId, normalizedSearch, normalizedStatus);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync());
        var order = string.Equals(sort, "deadline", StringComparison.OrdinalIgnoreCase) ? "a.deadline NULLS LAST,a.created_at DESC" : "a.created_at DESC";
        await using var command = new NpgsqlCommand($"SELECT m.id,m.title,m.description,m.status,m.created_at,a.id,a.meeting_id,a.summary_id,a.task,a.responsible,a.deadline,a.status,a.evidence_segment_id,a.created_at FROM {joins} WHERE {filter} ORDER BY {order} LIMIT @limit OFFSET @offset", connection);
        AddRegistryParameters(command, includeAll, ownerId, meetingId, normalizedSearch, normalizedStatus); command.Parameters.AddWithValue("limit", pageSize); command.Parameters.AddWithValue("offset", offset);
        var items = new List<ActionItemRegistryRow>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var meeting = new MeetingRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
            items.Add(new ActionItemRegistryRow(meeting, new ActionItemRow(reader.GetGuid(5), reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetDateTime(10), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetGuid(12), reader.GetDateTime(13))));
        }
        return new RegistryPage<ActionItemRegistryRow>(items, total, offset + items.Count < total);
    }

    private static void AddRegistryParameters(NpgsqlCommand command, bool includeAll, Guid ownerId, Guid? meetingId, string? search, string? status)
    {
        command.Parameters.AddWithValue("include_all", includeAll); command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
        command.Parameters.Add("search", NpgsqlDbType.Text).Value = (object?)search ?? DBNull.Value;
        command.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status ?? DBNull.Value;
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
                   ts_rank_cd(to_tsvector('russian',COALESCE(s.text,'')),websearch_to_tsquery('russian',@query)),
                   m.created_at
            FROM transcript_segments s
            JOIN transcripts t ON t.id=s.transcript_id
            JOIN meetings m ON m.id=t.meeting_id
            LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
            WHERE (@include_all OR m.owner_id=@owner)
              AND (@meeting IS NULL OR m.id=@meeting)
              AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
              AND COALESCE(s.is_hidden,false)=false
              AND to_tsvector('russian',COALESCE(s.text,'')) @@ websearch_to_tsquery('russian',@query)
            ORDER BY ts_rank_cd(to_tsvector('russian',COALESCE(s.text,'')),websearch_to_tsquery('russian',@query)) DESC,
                     m.created_at DESC,s.ordinal
            LIMIT @limit OFFSET @offset
            """, connection);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
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
        var activeGpuJobs = await ScalarLongAsync("SELECT COUNT(*) FROM jobs WHERE type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS','TRANSCRIPT_ENRICH','SUMMARIZE') AND status IN ('QUEUED','RUNNING')");
        var failedGpuJobs24h = await ScalarLongAsync("SELECT COUNT(*) FROM jobs WHERE type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS','TRANSCRIPT_ENRICH','SUMMARIZE') AND status='FAILED' AND updated_at >= now()-interval '24 hours' AND (error_code LIKE 'GPU_%' OR error_code LIKE 'CUDA_%')");
        var pendingOutbox = await ScalarLongAsync("SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL");
        var activeAgents = await ScalarLongAsync("SELECT COUNT(*) FROM recorder_agents WHERE status <> 'OFFLINE' AND last_seen_at >= now()-interval '90 seconds'");
        var unavailableAgents = await ScalarLongAsync("SELECT COUNT(*) FROM recorder_agents WHERE last_seen_at IS NULL OR last_seen_at < now()-interval '90 seconds'");
        // A long-running recording is not stale merely because its row is
        // older than five minutes. The current-user Host advertises the
        // active local session in its heartbeat; only sessions without that
        // confirmation are surfaced as interrupted.
        var staleRecordingSessions = await ScalarLongAsync("SELECT COUNT(*) FROM recording_sessions s LEFT JOIN recorder_agents a ON a.id=s.agent_id WHERE s.state IN ('RECORDING','AWAITING_AGENT_RECONNECT') AND (s.local_session_id IS NULL OR a.id IS NULL OR COALESCE(a.capabilities->'deviceHealth'->>'activeSessionId','') <> s.local_session_id::text) AND (COALESCE(a.last_seen_at,s.created_at) < now()-interval '5 minutes' OR (COALESCE(s.total_samples,0)=0 AND COALESCE(s.started_at,s.created_at) < now()-interval '5 minutes'))");
        // Migrations 039 add stage/progress timestamps. Keep readiness
        // backwards compatible while an older API is still serving traffic;
        // once both columns exist they become the authoritative liveness age.
        var hasProgressColumns = await ScalarLongAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_name='jobs' AND column_name IN ('stage_changed_at','progress_changed_at')") == 2;
        var progressExpression = hasProgressColumns
            ? "COALESCE(j.progress_changed_at,j.stage_changed_at,j.last_heartbeat,j.updated_at)"
            : "COALESCE(j.last_heartbeat,j.updated_at)";
        var gpuOwnership = connection.CreateCommand();
        gpuOwnership.CommandText = $"""
            WITH gpu AS (
                SELECT j.id,j.status,
                       {progressExpression} AS progress_at,
                       EXISTS (
                           SELECT 1 FROM worker_instances wi
                           WHERE wi.worker_name='gpu-worker'
                             AND wi.current_job_id=j.id
                             AND wi.status IN ('READY','BUSY','DEGRADED')
                             AND wi.last_seen_at >= now()-interval '60 seconds'
                       ) AS live_owner,
                       EXISTS (
                           SELECT 1 FROM inbox_messages im
                           WHERE im.job_id=j.id AND im.lease_expires_at > now()
                       ) AS active_lease
                FROM jobs j
                WHERE j.type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS','TRANSCRIPT_ENRICH','SUMMARIZE')
                  AND j.status IN ('QUEUED','RUNNING')
            )
            SELECT
                COALESCE((SELECT COUNT(*) FROM gpu WHERE status='RUNNING' AND live_owner),0),
                COALESCE((SELECT COUNT(*) FROM gpu WHERE status='RUNNING' AND NOT live_owner),0),
                COALESCE((SELECT COUNT(*) FROM gpu WHERE active_lease),0),
                COALESCE((SELECT MAX(EXTRACT(EPOCH FROM (now()-progress_at))) FROM gpu WHERE status='RUNNING'),0),
                COALESCE((SELECT COUNT(*) FROM assistant_queries WHERE status='QUEUED'),0),
                COALESCE((SELECT COUNT(*) FROM jobs WHERE type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS') AND status='QUEUED'),0)
            """;
        await using (gpuOwnership)
        await using (var reader = await gpuOwnership.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return new OperationsSnapshot(queuedJobs, runningJobs, failedJobs24h, staleLeases, activeGpuJobs, failedGpuJobs24h, pendingOutbox, activeAgents, unavailableAgents, staleRecordingSessions, DateTimeOffset.UtcNow);
            return new OperationsSnapshot(
                queuedJobs, runningJobs, failedJobs24h, staleLeases, activeGpuJobs, failedGpuJobs24h,
                pendingOutbox, activeAgents, unavailableAgents, staleRecordingSessions, DateTimeOffset.UtcNow,
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetInt64(4), reader.GetInt64(5));
        }
    }

    public async Task<LlmRuntimeSnapshot?> GetLlmRuntimeSnapshotAsync()
    {
        await using var connection = await OpenAsync();
        await using (var columns = new NpgsqlCommand("""
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name='gpu_runtime_coordination'
              AND column_name IN ('llm_owner_heartbeat_at','llm_active_workload','llm_active_request_id')
            """, connection))
        {
            if (Convert.ToInt64(await columns.ExecuteScalarAsync()) != 3)
                return null;
        }
        await using var command = new NpgsqlCommand("""
            SELECT llm_owner,llm_owner_heartbeat_at,llm_active,llm_active_workload,llm_active_request_id
            FROM gpu_runtime_coordination WHERE id=1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new LlmRuntimeSnapshot(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
            !reader.IsDBNull(2) && reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task<IReadOnlyList<WorkerRuntimeRow>> ListWorkerRuntimeAsync()
    {
        var result = new List<WorkerRuntimeRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT worker_name,instance_id,status,last_seen_at,current_job_id,version,capabilities,last_error_code FROM worker_instances ORDER BY worker_name,instance_id",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new WorkerRuntimeRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetString(5),
                reader.GetFieldValue<JsonDocument>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
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
        command.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
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
            search.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
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
        command.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
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
        var pipelineCorrelationId = await GetPipelineCorrelationIdForMeetingAsync(connection, tx, meetingId);
        await using var job = new NpgsqlCommand("INSERT INTO jobs(id,meeting_id,type,status,stage,progress,input_transcript_id,pipeline_correlation_id) VALUES(@id,@meeting,'SUMMARIZE','QUEUED','TRANSCRIPT_READY',0,@transcript,@correlation) ON CONFLICT DO NOTHING RETURNING id", connection, tx);
        job.Parameters.AddWithValue("id", jobId); job.Parameters.AddWithValue("meeting", meetingId); job.Parameters.AddWithValue("transcript", transcriptGuid);
        job.Parameters.AddWithValue("correlation", (object?)pipelineCorrelationId ?? DBNull.Value);
        if (await job.ExecuteScalarAsync() is not Guid insertedJobId)
        {
            await using var duplicate = new NpgsqlCommand("SELECT id FROM jobs WHERE input_transcript_id=@transcript AND type='SUMMARIZE' AND status IN ('QUEUED','RUNNING') ORDER BY created_at DESC LIMIT 1", connection, tx);
            duplicate.Parameters.AddWithValue("transcript", transcriptGuid);
            var existingJobId = await duplicate.ExecuteScalarAsync();
            await tx.CommitAsync();
            return existingJobId as Guid?;
        }
        jobId = insertedJobId;
        var payload = JsonSerializer.Serialize(new
        {
            message_id = Guid.NewGuid(),
            job_id = jobId,
            meeting_id = meetingId,
            transcript_id = transcriptGuid,
            summary_profile = string.IsNullOrWhiteSpace(options?.Profile) ? (configuration["AUTO_SUMMARY_PROFILE"] ?? "MEETING_PROTOCOL_RU").Trim().ToUpperInvariant() : options.Profile.Trim().ToUpperInvariant(),
            prompt_version = string.IsNullOrWhiteSpace(options?.PromptVersion) ? "meeting-protocol-ru-v2" : options.PromptVersion.Trim(),
            reason = options?.Reason?.Trim(),
            meeting_context = options?.MeetingContext?.RootElement ?? JsonSerializer.SerializeToElement(new { }),
            source_hash = (string?)null,
            correlation_id = pipelineCorrelationId
        });
        await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'llm.summarize',@payload::jsonb)", connection, tx);
        outbox.Parameters.AddWithValue("id", Guid.NewGuid()); outbox.Parameters.AddWithValue("payload", payload); await outbox.ExecuteNonQueryAsync();
        await AppendAuditEventAsync(connection, tx, actorUserId, meetingId, "SUMMARY", jobId, "SUMMARY_REBUILD_QUEUED", null, JsonSerializer.Serialize(new { jobId, transcriptId = transcriptGuid, reason = options?.Reason }));
        await using var meeting = new NpgsqlCommand("UPDATE meetings SET status='SUMMARIZING' WHERE id=@meeting", connection, tx);
        meeting.Parameters.AddWithValue("meeting", meetingId); await meeting.ExecuteNonQueryAsync();
        await tx.CommitAsync(); return jobId;
    }

    /// <summary>
    /// Manual summary requests obey the same quality boundary as automatic
    /// summaries.  A draft V1 is useful to a person but must not become an
    /// authoritative Qwen protocol before enrichment and quality checks pass.
    /// </summary>
    public async Task<SummaryEligibility> GetSummaryEligibilityAsync(Guid meetingId, int? transcriptVersion = null)
    {
        await using var connection = await OpenAsync();
        var sql = transcriptVersion is int
            ? "SELECT status,COALESCE(version_kind,'GENERATED'),COALESCE(warnings,'[]'::jsonb)::text FROM transcripts WHERE meeting_id=@meeting AND version=@version LIMIT 1"
            : "SELECT status,COALESCE(version_kind,'GENERATED'),COALESCE(warnings,'[]'::jsonb)::text FROM transcripts WHERE meeting_id=@meeting ORDER BY version DESC LIMIT 1";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        if (transcriptVersion is int version) command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new SummaryEligibility(false, false, "TRANSCRIPT_REQUIRED");

        var status = reader.GetString(0).ToUpperInvariant();
        var versionKind = reader.GetString(1).ToUpperInvariant();
        string[] warnings;
        try { warnings = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? []; }
        catch (JsonException) { return new SummaryEligibility(true, false, "SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY"); }

        var warningSet = new HashSet<string>(warnings.Select(item => item.ToUpperInvariant()), StringComparer.Ordinal);
        var blockingWarnings = new HashSet<string>(StringComparer.Ordinal)
        {
            "NO_SPEECH_DETECTED", "ASR_LANGUAGE_MISMATCH", "AUDIO_SIGNAL_UNUSABLE", "NEEDS_REVIEW", "REQUIRES_REVIEW", "SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY"
        };
        if (versionKind != "ENRICHED") return new SummaryEligibility(true, false, "SUMMARY_REQUIRES_ENRICHED_V2");
        if (status is not "READY" || warningSet.Overlaps(blockingWarnings))
            return new SummaryEligibility(true, false, "SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY");
        return new SummaryEligibility(true, true);
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
        audit.Parameters.Add("meeting", NpgsqlDbType.Uuid).Value = (object?)meetingId ?? DBNull.Value;
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
    private static RecordingSessionRow ReadSession(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.IsDBNull(4)?null:r.GetDateTime(4),r.IsDBNull(5)?null:r.GetDateTime(5),r.FieldCount > 6 && !r.IsDBNull(6) ? r.GetString(6) : null);
    private static string NormalizeAcousticProfile(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "AUTO" : value.Trim().ToUpperInvariant();
        return normalized is "STANDARD" or "LARGE_ROOM" ? normalized : "AUTO";
    }

    private static async Task<string> ReadAcousticProfileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId)
    {
        await using var command = new NpgsqlCommand("SELECT acoustic_profile FROM recording_sessions WHERE id=@session", connection, transaction);
        command.Parameters.AddWithValue("session", sessionId);
        return NormalizeAcousticProfile(await command.ExecuteScalarAsync() as string);
    }

    private static async Task<string?> GetPipelineCorrelationIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId)
    {
        await using var command = new NpgsqlCommand("SELECT pipeline_correlation_id FROM recording_sessions WHERE id=@session", connection, transaction);
        command.Parameters.AddWithValue("session", sessionId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> GetPipelineCorrelationIdForMeetingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid meetingId)
    {
        await using var command = new NpgsqlCommand("SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=@meeting ORDER BY created_at DESC LIMIT 1", connection, transaction);
        command.Parameters.AddWithValue("meeting", meetingId);
        return await command.ExecuteScalarAsync() as string;
    }
    private static SummaryRow ReadSummary(NpgsqlDataReader r)
    {
        var content = r.GetFieldValue<JsonDocument>(8);
        return ReadSummary(r, 0, content);
    }

    private static SummaryRow ReadSummary(NpgsqlDataReader r, int offset, JsonDocument content)
    {
        string? Optional(string name)
        {
            if (!content.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            return value.GetString();
        }
        return new SummaryRow(
            r.GetGuid(offset), r.GetGuid(offset + 1), r.IsDBNull(offset + 2) ? null : r.GetGuid(offset + 2),
            r.GetInt32(offset + 3), r.GetString(offset + 4), r.GetString(offset + 5), r.GetString(offset + 6),
            r.GetString(offset + 7), content, r.GetDateTime(offset + 9),
            Optional("contentValidity"), Optional("generationState"), Optional("errorCode"));
    }
}
public sealed record MissingRecordingChunks(Guid TrackId, IReadOnlyList<int> Sequences);
public sealed record FinalizeRecordingResult(bool Found, bool Accepted, Guid? MeetingId, Guid? JobId, Guid? MediaAssetId, IReadOnlyList<MissingRecordingChunks> Missing, string? ErrorCode);

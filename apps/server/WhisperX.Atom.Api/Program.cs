using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<UnifiedProductStore>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(_ => true)));

var app = builder.Build();
app.UseCors();

var db = app.Services.GetRequiredService<Database>();
var unified = app.Services.GetRequiredService<UnifiedProductStore>();
await db.InitializeAsync();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/agents/enroll"))
    {
        var expectedEnrollment = builder.Configuration["AGENT_ENROLLMENT_SECRET"] ?? "";
        var suppliedEnrollment = context.Request.Headers["X-Agent-Enrollment-Secret"].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(expectedEnrollment);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedEnrollment);
        if (expectedBytes.Length == 0 || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "agent_enrollment_required" });
            return;
        }
        await next();
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api/v1/agents") || context.Request.Path.StartsWithSegments("/api/v1/recording-sessions"))
    {
        var agentIdText = context.Request.Headers["X-Agent-Id"].ToString();
        var authorization = context.Request.Headers.Authorization.ToString();
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : "";
        if (!Guid.TryParse(agentIdText, out var agentId) || string.IsNullOrWhiteSpace(token) || await unified.AuthenticateAgentAsync(agentId, token) is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "agent_authentication_required" });
            return;
        }
        context.Items["agent_id"] = agentId;
        await next();
        return;
    }

    if (context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/ready") ||
        context.Request.Path.StartsWithSegments("/api/auth/login") ||
        context.Request.Path.StartsWithSegments("/api/uploads/complete") ||
        context.Request.Path.StartsWithSegments("/api/internal/tusd/hooks") ||
        context.Request.Path.StartsWithSegments("/api/internal/imports"))
    {
        await next();
        return;
    }

    if (!context.Request.Cookies.TryGetValue("wa_session", out var session) ||
        !await db.IsSessionValidAsync(session))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "authentication_required" });
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "whisperx-atom-api" }));
app.MapGet("/ready", async () =>
{
    try
    {
        await db.PingAsync();
        return Results.Ok(new { ready = true, postgres = true });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Readiness check failed");
        return Results.Json(new { ready = false, postgres = false }, statusCode: 503);
    }
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, IConfiguration configuration) =>
{
    var user = await db.FindUserAsync(request.Username);
    if (user is null || !PasswordService.Verify(request.Password, user.PasswordHash))
        return Results.Unauthorized();

    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    await db.CreateSessionAsync(user.Id, token);
    http.Response.Cookies.Append("wa_session", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = !string.Equals(configuration["COOKIE_SECURE"], "false", StringComparison.OrdinalIgnoreCase),
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = TimeSpan.FromHours(12),
    });
    return Results.Ok(new { user = new { id = user.Id, username = user.Username, role = user.Role } });
});

app.MapPost("/api/auth/logout", async (HttpContext http) =>
{
    if (http.Request.Cookies.TryGetValue("wa_session", out var token))
        await db.RevokeSessionAsync(token);
    http.Response.Cookies.Delete("wa_session");
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/auth/me", async (HttpContext http) =>
{
    var user = await db.GetUserForSessionAsync(http.Request.Cookies["wa_session"]!);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(new { id = user.Id, username = user.Username, role = user.Role });
});

app.MapPost("/api/meetings", async (MeetingCreateRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "title_required" });

    var meeting = await db.CreateMeetingAsync(request.Title.Trim(), request.Description);
    return Results.Created("/api/meetings/" + meeting.Id, meeting);
});

app.MapGet("/api/meetings", async (int? limit, int? offset) =>
    Results.Ok(await db.ListMeetingsAsync(Math.Clamp(limit ?? 50, 1, 200), Math.Max(offset ?? 0, 0))));

app.MapGet("/api/meetings/{id:guid}", async (Guid id) =>
{
    var meeting = await db.GetMeetingAsync(id);
    return meeting is null ? Results.NotFound() : Results.Ok(meeting);
});

app.MapPost("/api/meetings/{id:guid}/uploads", async (Guid id, UploadReservationRequest request) =>
{
    if (!await db.MeetingExistsAsync(id))
        return Results.NotFound();

    var uploadId = Guid.NewGuid();
    await db.CreateUploadReservationAsync(id, uploadId, request.FileName, request.SizeBytes);
    return Results.Ok(new
    {
        uploadId,
        uploadUrl = "/files/" + uploadId,
        tusVersion = "1.0.0",
        maxSizeBytes = 8L * 1024 * 1024 * 1024,
    });
});

app.MapPost("/api/uploads/complete", async (HttpRequest request, UploadCompleteRequest payload, IConfiguration configuration) =>
{
    var expectedSecret = configuration["TUS_HOOK_SECRET"];
    if (!string.IsNullOrEmpty(expectedSecret) &&
        request.Headers["X-Tus-Hook-Secret"] != expectedSecret)
        return Results.Unauthorized();

    var job = await db.CompleteUploadAsync(payload);
    return job is null ? Results.NotFound() : Results.Accepted("/api/jobs/" + job.Id, job);
});

app.MapPost("/api/internal/tusd/hooks", async (JsonElement payload, HttpRequest request, IConfiguration configuration) =>
{
    if (!SecretMatches(request, configuration["TUS_HOOK_SECRET"]))
        return Results.Unauthorized();
    var eventType = JsonValue(payload, "Type") ?? JsonValue(payload, "Event", "Type") ?? JsonValue(payload, "Event", "TypeName");
    if (!string.Equals(eventType, "post-finish", StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new { ignored = true, eventType });
    var upload = JsonObject(payload, "Upload") ?? payload;
    var uploadIdText = JsonValue(upload, "ID") ?? JsonValue(upload, "Id");
    var metadata = JsonObject(upload, "MetaData") ?? JsonObject(upload, "Metadata");
    var reservationText = metadata.HasValue ? (JsonValue(metadata.Value, "reservationId") ?? JsonValue(metadata.Value, "reservation_id")) : null;
    if (!Guid.TryParse(reservationText ?? uploadIdText, out var reservationId))
        return Results.BadRequest(new { error = "reservation_id_required" });
    var uploadId = uploadIdText ?? reservationId.ToString();
    var originalName = metadata.HasValue ? (JsonValue(metadata.Value, "filename") ?? JsonValue(metadata.Value, "name")) : null;
    originalName ??= "upload-" + uploadId;
    var size = JsonLong(upload, "Size") ?? JsonLong(upload, "size") ?? 0;
    if (!MediaPolicy.IsAllowedExtension(originalName))
        return Results.BadRequest(new { error = "unsupported_audio_format" });
    if (size <= 0 || size > 8L * 1024 * 1024 * 1024)
        return Results.BadRequest(new { error = "media_size_limit" });
    var storageKey = "/data/uploads/" + uploadId;
    var uploadedPath = StorageHelpers.StoragePath(storageKey);
    if (!File.Exists(uploadedPath))
        return Results.Conflict(new { error = "upload_file_not_ready" });
    var actualSize = new FileInfo(uploadedPath).Length;
    if (size > 0 && actualSize != size)
        return Results.BadRequest(new { error = "upload_size_mismatch" });
    var sha256 = await StorageHelpers.ComputeSha256Async(uploadedPath);
    var job = await db.CompleteUploadAsync(new UploadCompleteRequest(reservationId, storageKey, sha256, actualSize, 0));
    return job is null ? Results.NotFound(new { error = "upload_reservation_not_found" }) : Results.Accepted("/api/jobs/" + job.Id, job);
});

app.MapPost("/api/internal/imports", async (ImportRequest request, HttpRequest http, IConfiguration configuration) =>
{
    var expected = configuration["IMPORT_WORKER_TOKEN"];
    var supplied = http.Headers["X-Import-Worker-Token"].ToString();
    if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
        return Results.Unauthorized();
    if (!MediaPolicy.IsAllowedExtension(request.OriginalName) || string.IsNullOrWhiteSpace(request.Sha256) || request.SizeBytes <= 0 || request.SizeBytes > 8L * 1024 * 1024 * 1024)
        return Results.BadRequest(new { error = "unsupported_or_oversized_audio" });
    var job = await db.RegisterImportAsync(request);
    return Results.Accepted("/api/jobs/" + job.Id, job);
});

static bool AgentMatches(HttpContext context, Guid agentId) => context.Items.TryGetValue("agent_id", out var item) && item is Guid authenticated && authenticated == agentId;

static bool SecretMatches(HttpRequest request, string? expected)
{
    if (string.IsNullOrWhiteSpace(expected)) return false;
    var supplied = request.Query["secret"].ToString();
    if (string.IsNullOrWhiteSpace(supplied)) supplied = request.Headers["X-Tus-Hook-Secret"].ToString();
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
}

static JsonElement? JsonObject(JsonElement value, params string[] path)
{
    var current = value;
    foreach (var name in path)
    {
        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
    }
    return current;
}

static string? JsonValue(JsonElement value, params string[] path)
{
    var obj = JsonObject(value, path);
    return obj.HasValue && obj.Value.ValueKind == JsonValueKind.String ? obj.Value.GetString() : null;
}

static long? JsonLong(JsonElement value, params string[] path)
{
    var obj = JsonObject(value, path);
    if (!obj.HasValue) return null;
    if (obj.Value.ValueKind == JsonValueKind.Number && obj.Value.TryGetInt64(out var number)) return number;
    return obj.Value.ValueKind == JsonValueKind.String && long.TryParse(obj.Value.GetString(), out number) ? number : null;
}

app.MapPost("/api/v1/agents/enroll", async (AgentEnrollRequest request, HttpRequest http, UnifiedProductStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "agent_name_required" });
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var agent = await store.EnrollAgentAsync(request.Name, request.RoomId, token, request.Version ?? "0.1.0", request.Capabilities ?? JsonDocument.Parse("{}"));
    return Results.Ok(new { agentId = agent.Id, agent, token });
});

app.MapPost("/api/v1/agents/{agentId:guid}/heartbeat", async (Guid agentId, AgentHeartbeatRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!AgentMatches(context, agentId)) return Results.Unauthorized();
    return await store.HeartbeatAsync(agentId, request.Status ?? "ONLINE", request.Version ?? "0.1.0", request.Capabilities ?? JsonDocument.Parse("{}"))
        ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.MapGet("/api/v1/agents/{agentId:guid}/commands/events", async (Guid agentId, long? after, HttpContext context, HttpResponse response, UnifiedProductStore store, CancellationToken cancellationToken) =>
{
    if (!AgentMatches(context, agentId)) { response.StatusCode = 401; return; }
    response.Headers.ContentType = "text/event-stream"; response.Headers.CacheControl = "no-cache";
    var cursor = after ?? 0;
    for (var i = 0; i < 60 && !cancellationToken.IsCancellationRequested; i++)
    {
        var commands = await store.PendingCommandsAsync(agentId, cursor);
        foreach (var command in commands)
        {
            cursor = Math.Max(cursor, command.Cursor);
            await response.WriteAsync($"id: {command.Cursor}\nevent: command\ndata: {JsonSerializer.Serialize(command)}\n\n", cancellationToken);
        }
        await response.Body.FlushAsync(cancellationToken);
        if (commands.Count > 0) return;
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }
});

app.MapPost("/api/v1/agents/{agentId:guid}/commands/{commandId:guid}/result", async (Guid agentId, Guid commandId, AgentCommandResultRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!AgentMatches(context, agentId)) return Results.Unauthorized();
    return await store.CompleteCommandAsync(commandId, request.Status ?? "COMPLETED", request.Result ?? JsonDocument.Parse("{}")) ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.MapGet("/api/agents", async (UnifiedProductStore store) => Results.Ok(await store.ListAgentsAsync()));

app.MapPost("/api/v1/recording-sessions", async (CreateRecordingSessionRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var session = await store.CreateRecordingSessionAsync(request.MeetingId, agentId);
    return session is null ? Results.NotFound() : Results.Created($"/api/v1/recording-sessions/{session.Id}", session);
});

app.MapPost("/api/v1/recording-sessions/{sessionId:guid}/tracks", async (Guid sessionId, CreateTrackRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.ContainsKey("agent_id")) return Results.Unauthorized();
    var track = await store.CreateRecordingTrackAsync(sessionId, request.TrackType, request.DeviceId, request.SampleRate, request.Channels);
    return track is null ? Results.NotFound() : Results.Created($"/api/v1/recording-sessions/{sessionId}/tracks/{track.Id}", track);
});

app.MapPut("/api/v1/recording-sessions/{sessionId:guid}/tracks/{trackId:guid}/chunks/{sequence:int}", async (Guid sessionId, Guid trackId, int sequence, HttpRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.ContainsKey("agent_id")) return Results.Unauthorized();
    var key = $"/data/recordings/{sessionId:N}/{trackId:N}/{sequence:D8}.flac";
    var path = StorageHelpers.StoragePath(key);
    var partPath = path + ".part";
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    try
    {
        await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await request.Body.CopyToAsync(output);
        var size = new FileInfo(partPath).Length;
        var sha = await StorageHelpers.ComputeSha256Async(partPath);
        var expectedSha = request.Headers["X-Chunk-SHA256"].ToString();
        if (!string.IsNullOrWhiteSpace(expectedSha) && !string.Equals(expectedSha, sha, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            return Results.BadRequest(new { error = "chunk_checksum_mismatch" });
        }
        if (File.Exists(path))
        {
            var existingSha = await StorageHelpers.ComputeSha256Async(path);
            File.Delete(partPath);
            return string.Equals(existingSha, sha, StringComparison.OrdinalIgnoreCase)
                ? Results.Ok(new { sequence, storageKey = key, sizeBytes = new FileInfo(path).Length, sha256 = existingSha, idempotent = true })
                : Results.Conflict(new { error = "chunk_sequence_hash_conflict" });
        }
        var startSample = long.TryParse(request.Headers["X-Start-Sample"], out var parsedStart) ? parsedStart : 0;
        var sampleCount = long.TryParse(request.Headers["X-Sample-Count"], out var parsedCount) ? parsedCount : 0;
        var stored = await store.RegisterChunkAsync(sessionId, trackId, sequence, key, startSample, sampleCount, size, sha);
        if (!stored)
        {
            File.Delete(partPath);
            return Results.Conflict(new { error = "chunk_sequence_hash_conflict" });
        }
        File.Move(partPath, path, true);
        return Results.Ok(new { sequence, storageKey = key, sizeBytes = size, sha256 = sha });
    }
    catch
    {
        if (File.Exists(partPath)) File.Delete(partPath);
        throw;
    }
});

app.MapGet("/api/v1/recording-sessions/{sessionId:guid}/tracks/{trackId:guid}/missing-chunks", async (Guid sessionId, Guid trackId, int expectedCount, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.ContainsKey("agent_id")) return Results.Unauthorized();
    return Results.Ok(new { missing = await store.MissingChunksAsync(sessionId, trackId, Math.Clamp(expectedCount, 0, 100000)) });
});

app.MapPost("/api/v1/recording-sessions/{sessionId:guid}/finalize", async (Guid sessionId, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.ContainsKey("agent_id")) return Results.Unauthorized();
    return await store.FinalizeRecordingAsync(sessionId) ? Results.Accepted() : Results.NotFound();
});

app.MapPost("/api/meetings/{id:guid}/recording-commands", async (Guid id, RecordingCommandRequest request, UnifiedProductStore store) =>
{
    if (request.AgentId == Guid.Empty) return Results.BadRequest(new { error = "agent_id_required" });
    if (!await db.MeetingExistsAsync(id)) return Results.NotFound();
    var commandId = await store.CreateCommandAsync(request.AgentId, request.CommandType, request.Payload ?? JsonDocument.Parse("{}"));
    return Results.Accepted($"/api/agents/{request.AgentId}", new { commandId });
});

app.MapGet("/api/meetings/{id:guid}/summary", async (Guid id, UnifiedProductStore store) => Results.Ok(await store.GetLatestSummaryAsync(id)));
app.MapPost("/api/meetings/{id:guid}/summary/rebuild", async (Guid id, UnifiedProductStore store) =>
{
    var jobId = await store.QueueSummaryAsync(id);
    return jobId is null ? Results.Conflict(new { error = "transcript_required" }) : Results.Accepted($"/api/jobs/{jobId}", new { jobId });
});
app.MapGet("/api/meetings/{id:guid}/decisions", async (Guid id, UnifiedProductStore store) => Results.Ok(await store.ListDecisionsAsync(id)));
app.MapGet("/api/meetings/{id:guid}/tasks", async (Guid id, UnifiedProductStore store) => Results.Ok(await store.ListActionItemsAsync(id)));
app.MapPatch("/api/tasks/{id:guid}", async (Guid id, UpdateTaskRequest request, UnifiedProductStore store) => await store.UpdateActionItemAsync(id, request.Task, request.Responsible, request.Deadline, request.Status) ? Results.Ok(new { ok = true }) : Results.NotFound());
app.MapPost("/api/assistant/queries", async (AssistantQueryRequest request, UnifiedProductStore store) => Results.Ok(await store.AnswerAssistantAsync(request.MeetingId, request.Query)));
app.MapGet("/api/meetings/{id:guid}/jobs", async (Guid id) => Results.Ok(await db.ListJobsAsync(id)));

app.MapGet("/api/meetings/{id:guid}/media", async (Guid id) => Results.Ok(await db.ListMediaAsync(id)));

app.MapGet("/api/meetings/{id:guid}/speakers", async (Guid id) => Results.Ok(await db.ListSpeakersAsync(id)));

app.MapGet("/api/media/{id:guid}/preview", async (Guid id) =>
{
    var media = await db.GetMediaAsync(id);
    if (media is null || string.IsNullOrWhiteSpace(media.PreviewStorageKey)) return Results.NotFound();
    var path = StorageHelpers.StoragePath(media.PreviewStorageKey);
    if (!File.Exists(path)) return Results.NotFound();
    return Results.File(File.OpenRead(path), "audio/ogg", enableRangeProcessing: true);
});

app.MapGet("/api/jobs/{id:guid}", async (Guid id) =>
{
    var job = await db.GetJobAsync(id);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapPost("/api/jobs/{id:guid}/retry", async (Guid id) =>
{
    var job = await db.RetryJobAsync(id);
    return job is null ? Results.NotFound() : Results.Accepted("/api/jobs/" + id, job);
});

app.MapGet("/api/jobs/{id:guid}/events", async (Guid id, HttpResponse response, CancellationToken cancellationToken) =>
{
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    for (var i = 0; i < 120 && !cancellationToken.IsCancellationRequested; i++)
    {
        var job = await db.GetJobAsync(id);
        if (job is null)
        {
            response.StatusCode = 404;
            return;
        }

        await response.WriteAsync("event: progress\ndata: " + JsonSerializer.Serialize(job) + "\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        if (job.Status is "READY" or "FAILED")
            return;
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
});

app.MapGet("/api/meetings/{id:guid}/transcript", async (Guid id) =>
    Results.Ok(await db.GetTranscriptAsync(id)));

app.MapPatch("/api/meetings/{meetingId:guid}/speakers/{speakerId:guid}",
    async (Guid meetingId, Guid speakerId, SpeakerRenameRequest request) =>
{
    var updated = await db.RenameSpeakerAsync(meetingId, speakerId, request.DisplayName);
    return updated ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.MapPost("/api/meetings/{meetingId:guid}/speakers/merge",
    async (Guid meetingId, SpeakerMergeRequest request) =>
{
    var merged = await db.MergeSpeakersAsync(meetingId, request.SourceSpeakerId, request.TargetSpeakerId);
    return merged ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.Run();

public record AgentEnrollRequest(string Name, Guid? RoomId, string? Version, JsonDocument? Capabilities);
public record AgentHeartbeatRequest(string? Status, string? Version, JsonDocument? Capabilities);
public record AgentCommandResultRequest(string? Status, JsonDocument? Result);
public record CreateRecordingSessionRequest(Guid MeetingId);
public record CreateTrackRequest(string TrackType, string? DeviceId, int SampleRate = 48000, int Channels = 1);
public record RecordingCommandRequest(Guid AgentId, string CommandType, JsonDocument? Payload);
public record UpdateTaskRequest(string Task, string? Responsible, DateTime? Deadline, string Status);
public record AssistantQueryRequest(string Query, Guid? MeetingId);
public record LoginRequest(string Username, string Password);
public record MeetingCreateRequest(string Title, string? Description);
public record UploadReservationRequest(string FileName, long SizeBytes);
public record UploadCompleteRequest(Guid UploadId, string StorageKey, string Sha256, long SizeBytes, long DurationMs);
public record ImportRequest(
    [property: JsonPropertyName("original_name")] string OriginalName,
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("storage_key")] string StorageKey,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);
public sealed record MediaAssetRow(Guid Id, Guid MeetingId, string OriginalName, string? StorageKey, string? Sha256, long SizeBytes, long? DurationMs, string Status, string? ArchiveStorageKey, string? PreviewStorageKey, string? AsrStorageKey);
public sealed record SpeakerRow(Guid Id, string StableKey, string DisplayName);
public record SpeakerRenameRequest(string DisplayName);
public record SpeakerMergeRequest(Guid SourceSpeakerId, Guid TargetSpeakerId);

public sealed record UserRow(Guid Id, string Username, string PasswordHash, string Role);
public sealed record MeetingRow(Guid Id, string Title, string? Description, string Status, DateTime CreatedAt);
public sealed record JobRow(Guid Id, Guid MeetingId, string Type, string Status, string Stage, int Progress, int Attempt, string? Error);
public sealed record TranscriptSegmentRow(Guid Id, int Ordinal, long StartMs, long EndMs, string? Speaker, string Text, double? Confidence, JsonDocument? Words);
public sealed record TranscriptRow(Guid Id, Guid MeetingId, string Status, IReadOnlyList<TranscriptSegmentRow> Segments);

public static class StorageHelpers
{
    public static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string StoragePath(string storageKey)
    {
        var normalized = storageKey.Replace("\\", "/");
        if (normalized.StartsWith("/data/", StringComparison.Ordinal)) return normalized;
        if (normalized.StartsWith("data/", StringComparison.Ordinal)) return "/" + normalized;
        throw new InvalidOperationException("invalid_storage_key");
    }
}

public static class MediaPolicy
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".mp4", ".mkv", ".mov", ".webm", ".avi" };
    public static bool IsAllowedExtension(string name) => Extensions.Contains(Path.GetExtension(name));
}
public sealed class PasswordService
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return "pbkdf2-sha256$120000$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256")
                return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(parts[1]), HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class Database(IConfiguration configuration)
{
    private readonly string _connectionString =
        configuration.GetConnectionString("Postgres") ??
        configuration["POSTGRES_CONNECTION"] ??
        "Host=localhost;Port=5432;Database=whisperx_atom;Username=whisperx;Password=whisperx";

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyMigrationsAsync(connection);

        var username = configuration["BOOTSTRAP_ADMIN_USERNAME"] ?? "admin";
        var password = configuration["BOOTSTRAP_ADMIN_PASSWORD"];
        if (!string.IsNullOrWhiteSpace(password))
        {
            await using var check = new NpgsqlCommand("SELECT COUNT(*) FROM users WHERE username=@username", connection);
            check.Parameters.AddWithValue("username", username);
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) == 0)
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO users(id, username, password_hash, role) VALUES(@id,@username,@hash,'Administrator')", connection);
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("username", username);
                insert.Parameters.AddWithValue("hash", PasswordService.Hash(password));
                await insert.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task ApplyMigrationsAsync(NpgsqlConnection connection)
    {
        await using (var table = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_migrations(version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
            await table.ExecuteNonQueryAsync();
        var directory = Path.Combine(AppContext.BaseDirectory, "Migrations");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Migration directory not found: {directory}");
        foreach (var file in Directory.GetFiles(directory, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var version = Path.GetFileNameWithoutExtension(file);
            await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=@version)", connection);
            check.Parameters.AddWithValue("version", version);
            if ((bool)(await check.ExecuteScalarAsync())!)
                continue;
            var sql = await File.ReadAllTextAsync(file);
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var migration = new NpgsqlCommand(sql, connection, transaction))
                await migration.ExecuteNonQueryAsync();
            await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES(@version)", connection, transaction))
            {
                record.Parameters.AddWithValue("version", version);
                await record.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
    }

    public async Task PingAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync();
    }

    public async Task<UserRow?> FindUserAsync(string username)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, username, password_hash, role FROM users WHERE username=@username AND is_active", connection);
        command.Parameters.AddWithValue("username", username);
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null :
            new UserRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    public async Task<UserRow?> GetUserForSessionAsync(string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT u.id, u.username, u.password_hash, u.role FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@hash AND s.expires_at > now() AND s.revoked_at IS NULL", connection);
        command.Parameters.AddWithValue("hash", SessionHash(token));
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null :
            new UserRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    public async Task<bool> IsSessionValidAsync(string token) => await GetUserForSessionAsync(token) is not null;

    public async Task CreateSessionAsync(Guid userId, string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO sessions(id,user_id,token_hash,expires_at) VALUES(@id,@uid,@hash,now()+interval '12 hours')", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("uid", userId);
        command.Parameters.AddWithValue("hash", SessionHash(token));
        await command.ExecuteNonQueryAsync();
    }

    public async Task RevokeSessionAsync(string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE sessions SET revoked_at=now() WHERE token_hash=@hash", connection);
        command.Parameters.AddWithValue("hash", SessionHash(token));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<MeetingRow> CreateMeetingAsync(string title, string? description)
    {
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO meetings(id,title,description,status) VALUES(@id,@title,@description,'CREATED') RETURNING created_at", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        var created = (DateTime)(await command.ExecuteScalarAsync())!;
        return new MeetingRow(id, title, description, "CREATED", created);
    }

    public async Task<bool> MeetingExistsAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM meetings WHERE id=@id)", connection);
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<MeetingRow>> ListMeetingsAsync(int limit, int offset)
    {
        var result = new List<MeetingRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id,title,description,status,created_at FROM meetings ORDER BY created_at DESC LIMIT @limit OFFSET @offset", connection);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new MeetingRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetDateTime(4)));
        return result;
    }

    public async Task<MeetingRow?> GetMeetingAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,title,description,status,created_at FROM meetings WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null :
            new MeetingRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
    }

    public async Task CreateUploadReservationAsync(Guid meetingId, Guid uploadId, string fileName, long sizeBytes)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO media_assets(id,meeting_id,upload_id,original_name,size_bytes,status) VALUES(@id,@meeting,@upload,@name,@size,'UPLOADING')", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("meeting", meetingId);
        command.Parameters.AddWithValue("upload", uploadId);
        command.Parameters.AddWithValue("name", fileName);
        command.Parameters.AddWithValue("size", sizeBytes);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<JobRow?> CompleteUploadAsync(UploadCompleteRequest payload)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var normalizedStorageKey = payload.StorageKey;
        if (normalizedStorageKey.StartsWith("tusd:", StringComparison.OrdinalIgnoreCase))
        {
            var tusId = normalizedStorageKey[(normalizedStorageKey.LastIndexOf('/') + 1)..];
            if (string.IsNullOrWhiteSpace(tusId))
                return null;
            normalizedStorageKey = "/data/uploads/" + tusId;
        }

        await using var asset = new NpgsqlCommand(
            "UPDATE media_assets SET storage_key=@key, sha256=@sha, size_bytes=@size, duration_ms=@duration, status='UPLOADED' WHERE upload_id=@upload AND status='UPLOADING' RETURNING id,meeting_id", connection, tx);
        asset.Parameters.AddWithValue("key", normalizedStorageKey);
        asset.Parameters.AddWithValue("sha", payload.Sha256);
        asset.Parameters.AddWithValue("size", payload.SizeBytes);
        asset.Parameters.AddWithValue("duration", payload.DurationMs);
        asset.Parameters.AddWithValue("upload", payload.UploadId);
        await using var reader = await asset.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            await reader.CloseAsync();
            await using var existing = new NpgsqlCommand(
                "SELECT id,meeting_id,type,status,stage,progress,attempt,error_message FROM jobs WHERE media_asset_id=(SELECT id FROM media_assets WHERE upload_id=@upload) ORDER BY created_at DESC LIMIT 1",
                connection, tx);
            existing.Parameters.AddWithValue("upload", payload.UploadId);
            await using var existingReader = await existing.ExecuteReaderAsync();
            if (!await existingReader.ReadAsync())
                return null;
            var existingJob = ReadJob(existingReader);
            await existingReader.CloseAsync();
            await tx.CommitAsync();
            return existingJob;
        }

        var mediaId = reader.GetGuid(0);
        var meetingId = reader.GetGuid(1);
        await reader.CloseAsync();

        var jobId = Guid.NewGuid();
        await using var job = new NpgsqlCommand(
            "INSERT INTO jobs(id,meeting_id,media_asset_id,type,status,stage) VALUES(@id,@meeting,@asset,'TRANSCRIBE','QUEUED','UPLOADED')", connection, tx);
        job.Parameters.AddWithValue("id", jobId);
        job.Parameters.AddWithValue("meeting", meetingId);
        job.Parameters.AddWithValue("asset", mediaId);
        await job.ExecuteNonQueryAsync();

        var envelope = JsonSerializer.Serialize(new
        {
            message_id = Guid.NewGuid(),
            job_id = jobId,
            meeting_id = meetingId,
            media_asset_id = mediaId,
            stage = "UPLOADED",
            attempt = 0,
            storage_key = normalizedStorageKey,
        });
        await using var outbox = new NpgsqlCommand(
            "INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'media.ingest',@payload::jsonb)", connection, tx);
        outbox.Parameters.AddWithValue("id", Guid.NewGuid());
        outbox.Parameters.AddWithValue("payload", envelope);
        await outbox.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return new JobRow(jobId, meetingId, "TRANSCRIBE", "QUEUED", "UPLOADED", 0, 0, null);
    }

    public async Task<JobRow> RegisterImportAsync(ImportRequest request)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var duplicate = new NpgsqlCommand("SELECT j.id,j.meeting_id,j.type,j.status,j.stage,j.progress,j.attempt,j.error_message FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE a.sha256=@sha ORDER BY j.created_at DESC LIMIT 1", connection, tx);
        duplicate.Parameters.AddWithValue("sha", request.Sha256);
        await using var duplicateReader = await duplicate.ExecuteReaderAsync();
        if (await duplicateReader.ReadAsync()) { var existing = ReadJob(duplicateReader); await duplicateReader.CloseAsync(); await tx.CommitAsync(); return existing; }
        await duplicateReader.CloseAsync();
        var meetingId = Guid.NewGuid();
        var title = Path.GetFileNameWithoutExtension(request.OriginalName);
        await using var meeting = new NpgsqlCommand("INSERT INTO meetings(id,title,status) VALUES(@id,@title,'CREATED')", connection, tx);
        meeting.Parameters.AddWithValue("id", meetingId); meeting.Parameters.AddWithValue("title", string.IsNullOrWhiteSpace(title) ? "Р‘РµР·С‹РјСЏРЅРЅР°СЏ Р·Р°РїРёСЃСЊ" : title);
        await meeting.ExecuteNonQueryAsync();
        var assetId = Guid.NewGuid();
        await using var asset = new NpgsqlCommand("INSERT INTO media_assets(id,meeting_id,original_name,storage_key,sha256,size_bytes,status,source_type) VALUES(@id,@meeting,@name,@key,@sha,@size,'UPLOADED',@source)", connection, tx);
        asset.Parameters.AddWithValue("id", assetId); asset.Parameters.AddWithValue("meeting", meetingId); asset.Parameters.AddWithValue("name", request.OriginalName); asset.Parameters.AddWithValue("key", request.StorageKey); asset.Parameters.AddWithValue("sha", request.Sha256); asset.Parameters.AddWithValue("size", request.SizeBytes); asset.Parameters.AddWithValue("source", request.SourceType);
        await asset.ExecuteNonQueryAsync();
        var jobId = Guid.NewGuid();
        await using var job = new NpgsqlCommand("INSERT INTO jobs(id,meeting_id,media_asset_id,type,status,stage) VALUES(@id,@meeting,@asset,'TRANSCRIBE','QUEUED','UPLOADED') RETURNING id,meeting_id,type,status,stage,progress,attempt,error_message", connection, tx);
        job.Parameters.AddWithValue("id", jobId); job.Parameters.AddWithValue("meeting", meetingId); job.Parameters.AddWithValue("asset", assetId);
        await using var jobReader = await job.ExecuteReaderAsync(); await jobReader.ReadAsync(); var result = ReadJob(jobReader); await jobReader.CloseAsync();
        var envelope = JsonSerializer.Serialize(new { message_id = Guid.NewGuid(), job_id = result.Id, meeting_id = meetingId, media_asset_id = assetId, stage = "UPLOADED", attempt = 0, storage_key = request.StorageKey, source_type = request.SourceType });
        await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'media.ingest',@payload::jsonb)", connection, tx); outbox.Parameters.AddWithValue("id", Guid.NewGuid()); outbox.Parameters.AddWithValue("payload", envelope); await outbox.ExecuteNonQueryAsync();
        await tx.CommitAsync(); return result;
    }

    public async Task<MediaAssetRow?> GetMediaAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,original_name,storage_key,sha256,size_bytes,duration_ms,status,archive_storage_key,preview_storage_key,asr_storage_key FROM media_assets WHERE id=@id", connection); command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null : ReadMedia(reader);
    }

    public async Task<IReadOnlyList<SpeakerRow>> ListSpeakersAsync(Guid meetingId)
    {
        var result = new List<SpeakerRow>(); await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,stable_key,display_name FROM meeting_speakers WHERE meeting_id=@id ORDER BY stable_key", connection); command.Parameters.AddWithValue("id", meetingId);
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new SpeakerRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))); return result;
    }

    public async Task<IReadOnlyList<MediaAssetRow>> ListMediaAsync(Guid meetingId)
    {
        var result = new List<MediaAssetRow>(); await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,original_name,storage_key,sha256,size_bytes,duration_ms,status,archive_storage_key,preview_storage_key,asr_storage_key FROM media_assets WHERE meeting_id=@id ORDER BY created_at", connection); command.Parameters.AddWithValue("id", meetingId);
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(ReadMedia(reader)); return result;
    }

    public async Task<JobRow?> GetJobAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,type,status,stage,progress,attempt,error_message FROM jobs WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null : ReadJob(reader);
    }

    public async Task<IReadOnlyList<JobRow>> ListJobsAsync(Guid meetingId)
    {
        var result = new List<JobRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,type,status,stage,progress,attempt,error_message FROM jobs WHERE meeting_id=@id ORDER BY created_at DESC", connection);
        command.Parameters.AddWithValue("id", meetingId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadJob(reader));
        return result;
    }

    public async Task<JobRow?> RetryJobAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE jobs SET status='QUEUED',stage='UPLOADED',progress=0,error_message=NULL,error_code=NULL,worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,attempt=attempt+1,updated_at=now() WHERE id=@id RETURNING id,meeting_id,type,status,stage,progress,attempt,error_message", connection, tx);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var job = ReadJob(reader);
        await reader.CloseAsync();
        await using var publish = new NpgsqlCommand(
            "INSERT INTO outbox_messages(id,topic,payload) SELECT @outbox,'media.ingest',jsonb_build_object('message_id',@message,'job_id',j.id,'meeting_id',j.meeting_id,'media_asset_id',j.media_asset_id,'stage',j.stage,'attempt',j.attempt,'storage_key',a.storage_key,'source_type',a.source_type) FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE j.id=@id", connection, tx);
        publish.Parameters.AddWithValue("outbox", Guid.NewGuid());
        publish.Parameters.AddWithValue("message", Guid.NewGuid());
        publish.Parameters.AddWithValue("id", id);
        await publish.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return job;
    }

    public async Task<TranscriptRow> GetTranscriptAsync(Guid meetingId)
    {
        var segments = new List<TranscriptSegmentRow>();
        Guid transcriptId = Guid.Empty;
        string status = "PENDING";
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT t.id,t.status,s.id,s.ordinal,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label),s.text,s.confidence,s.words FROM transcripts t LEFT JOIN transcript_segments s ON s.transcript_id=t.id LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id WHERE t.meeting_id=@meeting AND t.version=(SELECT MAX(version) FROM transcripts WHERE meeting_id=@meeting) ORDER BY s.ordinal", connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            transcriptId = reader.GetGuid(0);
            status = reader.GetString(1);
            if (!reader.IsDBNull(2))
                segments.Add(new TranscriptSegmentRow(reader.GetGuid(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetDouble(8), reader.IsDBNull(9) ? null : reader.GetFieldValue<JsonDocument>(9)));
        }
        return new TranscriptRow(transcriptId, meetingId, status, segments);
    }

    public async Task<bool> RenameSpeakerAsync(Guid meetingId, Guid speakerId, string displayName)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE meeting_speakers SET display_name=@name WHERE id=@speaker AND meeting_id=@meeting", connection);
        command.Parameters.AddWithValue("name", displayName.Trim());
        command.Parameters.AddWithValue("speaker", speakerId);
        command.Parameters.AddWithValue("meeting", meetingId);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> MergeSpeakersAsync(Guid meetingId, Guid source, Guid target)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE transcript_segments SET speaker_id=@target WHERE speaker_id=@source AND transcript_id IN (SELECT id FROM transcripts WHERE meeting_id=@meeting)", connection, tx);
        update.Parameters.AddWithValue("target", target);
        update.Parameters.AddWithValue("source", source);
        update.Parameters.AddWithValue("meeting", meetingId);
        var count = await update.ExecuteNonQueryAsync();
        await using var remove = new NpgsqlCommand("DELETE FROM meeting_speakers WHERE id=@source AND meeting_id=@meeting", connection, tx);
        remove.Parameters.AddWithValue("source", source);
        remove.Parameters.AddWithValue("meeting", meetingId);
        await remove.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return count > 0;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static JobRow ReadJob(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7));

    private static MediaAssetRow ReadMedia(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10));

    private static string SessionHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));


}

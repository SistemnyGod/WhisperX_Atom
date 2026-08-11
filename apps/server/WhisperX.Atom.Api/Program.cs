using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<UnifiedProductStore>();
if (builder.Environment.IsProduction())
{
    static bool IsUnsafeSecret(string? value) => string.IsNullOrWhiteSpace(value)
        || value.StartsWith("generate-", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("replace-with", StringComparison.OrdinalIgnoreCase)
        || value.Equals("password", StringComparison.OrdinalIgnoreCase)
        || value.Equals("changeme", StringComparison.OrdinalIgnoreCase);
    if (string.Equals(builder.Configuration["COOKIE_SECURE"], "false", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("PRODUCTION_COOKIE_SECURE_REQUIRED");
    foreach (var name in new[] { "POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN", "AGENT_ENROLLMENT_SECRET" })
        if (IsUnsafeSecret(builder.Configuration[name])) throw new InvalidOperationException($"PRODUCTION_SECRET_INVALID:{name}");
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var route = path.StartsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase) ? "login" :
            path.StartsWith("/api/auth/refresh", StringComparison.OrdinalIgnoreCase) ? "refresh" :
            path.StartsWith("/api/v1/agents/enroll", StringComparison.OrdinalIgnoreCase) ? "enrollment" :
            path.StartsWith("/api/meetings/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/summary/rebuild", StringComparison.OrdinalIgnoreCase) ? "summary-rebuild" :
            path.StartsWith("/api/assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : null;
        if (route is null) return RateLimitPartition.GetNoLimiter("unlimited");
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var limit = route is "login" or "refresh" or "enrollment" ? 20 : 30;
        return RateLimitPartition.GetFixedWindowLimiter($"{route}:{ip}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});
var allowedOrigins = (builder.Configuration["CORS_ALLOWED_ORIGINS"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();
app.UseCors();
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    var suppliedTraceId = context.Request.Headers["X-Trace-Id"].ToString();
    var traceId = suppliedTraceId.Length is > 0 and <= 128 && suppliedTraceId.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
        ? suppliedTraceId
        : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    context.Response.Headers["X-Trace-Id"] = traceId;
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "http_request_failed trace_id={TraceId} method={Method} path={Path}", traceId, context.Request.Method, context.Request.Path.Value);
        throw;
    }
    finally
    {
        app.Logger.LogInformation(
            "http_request trace_id={TraceId} method={Method} path={Path} status_code={StatusCode} elapsed_ms={ElapsedMs}",
            traceId,
            context.Request.Method,
            context.Request.Path.Value,
            context.Response.StatusCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
});

var db = app.Services.GetRequiredService<Database>();
var unified = app.Services.GetRequiredService<UnifiedProductStore>();
await db.InitializeAsync();

async Task IssueAuthCookiesAsync(HttpContext http, IConfiguration configuration, UserRow user)
{
    var accessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    await db.CreateSessionAsync(user.Id, accessToken);
    await db.CreateRefreshSessionAsync(user.Id, refreshToken, Guid.NewGuid());
    AppendAuthCookies(http, configuration, accessToken, refreshToken);
}

void AppendAuthCookies(HttpContext http, IConfiguration configuration, string accessToken, string refreshToken)
{
    var secure = !string.Equals(configuration["COOKIE_SECURE"], "false", StringComparison.OrdinalIgnoreCase);
    http.Response.Cookies.Append("wa_session", accessToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = TimeSpan.FromHours(12),
    });
    http.Response.Cookies.Append("wa_refresh", refreshToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = TimeSpan.FromDays(30),
    });
}

void DeleteAuthCookies(HttpContext http)
{
    http.Response.Cookies.Delete("wa_session");
    http.Response.Cookies.Delete("wa_refresh");
}

async Task<int> QueueRecorderCancellationAsync(UnifiedProductStore store, IReadOnlyList<AgentSessionCancellationTarget> sessions, bool discardTransport)
{
    var queued = 0;
    foreach (var session in sessions.Distinct())
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            serverSessionId = session.ServerSessionId,
            discardTransport,
        }));
        await store.CreateCommandAsync(session.AgentId, "CANCEL_SERVER_SESSION", payload);
        queued++;
    }
    return queued;
}

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

    if (context.Request.Path.StartsWithSegments("/api/assistant/queries"))
    {
        var expectedVoiceToken = builder.Configuration["VOICE_HOST_TOKEN"] ?? string.Empty;
        var suppliedVoiceToken = context.Request.Headers["X-Voice-Host-Token"].ToString();
        // The API is published on host loopback in the desktop deployment.
        // Docker Desktop forwards that request through its bridge network, so
        // the container cannot reliably observe the original loopback address.
        if (!string.IsNullOrWhiteSpace(expectedVoiceToken) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedVoiceToken), Encoding.UTF8.GetBytes(suppliedVoiceToken)))
        {
            context.Items["voice_host"] = true;
            await next();
            return;
        }
        // Fall through to the regular cookie session path for Desktop UI users.

    }

    if (context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/ready") ||
        context.Request.Path.StartsWithSegments("/api/auth/login") ||
        context.Request.Path.StartsWithSegments("/api/auth/refresh") ||
        context.Request.Path.StartsWithSegments("/api/auth/logout") ||
        context.Request.Path.StartsWithSegments("/api/uploads/complete") ||
        context.Request.Path.StartsWithSegments("/api/internal/tusd/hooks") ||
        context.Request.Path.StartsWithSegments("/api/internal/imports"))
    {
        await next();
        return;
    }

    if (!context.Request.Cookies.TryGetValue("wa_session", out var session))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "authentication_required" });
        return;
    }

    var user = await db.GetUserForSessionAsync(session);
    if (user is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; await context.Response.WriteAsJsonAsync(new { error = "authentication_required" }); return; }
    if (!RoleAllows(user.Role, context.Request.Method, context.Request.Path)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; await context.Response.WriteAsJsonAsync(new { error = "insufficient_role" }); return; }
    context.Items["user_id"] = user.Id;
    context.Items["user_role"] = user.Role;

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

app.MapGet("/api/system/readiness", async (UnifiedProductStore store, IConfiguration configuration) =>
{
    var checkedAt = DateTimeOffset.UtcNow;
    var postgres = true;
    try { await db.PingAsync(); }
    catch (Exception ex)
    {
        postgres = false;
        app.Logger.LogWarning(ex, "System readiness PostgreSQL check failed");
    }

    var nats = false;
    var natsUrl = configuration["NATS_MONITORING_URL"] ?? "http://nats:8222/healthz";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await http.GetAsync(natsUrl);
        nats = response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        app.Logger.LogDebug(ex, "System readiness NATS check failed");
    }

    var workers = postgres ? await store.ListWorkerRuntimeAsync() : Array.Empty<WorkerRuntimeRow>();
    var fresh = workers
        .GroupBy(item => item.WorkerName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastSeenAt).First(), StringComparer.OrdinalIgnoreCase);
    var workerReady = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in new[] { "outbox-relay", "import-worker", "media-worker", "gpu-worker" })
    {
        if (!fresh.TryGetValue(name, out var item) || checkedAt.UtcDateTime - item.LastSeenAt.ToUniversalTime() > TimeSpan.FromSeconds(60))
        {
            workerReady[name] = new { status = "UNAVAILABLE", lastSeenAt = fresh.TryGetValue(name, out var stale) ? stale.LastSeenAt : (DateTime?)null };
            continue;
        }
        workerReady[name] = new { status = item.Status, lastSeenAt = item.LastSeenAt, version = item.Version, currentJobId = item.CurrentJobId, capabilities = item.Capabilities, lastErrorCode = item.LastErrorCode };
    }

    var gpu = fresh.TryGetValue("gpu-worker", out var gpuWorker) && checkedAt.UtcDateTime - gpuWorker.LastSeenAt.ToUniversalTime() <= TimeSpan.FromSeconds(60);
    var gpuCapabilities = gpuWorker?.Capabilities.RootElement;
    var cuda = gpu && gpuCapabilities.HasValue && gpuCapabilities.Value.TryGetProperty("cudaAvailable", out var cudaValue) && cudaValue.ValueKind == JsonValueKind.True;
    var hf = gpu && gpuCapabilities.HasValue && gpuCapabilities.Value.TryGetProperty("diarization", out var diarizationValue)
        ? diarizationValue.GetString() ?? "DEGRADED"
        : "DEGRADED";
    var requiredWorkersReady = new[] { "outbox-relay", "import-worker", "media-worker", "gpu-worker" }.All(name =>
        fresh.TryGetValue(name, out var worker) &&
        checkedAt.UtcDateTime - worker.LastSeenAt.ToUniversalTime() <= TimeSpan.FromSeconds(60) &&
        !string.Equals(worker.Status, "FAILED", StringComparison.OrdinalIgnoreCase));
    var qwenEnabled = string.Equals(configuration["AUTO_SUMMARY_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
    var ready = postgres && nats && requiredWorkersReady && cuda;

    return Results.Ok(new
    {
        ready,
        checkedAt,
        components = new
        {
            postgres = new { status = postgres ? "READY" : "UNAVAILABLE" },
            nats = new { status = nats ? "READY" : "UNAVAILABLE" },
            workers = workerReady,
            cuda = new { status = cuda ? "READY" : "UNAVAILABLE" },
            hfDiarization = new { status = hf },
            recorder = new { status = "OPTIONAL" },
            qwen = new { status = qwenEnabled ? "ENABLED" : "DISABLED" }
        }
    });
});

app.MapGet("/api/system/status", async () =>
{
    try
    {
        await db.PingAsync();
        var root = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "/data";
        var path = Path.GetPathRoot(Path.GetFullPath(root)) ?? Path.DirectorySeparatorChar.ToString();
        var drive = new DriveInfo(path);
        return Results.Ok(new { ready = true, postgres = true, freeBytes = drive.AvailableFreeSpace, totalBytes = drive.TotalSize, checkedAt = DateTimeOffset.UtcNow });
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "System status check failed");
        return Results.Ok(new { ready = false, postgres = false, freeBytes = 0L, totalBytes = 0L, checkedAt = DateTimeOffset.UtcNow });
    }
});

app.MapGet("/api/admin/operations", async (HttpContext context, UnifiedProductStore store) =>
{
    if (!IsPrivileged(context)) return Results.Forbid();
    return Results.Ok(await store.GetOperationsSnapshotAsync());
});

app.MapGet("/api/admin/audit", async (Guid? meetingId, string? eventType, int? limit, int? offset, HttpContext context, UnifiedProductStore store) =>
{
    if (!IsPrivileged(context)) return Results.Forbid();
    var normalizedEventType = string.IsNullOrWhiteSpace(eventType) ? null : eventType.Trim();
    if (normalizedEventType is not null && normalizedEventType.Length > 80)
        return Results.BadRequest(new { error = "event_type_too_long" });
    return Results.Ok(await store.ListAuditEventsAsync(
        meetingId,
        normalizedEventType,
        Math.Clamp(limit ?? 100, 1, 200),
        Math.Max(offset ?? 0, 0)));
});
app.MapGet("/api/admin/meetings/{id:guid}/diagnostics", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    var meeting = await db.GetMeetingAsync(id, null, true);
    if (meeting is null) return Results.NotFound();
    var jobs = await db.ListJobsAsync(id);
    var media = await db.ListMediaAsync(id);
    var transcript = await db.GetTranscriptAsync(id);
    var summary = await store.GetLatestSummaryAsync(id);
    var traceId = context.Request.Headers["X-Trace-Id"].ToString();
    return Results.Ok(new
    {
        meeting = new { meeting.Id, meeting.Status, meeting.CreatedAt },
        media = media.Select(item => new { item.Id, item.Status, item.DurationMs, item.SizeBytes }),
        jobs = jobs.Select(item => new { item.Id, item.Type, item.Status, item.Stage, item.Progress, item.Attempt, item.Error }),
        transcript = transcript is null ? null : new
        {
            transcript.Id,
            transcript.Status,
            segmentCount = transcript.Segments.Count,
            transcript.IsPartial,
            transcript.QualityScore,
            warnings = transcript.Warnings
        },
        summary = summary is null ? null : new { summary.Id, summary.Status, summary.Version, summary.ModelName, summary.PromptVersion, summary.CreatedAt },
        correlation = new
        {
            meetingId = id,
            mediaAssetIds = media.Select(item => item.Id),
            processingJobIds = jobs.Select(item => item.Id),
            transcriptId = transcript?.Id,
            summaryId = summary?.Id,
            traceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId
        },
        timings = new { localFinalizeMs = (long?)null, uploadMs = (long?)null, assemblyMs = (long?)null, asrMs = (long?)null, summaryMs = (long?)null }
    });
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, IConfiguration configuration) =>
{
    var user = await db.FindUserAsync(request.Username);
    if (user is null || !PasswordService.Verify(request.Password, user.PasswordHash))
        return Results.Unauthorized();

    await IssueAuthCookiesAsync(http, configuration, user);
    return Results.Ok(new { user = new { id = user.Id, username = user.Username, role = user.Role } });
});

app.MapPost("/api/auth/refresh", async (HttpContext http, IConfiguration configuration) =>
{
    if (!http.Request.Cookies.TryGetValue("wa_refresh", out var currentRefresh) || string.IsNullOrWhiteSpace(currentRefresh))
        return Results.Unauthorized();

    var accessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    var rotated = await db.RotateRefreshSessionAsync(currentRefresh, refreshToken);
    if (rotated is null)
    {
        DeleteAuthCookies(http);
        return Results.Unauthorized();
    }

    await db.CreateSessionAsync(rotated.User.Id, accessToken);
    AppendAuthCookies(http, configuration, accessToken, refreshToken);
    return Results.Ok(new { user = new { id = rotated.User.Id, username = rotated.User.Username, role = rotated.User.Role } });
});

app.MapPost("/api/auth/logout", async (HttpContext http) =>
{
    if (http.Request.Cookies.TryGetValue("wa_session", out var token))
        await db.RevokeSessionAsync(token);
    if (http.Request.Cookies.TryGetValue("wa_refresh", out var refreshToken))
        await db.RevokeRefreshSessionAsync(refreshToken);
    http.Response.Cookies.Delete("wa_session");
    http.Response.Cookies.Delete("wa_refresh");
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/auth/me", async (HttpContext http) =>
{
    var user = await db.GetUserForSessionAsync(http.Request.Cookies["wa_session"]!);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(new { id = user.Id, username = user.Username, role = user.Role });
});

app.MapPost("/api/meetings", async (MeetingCreateRequest request, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "title_required" });
    if (CurrentUserId(context) is not Guid userId)
        return Results.Unauthorized();

    var meeting = await db.CreateMeetingAsync(userId, request.Title.Trim(), request.Description);
    return Results.Created("/api/meetings/" + meeting.Id, meeting);
});

app.MapGet("/api/meetings", async (int? limit, int? offset, HttpContext context) =>
{
    if (CurrentUserId(context) is not Guid userId)
        return Results.Unauthorized();
    return Results.Ok(await db.ListMeetingsAsync(Math.Clamp(limit ?? 50, 1, 200), Math.Max(offset ?? 0, 0), userId, IsPrivileged(context)));
});

app.MapGet("/api/search", async (string? q, Guid? meetingId, int? limit, int? offset, HttpContext context, UnifiedProductStore store) =>
{
    if (CurrentUserId(context) is not Guid userId)
        return Results.Unauthorized();
    var query = q?.Trim();
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "query_required" });
    if (query.Length > 200)
        return Results.BadRequest(new { error = "query_too_long" });
    if (meetingId is Guid selectedMeeting && !await CanAccessMeetingAsync(context, selectedMeeting))
        return Results.NotFound();

    return Results.Ok(await store.SearchAsync(
        query,
        meetingId,
        userId,
        IsPrivileged(context),
        Math.Clamp(limit ?? 50, 1, 200),
        Math.Max(offset ?? 0, 0)));
});

app.MapGet("/api/meetings/{id:guid}", async (Guid id, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    var meeting = await db.GetMeetingAsync(id, CurrentUserId(context), IsPrivileged(context));
    return meeting is null ? Results.NotFound() : Results.Ok(meeting);
});

app.MapPost("/api/meetings/{id:guid}/cancel", async (Guid id, MeetingCancelRequest? request, HttpContext context, UnifiedProductStore store) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    var result = await db.CancelMeetingAsync(id, request?.Force == true);
    if (result is null) return Results.NotFound();
    if (result.RecordingMustStop) return Results.Conflict(new { error = "recording_must_stop_before_cancellation" });
    var commands = await QueueRecorderCancellationAsync(store, result.AgentSessions, discardTransport: false);
    return Results.Ok(new { result.MeetingId, result.Status, result.CancelledJobs, agentCommandsQueued = commands });
});

app.MapDelete("/api/meetings/{id:guid}", async (Guid id, bool? force, HttpContext context, UnifiedProductStore store) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    var result = await db.DeleteMeetingAsync(id, force == true);
    if (result is null) return Results.NotFound();
    if (result.RecordingMustStop) return Results.Conflict(new { error = "recording_must_stop_before_deletion" });
    var commands = await QueueRecorderCancellationAsync(store, result.AgentSessions, discardTransport: true);
    var cleanup = MeetingStorageCleanup.Delete(result.StorageKeys, app.Logger);
    return Results.Ok(new { result.MeetingId, result.CancelledJobs, agentCommandsQueued = commands, filesDeleted = cleanup.FilesDeleted, fileDeleteFailures = cleanup.Failures });
});

app.MapPost("/api/meetings/{id:guid}/uploads", async (Guid id, UploadReservationRequest request, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, id))
        return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.FileName) ||
        !MediaPolicy.IsAllowedExtension(request.FileName) ||
        request.SizeBytes <= 0 || request.SizeBytes > MediaPolicy.MaxUploadBytes)
        return Results.BadRequest(new { error = "unsupported_or_oversized_media" });

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
    var suppliedSecret = request.Headers["X-Tus-Hook-Secret"].ToString();
    if (string.IsNullOrWhiteSpace(expectedSecret) || string.IsNullOrWhiteSpace(suppliedSecret) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedSecret), Encoding.UTF8.GetBytes(suppliedSecret)))
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
    var upload = JsonObject(payload, "Event", "Upload") ?? JsonObject(payload, "Upload") ?? payload;
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

static Guid? CurrentUserId(HttpContext context) => context.Items.TryGetValue("user_id", out var item) && item is Guid userId ? userId : null;

static bool IsPrivileged(HttpContext context) => context.Items.TryGetValue("user_role", out var item) && item is string role &&
    (string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Operator", StringComparison.OrdinalIgnoreCase));

static bool IsAdministrator(HttpContext context) => context.Items.TryGetValue("user_role", out var item) && item is string role &&
    string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase);

async Task<bool> CanAccessMeetingAsync(HttpContext context, Guid meetingId)
{
    if (IsPrivileged(context) || context.Items.ContainsKey("voice_host")) return true;
    var userId = CurrentUserId(context);
    return userId.HasValue && await db.UserOwnsMeetingAsync(meetingId, userId.Value);
}

static bool RoleAllows(string role, string method, PathString path)
{
    if (string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Operator", StringComparison.OrdinalIgnoreCase)) return true;
    if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) return true;
    if (!string.Equals(role, "Editor", StringComparison.OrdinalIgnoreCase)) return false;
    if (HttpMethods.IsPatch(method)) return true;
    if (HttpMethods.IsDelete(method) && path.StartsWithSegments("/api/assistant/conversations")) return true;
    return HttpMethods.IsPost(method) &&
        (path.StartsWithSegments("/api/assistant/queries") || path.StartsWithSegments("/api/assistant/conversations") ||
         path.Value?.Contains("/speakers/merge", StringComparison.OrdinalIgnoreCase) == true ||
         path.Value?.EndsWith("/summary/rebuild", StringComparison.OrdinalIgnoreCase) == true);
}

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
    return await store.CompleteCommandAsync(agentId, commandId, request.Status ?? "COMPLETED", request.Result ?? JsonDocument.Parse("{}")) ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.MapGet("/api/agents", async (UnifiedProductStore store) => Results.Ok(await store.ListAgentsAsync()));

app.MapPost("/api/agents/link-local", async (AgentLinkLocalRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    if (request.InstallationId == Guid.Empty) return Results.BadRequest(new { error = "installation_id_required" });
    if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "agent_name_required" });

    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var agent = await store.LinkLocalAgentAsync(
        request.InstallationId,
        request.AgentId,
        request.Name,
        request.RoomId,
        token,
        request.Version ?? "0.1.0",
        request.Capabilities ?? JsonDocument.Parse("{}"));
    return Results.Ok(new { agentId = agent.Id, agent, token });
});

app.MapPost("/api/v1/recording-sessions", async (CreateRecordingSessionRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var session = await store.CreateRecordingSessionAsync(request.MeetingId, agentId, request.Title, request.StartedAt);
    return session is null ? Results.NotFound() : Results.Created($"/api/v1/recording-sessions/{session.Id}", session);
});

app.MapPost("/api/v1/recording-sessions/{sessionId:guid}/tracks", async (Guid sessionId, CreateTrackRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var track = await store.CreateRecordingTrackAsync(agentId, sessionId, request.TrackType, request.DeviceId, request.SampleRate, request.Channels);
    return track is null ? Results.NotFound() : Results.Created($"/api/v1/recording-sessions/{sessionId}/tracks/{track.Id}", track);
});

app.MapPut("/api/v1/recording-sessions/{sessionId:guid}/tracks/{trackId:guid}/chunks/{sequence:int}", async (Guid sessionId, Guid trackId, int sequence, HttpRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    if (!await store.AgentOwnsTrackAsync(agentId, sessionId, trackId)) return Results.NotFound();
    if (request.ContentLength is > 134217728) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    if (sequence < 0) return Results.BadRequest(new { error = "chunk_sequence_invalid" });
    var key = $"/data/recordings/{sessionId:N}/{trackId:N}/{sequence:D8}.flac";
    var path = StorageHelpers.StoragePath(key);
    var partPath = path + "." + Guid.NewGuid().ToString("N") + ".part";
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
        var startSample = long.TryParse(request.Headers["X-Start-Sample"], out var parsedStart) ? parsedStart : 0;
        var sampleCount = long.TryParse(request.Headers["X-Sample-Count"], out var parsedCount) ? parsedCount : 0;
        if (File.Exists(path))
        {
            var existingSha = await StorageHelpers.ComputeSha256Async(path);
            File.Delete(partPath);
            if (!string.Equals(existingSha, sha, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "chunk_sequence_hash_conflict" });
            var repaired = await store.ConfirmChunkAsync(agentId, sessionId, trackId, sequence, key, startSample, sampleCount, new FileInfo(path).Length, existingSha);
            return repaired
                ? Results.Ok(new { sequence, storageKey = key, sizeBytes = new FileInfo(path).Length, sha256 = existingSha, idempotent = true })
                : Results.Conflict(new { error = "chunk_sequence_hash_conflict" });
        }
        var staged = await store.StageChunkAsync(agentId, sessionId, trackId, sequence, key, startSample, sampleCount, size, sha);
        if (!staged)
        {
            File.Delete(partPath);
            return Results.Conflict(new { error = "chunk_sequence_hash_conflict" });
        }
        File.Move(partPath, path, true);
        var stored = await store.ConfirmChunkAsync(agentId, sessionId, trackId, sequence, key, startSample, sampleCount, size, sha);
        if (!stored)
        {
            return Results.Json(new { error = "chunk_confirmation_pending" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
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
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var missing = await store.MissingChunksAsync(agentId, sessionId, trackId, Math.Clamp(expectedCount, 0, 100000));
    return missing is null ? Results.NotFound() : Results.Ok(new { missing });
});

app.MapPost("/api/v1/recording-sessions/{sessionId:guid}/finalize", async (Guid sessionId, FinalizeRecordingRequest? request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var result = await store.FinalizeRecordingAsync(agentId, sessionId, request?.Manifest);
    if (!result.Found) return Results.NotFound(new { error = result.ErrorCode });
    if (!result.Accepted) return Results.Conflict(new { error = result.ErrorCode, missing = result.Missing });
    return Results.Accepted($"/api/jobs/{result.JobId}", new { result.JobId, result.MediaAssetId, result.MeetingId, sessionId, state = "ASSEMBLY_QUEUED" });
});

app.MapGet("/api/v1/recording-sessions/{sessionId:guid}/status", async (Guid sessionId, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var status = await store.GetRecordingSessionStatusAsync(agentId, sessionId);
    return status is null ? Results.NotFound(new { error = "recording_session_not_found" }) : Results.Ok(status);
});

app.MapPost("/api/v1/recording-sessions/{sessionId:guid}/events/batch", async (Guid sessionId, RecordingEventBatchRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    if (request.Events is null || request.Events.Count > 1000) return Results.BadRequest(new { error = "events_invalid" });
    var accepted = await store.RegisterRecordingEventsAsync(agentId, sessionId, request.Events);
    return accepted is null ? Results.NotFound() : Results.Ok(new { accepted });
});

app.MapPost("/api/meetings/{id:guid}/recording-commands", async (Guid id, RecordingCommandRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (request.AgentId == Guid.Empty) return Results.BadRequest(new { error = "agent_id_required" });
    if (string.IsNullOrWhiteSpace(request.CommandType) || request.CommandType.Length > 80)
        return Results.BadRequest(new { error = "command_type_required" });
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    if (!await store.AgentExistsAsync(request.AgentId)) return Results.NotFound(new { error = "agent_not_found" });
    var commandId = await store.CreateCommandAsync(request.AgentId, request.CommandType.Trim(), request.Payload ?? JsonDocument.Parse("{}"));
    return Results.Accepted($"/api/agents/{request.AgentId}", new { commandId });
});

app.MapGet("/api/meetings/{id:guid}/summary", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    return Results.Ok(await store.GetLatestSummaryAsync(id));
});
app.MapPost("/api/meetings/{id:guid}/summary/rebuild", async (Guid id, SummaryRebuildRequest? request, HttpContext context, UnifiedProductStore store) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    if (request?.Profile is { Length: > 40 } || request?.PromptVersion is { Length: > 120 } || request?.Reason is { Length: > 500 })
        return Results.BadRequest(new { error = "summary_options_too_long" });
    var jobId = await store.QueueSummaryAsync(id, CurrentUserId(context), request);
    return jobId is null ? Results.Conflict(new { error = "transcript_required" }) : Results.Accepted($"/api/jobs/{jobId}", new { jobId });
});
app.MapGet("/api/meetings/{id:guid}/decisions", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    return Results.Ok(await store.ListDecisionsAsync(id));
});
app.MapGet("/api/meetings/{id:guid}/tasks", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    return Results.Ok(await store.ListActionItemsAsync(id));
});
app.MapPatch("/api/tasks/{id:guid}", async (Guid id, UpdateTaskRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var meetingId = await store.GetActionItemMeetingIdAsync(id);
    if (meetingId is null || !await CanAccessMeetingAsync(context, meetingId.Value)) return Results.NotFound();
    var task = request.Task?.Trim();
    var status = request.Status?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(task) || task.Length > 4000)
        return Results.BadRequest(new { error = "task_required" });
    if (status is not ("NEEDS_REVIEW" or "OPEN" or "DONE" or "CANCELLED"))
        return Results.BadRequest(new { error = "invalid_task_status" });
    return (await store.UpdateActionItemAsync(id, task, request.Responsible?.Trim(), request.Deadline, status, CurrentUserId(context))) switch
    {
        ActionItemUpdateResult.Updated => Results.Ok(new { ok = true }),
        ActionItemUpdateResult.InvalidTransition => Results.Conflict(new { error = "invalid_task_transition" }),
        _ => Results.NotFound()
    };
});
app.MapGet("/api/assistant/conversations", async (HttpContext context, UnifiedProductStore store, bool? includeArchived) =>
{
    var userId = CurrentUserId(context);
    return userId is null
        ? Results.Unauthorized()
        : Results.Ok(await store.ListAssistantConversationsAsync(userId.Value, includeArchived == true));
});
app.MapPost("/api/assistant/conversations", async (AssistantConversationCreateRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    if (userId is null) return Results.Unauthorized();
    var scope = request.ScopeType?.Trim().ToUpperInvariant();
    if (scope == "GLOBAL" && !IsPrivileged(context)) return Results.Forbid();
    if (scope == "MEETING" && (!request.MeetingId.HasValue || !await CanAccessMeetingAsync(context, request.MeetingId.Value))) return Results.NotFound();
    if (scope is not ("MEETING" or "GLOBAL")) return Results.BadRequest(new { error = "invalid_assistant_scope" });
    var conversation = await store.CreateAssistantConversationAsync(userId.Value, request.Title, scope, request.MeetingId);
    return conversation is null ? Results.BadRequest(new { error = "assistant_context_not_ready" }) : Results.Created($"/api/assistant/conversations/{conversation.Id}", conversation);
});
app.MapGet("/api/assistant/conversations/{id:guid}", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    var conversation = userId is null ? null : await store.GetAssistantConversationAsync(id, userId.Value);
    return conversation is null ? Results.NotFound() : Results.Ok(conversation);
});
app.MapPatch("/api/assistant/conversations/{id:guid}", async (Guid id, AssistantConversationUpdateRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    if (userId is null) return Results.Unauthorized();
    return await store.UpdateAssistantConversationAsync(id, userId.Value, request.Title, request.Archived) ? Results.Ok(new { ok = true }) : Results.NotFound();
});
app.MapDelete("/api/assistant/conversations/{id:guid}", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    if (userId is null) return Results.Unauthorized();
    return await store.DeleteAssistantConversationAsync(id, userId.Value) ? Results.Ok(new { ok = true }) : Results.NotFound();
});
app.MapGet("/api/assistant/conversations/{id:guid}/messages", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    if (userId is null) return Results.Unauthorized();
    if (await store.GetAssistantConversationAsync(id, userId.Value) is null) return Results.NotFound();
    return Results.Ok(await store.ListAssistantMessagesAsync(id, userId.Value));
});
app.MapPost("/api/assistant/conversations/{id:guid}/messages", async (Guid id, AssistantMessageCreateRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    if (userId is null) return Results.Unauthorized();
    var result = await store.CreateAssistantMessageAsync(id, userId.Value, request.Content ?? string.Empty, request.RetryOf);
    return result is null ? Results.BadRequest(new { error = "assistant_conversation_not_ready" }) : Results.Accepted($"/api/assistant/conversations/{id}/messages/{result.AssistantMessage.Id}/events", result);
});
app.MapGet("/api/assistant/conversations/{conversationId:guid}/messages/{messageId:guid}/events", async (Guid conversationId, Guid messageId, HttpContext context, HttpResponse response, UnifiedProductStore store, CancellationToken cancellationToken) =>
{
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    var userId = CurrentUserId(context);
    if (userId is null) { response.StatusCode = StatusCodes.Status401Unauthorized; return; }
    if (await store.GetAssistantConversationAsync(conversationId, userId.Value) is null) { response.StatusCode = StatusCodes.Status404NotFound; return; }
    for (var attempt = 0; attempt < 120 && !cancellationToken.IsCancellationRequested; attempt++)
    {
        var messages = await store.ListAssistantMessagesAsync(conversationId, userId.Value);
        var message = messages.FirstOrDefault(item => item.Id == messageId);
        if (message is null) { response.StatusCode = StatusCodes.Status404NotFound; return; }
        await response.WriteAsync($"event: status\ndata: {JsonSerializer.Serialize(message)}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        if (message.Status is "READY" or "FAILED" or "NEEDS_REVIEW") return;
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
});
app.MapPost("/api/assistant/queries", async (AssistantQueryRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (request.MeetingId is Guid meetingId && !await CanAccessMeetingAsync(context, meetingId))
        return Results.NotFound();
    if (request.MeetingId is null && !IsPrivileged(context) && !context.Items.ContainsKey("voice_host"))
        return Results.BadRequest(new { error = "meeting_required" });
    var query = await store.CreateAssistantQueryAsync(request.MeetingId, request.Query, CurrentUserId(context));
    return query is null ? Results.BadRequest(new { error = "meeting_not_ready_or_query_invalid" }) : Results.Accepted($"/api/assistant/queries/{query.Id}", query);
});
app.MapGet("/api/assistant/queries/{id:guid}", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    var query = await store.GetAssistantQueryAsync(id, CurrentUserId(context), IsPrivileged(context) || context.Items.ContainsKey("voice_host"));
    return query is null ? Results.NotFound() : Results.Ok(query);
});
app.MapGet("/api/assistant/queries/{id:guid}/events", async (Guid id, HttpContext context, HttpResponse response, UnifiedProductStore store, CancellationToken cancellationToken) =>
{
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    for (var attempt = 0; attempt < 120 && !cancellationToken.IsCancellationRequested; attempt++)
    {
        var query = await store.GetAssistantQueryAsync(id, CurrentUserId(context), IsPrivileged(context) || context.Items.ContainsKey("voice_host"));
        if (query is null) { response.StatusCode = 404; return; }
        await response.WriteAsync($"event: status\ndata: {JsonSerializer.Serialize(query)}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        if (query.Status is "READY" or "FAILED" or "NEEDS_REVIEW") return;
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
});
app.MapGet("/api/meetings/{id:guid}/jobs", async (Guid id, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    return Results.Ok(await db.ListJobsAsync(id));
});

app.MapGet("/api/meetings/{id:guid}/media", async (Guid id, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    return Results.Ok(await db.ListMediaAsync(id));
});

app.MapGet("/api/meetings/{id:guid}/speakers", async (Guid id, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    return Results.Ok(await db.ListSpeakersAsync(id));
});

app.MapGet("/api/media/{id:guid}/preview", async (Guid id, HttpContext context) =>
{
    var media = await db.GetMediaAsync(id);
    if (media is null || !await CanAccessMeetingAsync(context, media.MeetingId) || string.IsNullOrWhiteSpace(media.PreviewStorageKey)) return Results.NotFound();
    var path = StorageHelpers.StoragePath(media.PreviewStorageKey);
    if (!File.Exists(path)) return Results.NotFound();
    return Results.File(File.OpenRead(path), "audio/ogg", enableRangeProcessing: true);
});

app.MapGet("/api/jobs/{id:guid}", async (Guid id, HttpContext context) =>
{
    var job = await db.GetJobAsync(id);
    return job is null || !await CanAccessMeetingAsync(context, job.MeetingId) ? Results.NotFound() : Results.Ok(job);
});

app.MapPost("/api/jobs/{id:guid}/retry", async (Guid id, HttpContext context) =>
{
    var current = await db.GetJobAsync(id);
    if (current is null || !await CanAccessMeetingAsync(context, current.MeetingId)) return Results.NotFound();
    if (current.Status is not ("FAILED" or "CANCELLED"))
        return Results.Conflict(new { error = "job_not_retryable", status = current.Status });
    var job = await db.RetryJobAsync(id);
    return job is null ? Results.Conflict(new { error = "job_retry_conflict" }) : Results.Accepted("/api/jobs/" + id, job);
});

app.MapGet("/api/jobs/{id:guid}/events", async (Guid id, HttpContext context, HttpResponse response, CancellationToken cancellationToken) =>
{
    var initialJob = await db.GetJobAsync(id);
    if (initialJob is null || !await CanAccessMeetingAsync(context, initialJob.MeetingId)) { response.StatusCode = 404; return; }
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
        if (job.Status is "READY" or "FAILED" or "CANCELLED")
            return;
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
});

app.MapGet("/api/meetings/{id:guid}/transcript", async (Guid id, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    var transcript = await db.GetTranscriptAsync(id);
    return Results.Ok(new
    {
        id = transcript.Id,
        meetingId = transcript.MeetingId,
        status = transcript.Status,
        isPartial = transcript.IsPartial,
        warnings = transcript.Warnings,
        qualityWarnings = transcript.Warnings,
        quality = transcript.Quality,
        qualityScore = transcript.QualityScore,
        segments = transcript.Segments
    });
});

app.MapPatch("/api/meetings/{meetingId:guid}/speakers/{speakerId:guid}",
    async (Guid meetingId, Guid speakerId, SpeakerRenameRequest request, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, meetingId)) return Results.NotFound();
    var updated = await db.RenameSpeakerAsync(meetingId, speakerId, request.DisplayName);
    return updated ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.MapPost("/api/meetings/{meetingId:guid}/speakers/merge",
    async (Guid meetingId, SpeakerMergeRequest request, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, meetingId)) return Results.NotFound();
    var merged = await db.MergeSpeakersAsync(meetingId, request.SourceSpeakerId, request.TargetSpeakerId);
    return merged ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.Run();

public record AgentEnrollRequest(string Name, Guid? RoomId, string? Version, JsonDocument? Capabilities);
public record AgentLinkLocalRequest(Guid InstallationId, Guid? AgentId, string Name, Guid? RoomId, string? Version, JsonDocument? Capabilities);
public record AgentHeartbeatRequest(string? Status, string? Version, JsonDocument? Capabilities);
public record AgentCommandResultRequest(string? Status, JsonDocument? Result);
public record CreateRecordingSessionRequest(Guid? MeetingId, string? Title, DateTimeOffset? StartedAt);
public record CreateTrackRequest(string TrackType, string? DeviceId, int SampleRate = 48000, int Channels = 1);
public record RecordingCommandRequest(Guid AgentId, string CommandType, JsonDocument? Payload);
public record UpdateTaskRequest(string Task, string? Responsible, DateTime? Deadline, string Status);
public record AssistantQueryRequest(string Query, Guid? MeetingId);
public record AssistantConversationCreateRequest(string? Title, string? ScopeType, Guid? MeetingId);
public record AssistantConversationUpdateRequest(string? Title, bool? Archived);
public record AssistantMessageCreateRequest(string? Content, Guid? RetryOf);
public record SummaryRebuildRequest(string? Profile, int? TranscriptVersion, string? PromptVersion, string? Reason, JsonDocument? MeetingContext);
public record RecordingEventRequest(Guid Id, string EventType, long? MediaTimeMs, JsonDocument? Payload, DateTimeOffset? CreatedAt);
public record RecordingEventBatchRequest(IReadOnlyList<RecordingEventRequest> Events);

public record LoginRequest(string Username, string Password);
public record MeetingCreateRequest(string Title, string? Description);
public record MeetingCancelRequest(bool Force = false);
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
public sealed record RefreshRotation(UserRow User, string RefreshToken);
public sealed record MeetingRow(Guid Id, string Title, string? Description, string Status, DateTime CreatedAt);
public sealed record JobRow(Guid Id, Guid MeetingId, string Type, string Status, string Stage, int Progress, int Attempt, string? Error);
public sealed record AgentSessionCancellationTarget(Guid AgentId, Guid ServerSessionId);
public sealed record MeetingCancellationResult(Guid MeetingId, string Status, int CancelledJobs, IReadOnlyList<AgentSessionCancellationTarget> AgentSessions, bool RecordingMustStop = false);
public sealed record MeetingDeletionResult(Guid MeetingId, int CancelledJobs, IReadOnlyList<string> StorageKeys, IReadOnlyList<AgentSessionCancellationTarget> AgentSessions, bool RecordingMustStop = false);
public sealed record TranscriptSegmentRow(Guid Id, int Ordinal, long StartMs, long EndMs, string? Speaker, string Text, double? Confidence, JsonDocument? Words, string SegmentKind = "SPEECH", bool IsHidden = false);
public sealed record TranscriptRow(Guid Id, Guid MeetingId, string Status, IReadOnlyList<TranscriptSegmentRow> Segments, bool IsPartial = false, JsonDocument? Warnings = null, JsonDocument? Quality = null, double? QualityScore = null);
public sealed record CorrelationContext(Guid? MeetingId = null, string? LocalSessionId = null, Guid? ServerSessionId = null, Guid? MediaAssetId = null, Guid? ProcessingJobId = null, Guid? TranscriptId = null, Guid? SummaryJobId = null, Guid? SummaryId = null, string? TraceId = null);

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
    public const long MaxUploadBytes = 8L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".mp4", ".mkv", ".mov", ".webm", ".avi" };
    public static bool IsAllowedExtension(string name) => Extensions.Contains(Path.GetExtension(name));
}

public static class MeetingStorageCleanup
{
    public static (int FilesDeleted, IReadOnlyList<string> Failures) Delete(IReadOnlyList<string> storageKeys, ILogger logger)
    {
        var deleted = 0;
        var failures = new List<string>();
        foreach (var key in storageKeys.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var path = StorageHelpers.StoragePath(key);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deleted++;
            }
            catch (Exception exception)
            {
                failures.Add(key);
                logger.LogWarning(exception, "Unable to delete meeting storage object {StorageKey}", key);
            }
        }
        return (deleted, failures);
    }
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
            "SELECT u.id, u.username, u.password_hash, u.role FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@hash AND s.expires_at > now() AND s.revoked_at IS NULL AND u.is_active", connection);
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

    public async Task CreateRefreshSessionAsync(Guid userId, string token, Guid familyId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO refresh_sessions(id,user_id,family_id,token_hash,expires_at) VALUES(@id,@uid,@family,@hash,now()+interval '30 days')", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("uid", userId);
        command.Parameters.AddWithValue("family", familyId);
        command.Parameters.AddWithValue("hash", SessionHash(token));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<RefreshRotation?> RotateRefreshSessionAsync(string token, string replacementToken)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var select = new NpgsqlCommand(
            "SELECT r.id,r.user_id,r.family_id,r.expires_at,r.revoked_at,r.replaced_by,u.id,u.username,u.password_hash,u.role FROM refresh_sessions r JOIN users u ON u.id=r.user_id WHERE r.token_hash=@hash FOR UPDATE", connection, transaction);
        select.Parameters.AddWithValue("hash", SessionHash(token));
        await using var reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            await transaction.CommitAsync();
            return null;
        }

        var id = reader.GetGuid(0);
        var user = new UserRow(reader.GetGuid(6), reader.GetString(7), reader.GetString(8), reader.GetString(9));
        var familyId = reader.GetGuid(2);
        var expired = reader.GetDateTime(3) <= DateTime.UtcNow;
        var revoked = !reader.IsDBNull(4);
        var replaced = !reader.IsDBNull(5);
        await reader.CloseAsync();

        if (expired || !await UserIsActiveAsync(user.Id, connection, transaction))
        {
            await transaction.CommitAsync();
            return null;
        }

        if (revoked || replaced)
        {
            await using var revokeFamily = new NpgsqlCommand("UPDATE refresh_sessions SET revoked_at=COALESCE(revoked_at,now()) WHERE family_id=@family AND revoked_at IS NULL", connection, transaction);
            revokeFamily.Parameters.AddWithValue("family", familyId);
            await revokeFamily.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return null;
        }

        var replacementId = Guid.NewGuid();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO refresh_sessions(id,user_id,family_id,token_hash,expires_at) VALUES(@id,@uid,@family,@hash,now()+interval '30 days')", connection, transaction);
        insert.Parameters.AddWithValue("id", replacementId);
        insert.Parameters.AddWithValue("uid", user.Id);
        insert.Parameters.AddWithValue("family", familyId);
        insert.Parameters.AddWithValue("hash", SessionHash(replacementToken));
        await insert.ExecuteNonQueryAsync();

        await using var replace = new NpgsqlCommand("UPDATE refresh_sessions SET revoked_at=now(),last_used_at=now(),replaced_by=@replacement WHERE id=@id", connection, transaction);
        replace.Parameters.AddWithValue("replacement", replacementId);
        replace.Parameters.AddWithValue("id", id);
        await replace.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return new RefreshRotation(user, replacementToken);
    }

    private static async Task<bool> UserIsActiveAsync(Guid userId, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT is_active FROM users WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", userId);
        return (bool?)await command.ExecuteScalarAsync() == true;
    }

    public async Task RevokeRefreshSessionAsync(string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE refresh_sessions SET revoked_at=now() WHERE token_hash=@hash", connection);
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

    public async Task<MeetingRow> CreateMeetingAsync(Guid ownerId, string title, string? description)
    {
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO meetings(id,owner_id,title,description,status) VALUES(@id,@owner,@title,@description,'CREATED') RETURNING created_at", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        var created = (DateTime)(await command.ExecuteScalarAsync())!;
        return new MeetingRow(id, title, description, "CREATED", created);
    }

    public async Task<bool> UserOwnsMeetingAsync(Guid meetingId, Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM meetings WHERE id=@meeting AND owner_id=@owner)", connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        command.Parameters.AddWithValue("owner", userId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<MeetingRow>> ListMeetingsAsync(int limit, int offset, Guid ownerId, bool includeAll)
    {
        var result = new List<MeetingRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id,title,description,status,created_at FROM meetings WHERE @include_all OR owner_id=@owner ORDER BY created_at DESC LIMIT @limit OFFSET @offset", connection);
        command.Parameters.AddWithValue("include_all", includeAll);
        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new MeetingRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetDateTime(4)));
        return result;
    }

    public async Task<MeetingRow?> GetMeetingAsync(Guid id, Guid? ownerId, bool includeAll)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,title,description,status,created_at FROM meetings WHERE id=@id AND (@include_all OR owner_id=@owner)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", (object?)ownerId ?? DBNull.Value);
        command.Parameters.AddWithValue("include_all", includeAll);
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

        // Serialize completion of uploads with the same content hash. This avoids a
        // race where two simultaneous finalizes both pass the duplicate check and
        // one of them fails on ux_media_assets_sha256 with an HTTP 500.
        await using (var hashLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@sha, 0))", connection, tx))
        {
            hashLock.Parameters.AddWithValue("sha", payload.Sha256);
            await hashLock.ExecuteNonQueryAsync();
        }

        Guid? duplicateOf = null;
        await using (var duplicate = new NpgsqlCommand(
            "SELECT id FROM media_assets WHERE sha256=@sha AND upload_id IS DISTINCT FROM @upload ORDER BY created_at LIMIT 1",
            connection, tx))
        {
            duplicate.Parameters.AddWithValue("sha", payload.Sha256);
            duplicate.Parameters.AddWithValue("upload", payload.UploadId);
            var value = await duplicate.ExecuteScalarAsync();
            if (value is Guid id)
                duplicateOf = id;
        }

        await using var asset = new NpgsqlCommand(
            "UPDATE media_assets SET storage_key=@key, sha256=@sha, duplicate_of=@duplicate, size_bytes=@size, duration_ms=@duration, status=@status WHERE upload_id=@upload AND status='UPLOADING' RETURNING id,meeting_id",
            connection, tx);
        asset.Parameters.AddWithValue("key", normalizedStorageKey);
        asset.Parameters.AddWithValue("sha", duplicateOf.HasValue ? DBNull.Value : payload.Sha256);
        asset.Parameters.AddWithValue("duplicate", duplicateOf.HasValue ? duplicateOf.Value : DBNull.Value);
        asset.Parameters.AddWithValue("size", payload.SizeBytes);
        asset.Parameters.AddWithValue("duration", payload.DurationMs);
        asset.Parameters.AddWithValue("status", duplicateOf.HasValue ? "UPLOADED_DUPLICATE" : "UPLOADED");
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
            duplicate_of = duplicateOf,
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
        meeting.Parameters.AddWithValue("id", meetingId); meeting.Parameters.AddWithValue("title", string.IsNullOrWhiteSpace(title) ? "Безымянная запись" : title);
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

    public async Task<MeetingCancellationResult?> CancelMeetingAsync(Guid meetingId, bool force)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var meeting = new NpgsqlCommand("SELECT status FROM meetings WHERE id=@meeting FOR UPDATE", connection, transaction);
        meeting.Parameters.AddWithValue("meeting", meetingId);
        var status = await meeting.ExecuteScalarAsync() as string;
        if (status is null) return null;
        if (status == "RECORDING" && !force)
            return new MeetingCancellationResult(meetingId, status, 0, [], RecordingMustStop: true);

        var agentSessions = await GetAgentSessionCancellationTargetsAsync(connection, transaction, meetingId);

        await using (var cancelJobs = new NpgsqlCommand(
            "UPDATE jobs SET status='CANCELLED',stage='CANCELLED',error_message='cancelled_by_user',error_code='CANCELLED_BY_USER',worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE meeting_id=@meeting AND status NOT IN ('READY','FAILED','CANCELLED')", connection, transaction))
        {
            cancelJobs.Parameters.AddWithValue("meeting", meetingId);
            var cancelled = await cancelJobs.ExecuteNonQueryAsync();
            await using var removeInbox = new NpgsqlCommand("DELETE FROM inbox_messages WHERE job_id IN (SELECT id FROM jobs WHERE meeting_id=@meeting)", connection, transaction);
            removeInbox.Parameters.AddWithValue("meeting", meetingId);
            await removeInbox.ExecuteNonQueryAsync();
            await using var removeOutbox = new NpgsqlCommand("DELETE FROM outbox_messages WHERE published_at IS NULL AND payload->>'meeting_id'=CAST(@meeting AS text)", connection, transaction);
            removeOutbox.Parameters.AddWithValue("meeting", meetingId);
            await removeOutbox.ExecuteNonQueryAsync();
            await using var updateMeeting = new NpgsqlCommand("UPDATE meetings SET status='CANCELLED',finished_at=COALESCE(finished_at,now()) WHERE id=@meeting", connection, transaction);
            updateMeeting.Parameters.AddWithValue("meeting", meetingId);
            await updateMeeting.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return new MeetingCancellationResult(meetingId, "CANCELLED", cancelled, agentSessions);
        }
    }

    public async Task<MeetingDeletionResult?> DeleteMeetingAsync(Guid meetingId, bool force)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var meeting = new NpgsqlCommand("SELECT status FROM meetings WHERE id=@meeting FOR UPDATE", connection, transaction);
        meeting.Parameters.AddWithValue("meeting", meetingId);
        var status = await meeting.ExecuteScalarAsync() as string;
        if (status is null) return null;
        if (status == "RECORDING" && !force)
            return new MeetingDeletionResult(meetingId, 0, [], [], RecordingMustStop: true);

        var agentSessions = await GetAgentSessionCancellationTargetsAsync(connection, transaction, meetingId);

        var storageKeys = new HashSet<string>(StringComparer.Ordinal);
        await using (var assets = new NpgsqlCommand("SELECT storage_key,archive_storage_key,preview_storage_key,asr_storage_key FROM media_assets WHERE meeting_id=@meeting", connection, transaction))
        {
            assets.Parameters.AddWithValue("meeting", meetingId);
            await using var reader = await assets.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                for (var index = 0; index < reader.FieldCount; index++)
                    if (!reader.IsDBNull(index) && !string.IsNullOrWhiteSpace(reader.GetString(index))) storageKeys.Add(reader.GetString(index));
        }
        await using (var chunks = new NpgsqlCommand("SELECT c.storage_key FROM recording_chunks c JOIN recording_sessions s ON s.id=c.session_id WHERE s.meeting_id=@meeting", connection, transaction))
        {
            chunks.Parameters.AddWithValue("meeting", meetingId);
            await using var reader = await chunks.ExecuteReaderAsync();
            while (await reader.ReadAsync()) storageKeys.Add(reader.GetString(0));
        }

        var unsharedKeys = new List<string>();
        foreach (var key in storageKeys)
        {
            await using var shared = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM media_assets WHERE meeting_id<>@meeting AND (storage_key=@key OR archive_storage_key=@key OR preview_storage_key=@key OR asr_storage_key=@key))", connection, transaction);
            shared.Parameters.AddWithValue("meeting", meetingId);
            shared.Parameters.AddWithValue("key", key);
            if ((bool)(await shared.ExecuteScalarAsync())!) unsharedKeys.Add(key);
        }
        var keysToDelete = storageKeys.Except(unsharedKeys, StringComparer.Ordinal).ToArray();

        await using var cancelJobs = new NpgsqlCommand("UPDATE jobs SET status='CANCELLED',stage='CANCELLED',error_message='deleted_by_user',error_code='CANCELLED_BY_USER',worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE meeting_id=@meeting AND status NOT IN ('READY','FAILED','CANCELLED')", connection, transaction);
        cancelJobs.Parameters.AddWithValue("meeting", meetingId);
        var cancelledJobs = await cancelJobs.ExecuteNonQueryAsync();

        foreach (var sql in new[]
        {
            "DELETE FROM outbox_messages WHERE payload->>'meeting_id'=CAST(@meeting AS text)",
            "DELETE FROM inbox_messages WHERE job_id IN (SELECT id FROM jobs WHERE meeting_id=@meeting)",
            "DELETE FROM action_items WHERE meeting_id=@meeting",
            "DELETE FROM decisions WHERE meeting_id=@meeting",
            "DELETE FROM summary_evidence WHERE summary_id IN (SELECT id FROM summaries WHERE meeting_id=@meeting)",
            "DELETE FROM summary_runs WHERE summary_id IN (SELECT id FROM summaries WHERE meeting_id=@meeting)",
            "DELETE FROM summaries WHERE meeting_id=@meeting",
            "DELETE FROM assistant_queries WHERE meeting_id=@meeting",
            "DELETE FROM audit_events WHERE meeting_id=@meeting",
            "DELETE FROM transcript_segments WHERE transcript_id IN (SELECT id FROM transcripts WHERE meeting_id=@meeting)",
            "DELETE FROM meeting_speakers WHERE meeting_id=@meeting",
            "DELETE FROM transcripts WHERE meeting_id=@meeting",
            "DELETE FROM recording_events WHERE session_id IN (SELECT id FROM recording_sessions WHERE meeting_id=@meeting)",
            "DELETE FROM recording_chunks WHERE session_id IN (SELECT id FROM recording_sessions WHERE meeting_id=@meeting)",
            "DELETE FROM recording_tracks WHERE session_id IN (SELECT id FROM recording_sessions WHERE meeting_id=@meeting)",
            "DELETE FROM recording_sessions WHERE meeting_id=@meeting",
            "DELETE FROM job_attempts WHERE job_id IN (SELECT id FROM jobs WHERE meeting_id=@meeting)",
            "DELETE FROM jobs WHERE meeting_id=@meeting",
            "UPDATE media_assets SET duplicate_of=NULL WHERE meeting_id<>@meeting AND duplicate_of IN (SELECT id FROM media_assets WHERE meeting_id=@meeting)",
            "DELETE FROM media_assets WHERE meeting_id=@meeting",
            "DELETE FROM meetings WHERE id=@meeting",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("meeting", meetingId);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return new MeetingDeletionResult(meetingId, cancelledJobs, keysToDelete, agentSessions);
    }

    private static async Task<IReadOnlyList<AgentSessionCancellationTarget>> GetAgentSessionCancellationTargetsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid meetingId)
    {
        var result = new List<AgentSessionCancellationTarget>();
        await using var command = new NpgsqlCommand(
            "SELECT agent_id,id FROM recording_sessions WHERE meeting_id=@meeting AND agent_id IS NOT NULL", connection, transaction);
        command.Parameters.AddWithValue("meeting", meetingId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new AgentSessionCancellationTarget(reader.GetGuid(0), reader.GetGuid(1)));
        return result;
    }

    public async Task<JobRow?> RetryJobAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE jobs SET status='QUEUED',stage=CASE WHEN type='SUMMARIZE' THEN 'TRANSCRIPT_READY' ELSE 'UPLOADED' END,progress=0,error_message=NULL,error_code=NULL,worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,attempt=attempt+1,updated_at=now() WHERE id=@id AND status IN ('FAILED','CANCELLED') RETURNING id,meeting_id,type,status,stage,progress,attempt,error_message", connection, tx);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var job = ReadJob(reader);
        await reader.CloseAsync();

        var messageId = Guid.NewGuid();
        await using var publish = job.Type switch
        {
            "SUMMARIZE" => new NpgsqlCommand(
                "INSERT INTO outbox_messages(id,topic,payload) SELECT @outbox,'llm.summarize',jsonb_build_object('message_id',@message,'job_id',j.id,'meeting_id',j.meeting_id,'transcript_id',t.id) FROM jobs j JOIN LATERAL (SELECT id FROM transcripts WHERE meeting_id=j.meeting_id ORDER BY version DESC LIMIT 1) t ON true WHERE j.id=@id", connection, tx),
            "TRANSCRIBE" => new NpgsqlCommand(
                "INSERT INTO outbox_messages(id,topic,payload) SELECT @outbox,'ml.transcribe',jsonb_build_object('message_id',@message,'job_id',j.id,'meeting_id',j.meeting_id,'media_asset_id',j.media_asset_id,'stage',j.stage,'attempt',j.attempt,'storage_key',a.asr_storage_key,'source_type',a.source_type) FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE j.id=@id", connection, tx),
            _ => new NpgsqlCommand(
                "INSERT INTO outbox_messages(id,topic,payload) SELECT @outbox,'media.ingest',jsonb_build_object('message_id',@message,'job_id',j.id,'meeting_id',j.meeting_id,'media_asset_id',j.media_asset_id,'stage',j.stage,'attempt',j.attempt,'storage_key',a.storage_key,'source_type',a.source_type) FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE j.id=@id", connection, tx),
        };
        await using (publish)
        {
            publish.Parameters.AddWithValue("outbox", Guid.NewGuid());
            publish.Parameters.AddWithValue("message", messageId);
            publish.Parameters.AddWithValue("id", id);
            await publish.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return job;
    }
    public async Task<TranscriptRow> GetTranscriptAsync(Guid meetingId)
    {
        var segments = new List<TranscriptSegmentRow>();
        Guid transcriptId = Guid.Empty;
        string status = "PENDING";
        JsonDocument? warnings = null;
        JsonDocument? quality = null;
        double? qualityScore = null;
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT t.id,t.status,t.warnings,t.quality_metadata,t.quality_score,s.id,s.ordinal,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label),s.text,s.confidence,s.words,COALESCE(s.segment_kind,'SPEECH'),COALESCE(s.is_hidden,false) FROM transcripts t LEFT JOIN transcript_segments s ON s.transcript_id=t.id LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id WHERE t.meeting_id=@meeting AND t.version=(SELECT MAX(version) FROM transcripts WHERE meeting_id=@meeting) AND COALESCE(s.is_hidden,false)=false ORDER BY s.ordinal", connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            transcriptId = reader.GetGuid(0);
            status = reader.GetString(1);
            warnings ??= reader.IsDBNull(2) ? null : reader.GetFieldValue<JsonDocument>(2);
            quality ??= reader.IsDBNull(3) ? null : reader.GetFieldValue<JsonDocument>(3);
            qualityScore ??= reader.IsDBNull(4) ? null : Convert.ToDouble(reader.GetValue(4));
            if (!reader.IsDBNull(5))
                segments.Add(new TranscriptSegmentRow(reader.GetGuid(5), reader.GetInt32(6), reader.GetInt64(7), reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetDouble(11), reader.IsDBNull(12) ? null : reader.GetFieldValue<JsonDocument>(12), reader.GetString(13), reader.GetBoolean(14)));
        }
        return new TranscriptRow(transcriptId, meetingId, status, segments, string.Equals(status, "PARTIAL_READY", StringComparison.OrdinalIgnoreCase), warnings, quality, qualityScore);
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

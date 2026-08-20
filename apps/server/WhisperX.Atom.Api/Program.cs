using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<UnifiedProductStore>();
builder.Services.AddHostedService<OperationalRecoveryService>();
builder.Services.AddHttpClient("nats-readiness", client => client.Timeout = TimeSpan.FromSeconds(2));
static bool IsUnsafeSecret(string? value) => string.IsNullOrWhiteSpace(value)
    || value.StartsWith("generate-", StringComparison.OrdinalIgnoreCase)
    || value.StartsWith("replace-with", StringComparison.OrdinalIgnoreCase)
    || value.StartsWith("change-me", StringComparison.OrdinalIgnoreCase)
    || value.Equals("password", StringComparison.OrdinalIgnoreCase)
    || value.Equals("changeme", StringComparison.OrdinalIgnoreCase);

static bool IsPrivateLanOrigin(string? value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.IsLoopback || uri.Port <= 0)
        return false;
    if (!System.Net.IPAddress.TryParse(uri.Host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        return false;
    var bytes = address.GetAddressBytes();
    return bytes[0] == 10
        || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        || (bytes[0] == 192 && bytes[1] == 168);
}

if (builder.Environment.IsProduction())
{
    if (string.Equals(builder.Configuration["COOKIE_SECURE"], "false", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("PRODUCTION_COOKIE_SECURE_REQUIRED");
    foreach (var name in new[] { "POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN", "AGENT_ENROLLMENT_SECRET" })
        if (IsUnsafeSecret(builder.Configuration[name])) throw new InvalidOperationException($"PRODUCTION_SECRET_INVALID:{name}");
}
else if (builder.Environment.IsEnvironment("Lan"))
{
    if (!string.Equals(builder.Configuration["COOKIE_SECURE"], "false", StringComparison.OrdinalIgnoreCase)
        || !string.Equals(builder.Configuration["ALLOW_INSECURE_LAN_HTTP"], "true", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("LAN_HTTP_EXPLICIT_REQUIRED");
    if (!IsPrivateLanOrigin(builder.Configuration["SERVER_ORIGIN"]))
        throw new InvalidOperationException("LAN_SERVER_ORIGIN_INVALID");
    foreach (var name in new[] { "POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN", "AGENT_ENROLLMENT_SECRET" })
    {
        var value = builder.Configuration[name];
        if (IsUnsafeSecret(value) || value!.Length < 16) throw new InvalidOperationException($"LAN_SECRET_INVALID:{name}");
    }
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
            path.StartsWith("/api/assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" :
            path.StartsWith("/api/client-updates", StringComparison.OrdinalIgnoreCase) ? "client-updates" : null;
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
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "INTERNAL_SERVER_ERROR",
                retryable = true,
                traceId
            });
        }
        else
        {
            throw;
        }
    }
    finally
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var routineProbe = path is "/ready" or "/health"
            || (path.StartsWith("/api/v1/agents/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase));
        if (routineProbe && context.Response.StatusCode < 400)
            app.Logger.LogDebug(
                "http_request trace_id={TraceId} method={Method} path={Path} status_code={StatusCode} elapsed_ms={ElapsedMs}",
                traceId, context.Request.Method, path, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        else
            app.Logger.LogInformation(
                "http_request trace_id={TraceId} method={Method} path={Path} status_code={StatusCode} elapsed_ms={ElapsedMs}",
                traceId, context.Request.Method, path, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
});

var db = app.Services.GetRequiredService<Database>();
var unified = app.Services.GetRequiredService<UnifiedProductStore>();
await db.InitializeAsync();
// Release orchestration can run additive migrations as an isolated one-shot
// container before replacing the API. This path deliberately performs no
// HTTP serving and therefore cannot expose a half-started runtime.
if (string.Equals(builder.Configuration["WHISPERX_MIGRATION_ONLY"], "true", StringComparison.OrdinalIgnoreCase))
{
    app.Logger.LogInformation("WHISPERX_MIGRATION_ONLY completed with build identity {BuildIdentity}", builder.Configuration["WHISPERX_BUILD_IDENTITY"] ?? builder.Configuration["WHISPERX_RELEASE_VERSION"] ?? "dev");
    return;
}

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
            await context.Response.WriteAsJsonAsync(new { error = "AUTH_REJECTED" });
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

    if (context.Request.Path.StartsWithSegments("/api/auth/change-password"))
    {
        // This endpoint is authenticated below and is also allowed for Reader
        // users who have been provisioned with a temporary password.
    }

    if (context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/ready") ||
        context.Request.Path.StartsWithSegments("/api/system/version") ||
        context.Request.Path.StartsWithSegments("/api/client-updates") ||
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
    if (user.MustChangePassword && !context.Request.Path.StartsWithSegments("/api/auth/change-password")
        && !context.Request.Path.StartsWithSegments("/api/auth/me")
        && !context.Request.Path.StartsWithSegments("/api/auth/logout"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "PASSWORD_CHANGE_REQUIRED" });
        return;
    }
    if (!RoleAllows(user.Role, context.Request.Method, context.Request.Path)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; await context.Response.WriteAsJsonAsync(new { error = "insufficient_role" }); return; }
    context.Items["user_id"] = user.Id;
    context.Items["user_role"] = user.Role;

    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { ok = true, service = "whisperx-atom-api", serverTimeUtc = DateTimeOffset.UtcNow }));
app.MapGet("/health", () => Results.Ok(new { ok = true, service = "whisperx-atom-api", serverTimeUtc = DateTimeOffset.UtcNow }));
app.MapGet("/health/ready", async (IHttpClientFactory httpClientFactory) =>
{
    var postgres = true;
    var nats = true;
    var storage = true;
    try { await db.PingAsync(); } catch { postgres = false; }
    try
    {
        var natsUrl = builder.Configuration["NATS_MONITORING_URL"] ?? "http://nats:8222/healthz";
        var http = httpClientFactory.CreateClient("nats-readiness");
        nats = (await http.GetAsync(natsUrl)).IsSuccessStatusCode;
    }
    catch { nats = false; }
    try
    {
        var root = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "/data";
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, $".ready-probe-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(probe, "ready");
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }
    catch { storage = false; }
    var ready = postgres && nats && storage;
    return Results.Json(new { ready, postgres, nats, storage, serverTimeUtc = DateTimeOffset.UtcNow }, statusCode: ready ? 200 : 503);
});
app.MapGet("/ready", async () =>
{
    try
    {
        await db.PingAsync();
        return Results.Ok(new { ready = true, postgres = true, serverTimeUtc = DateTimeOffset.UtcNow });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Readiness check failed");
        return Results.Json(new { ready = false, postgres = false, serverTimeUtc = DateTimeOffset.UtcNow }, statusCode: 503);
    }
});

app.MapGet("/api/system/version", (IConfiguration configuration) => Results.Ok(new
{
    product = "WhisperX Atom",
    apiVersion = 1,
    releaseVersion = configuration["WHISPERX_RELEASE_VERSION"] ?? "dev",
    buildIdentity = configuration["WHISPERX_BUILD_IDENTITY"] ?? configuration["WHISPERX_RELEASE_VERSION"] ?? "dev",
    minDesktopVersion = configuration["WHISPERX_MIN_DESKTOP_VERSION"] ?? "0.1.0",
    minRecorderVersion = configuration["WHISPERX_MIN_RECORDER_VERSION"] ?? "0.1.0",
    serverTimeUtc = DateTimeOffset.UtcNow
}));

// Client update metadata and packages are intentionally anonymous.  A client
// may need to update before it can authenticate, while SHA256 and
// Authenticode validation remain mandatory on the Desktop side.  The update
// root is a read-only bind mount in release Compose and is never shared with
// media, spool or credentials.
app.MapGet("/api/client-updates/latest", (HttpRequest request, IConfiguration configuration) =>
{
    var root = GetClientUpdateRoot(configuration);
    var manifestPath = Path.Combine(root, "client-update.json");
    if (!File.Exists(manifestPath)) return Results.NoContent();

    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object
            || !string.Equals(manifest.TryGetString("product"), "WhisperX Atom", StringComparison.Ordinal))
            return Results.NoContent();

        var requestedChannel = request.Query["channel"].ToString();
        var channel = manifest.TryGetString("channel") ?? "stable";
        if (!string.IsNullOrWhiteSpace(requestedChannel)
            && !string.Equals(requestedChannel, channel, StringComparison.OrdinalIgnoreCase))
            return Results.NoContent();

        var currentVersion = request.Query["currentVersion"].ToString();
        var currentBuild = request.Query["currentBuildIdentity"].ToString();
        var manifestVersion = manifest.TryGetString("version");
        var manifestBuild = manifest.TryGetString("buildIdentity");
        if (!IsClientUpdateCandidate(channel, currentVersion, currentBuild, manifestVersion, manifestBuild))
            return Results.NoContent();

        if (!manifest.TryGetProperty("package", out var package)
            || package.ValueKind != JsonValueKind.Object
            || !IsSha256(package.TryGetString("packageId"))
            || !IsSha256(package.TryGetString("sha256"))
            || !string.Equals(package.TryGetString("packageId"), package.TryGetString("sha256"), StringComparison.OrdinalIgnoreCase))
            return Results.NoContent();

        var packageId = package.TryGetString("packageId")!;
        var packageFile = Path.Combine(root, "packages", packageId + ".exe");
        if (!File.Exists(packageFile)) return Results.NoContent();
        return Results.Json(manifest.Clone(), statusCode: StatusCodes.Status200OK);
    }
    catch (JsonException) { return Results.NoContent(); }
    catch (IOException) { return Results.NoContent(); }
});

app.MapGet("/api/client-updates/packages/{packageId}", (string packageId, HttpResponse response, IConfiguration configuration) =>
{
    if (!IsSha256(packageId)) return Results.NotFound();
    var root = GetClientUpdateRoot(configuration);
    var manifestPath = Path.Combine(root, "client-update.json");
    if (!File.Exists(manifestPath)) return Results.NotFound();

    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (!manifest.TryGetProperty("package", out var package)
            || !string.Equals(package.TryGetString("packageId"), packageId, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();
        var fileName = package.TryGetString("fileName");
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            return Results.NotFound();

        var path = Path.Combine(root, "packages", packageId + ".exe");
        if (!File.Exists(path)) return Results.NotFound();
        var etag = $"\"{packageId.ToLowerInvariant()}\"";
        response.Headers.ETag = etag;
        response.Headers.CacheControl = "no-cache";
        if (string.Equals(response.HttpContext.Request.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.File(path, "application/vnd.microsoft.portable-executable", fileName, enableRangeProcessing: true);
    }
    catch (JsonException) { return Results.NotFound(); }
    catch (IOException) { return Results.NotFound(); }
});

app.MapGet("/api/system/readiness", async (UnifiedProductStore store, IConfiguration configuration, IHttpClientFactory httpClientFactory) =>
{
    var checkedAt = DateTimeOffset.UtcNow;
    var expectedBuildIdentity = configuration["WHISPERX_BUILD_IDENTITY"] ?? configuration["WHISPERX_RELEASE_VERSION"] ?? "dev";
    var releaseIdentityValid = !string.IsNullOrWhiteSpace(expectedBuildIdentity)
        && !expectedBuildIdentity.Contains("dev", StringComparison.OrdinalIgnoreCase)
        && !expectedBuildIdentity.Contains("dirty", StringComparison.OrdinalIgnoreCase)
        && expectedBuildIdentity.Contains('+', StringComparison.Ordinal)
        && expectedBuildIdentity[(expectedBuildIdentity.IndexOf('+') + 1)..].Length >= 40;
    var mediaRoot = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "/data";
    // Match /health/ready: a bind mount may be created lazily by Docker, so a
    // Directory.Exists-only check can report a false storage outage. Probe the
    // actual write/delete path used by media ingestion instead.
    var storage = false;
    try
    {
        Directory.CreateDirectory(mediaRoot);
        var probe = Path.Combine(mediaRoot, $".readiness-probe-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(probe, "ready");
        File.Delete(probe);
        storage = true;
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "System readiness media storage probe failed");
    }
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
        var http = httpClientFactory.CreateClient("nats-readiness");
        using var response = await http.GetAsync(natsUrl);
        nats = response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        app.Logger.LogDebug(ex, "System readiness NATS check failed");
    }

    IReadOnlyList<WorkerRuntimeRow> workers = Array.Empty<WorkerRuntimeRow>();
    OperationsSnapshot? operations = null;
    if (postgres)
    {
        try
        {
            workers = await store.ListWorkerRuntimeAsync();
            operations = await store.GetOperationsSnapshotAsync();
        }
        catch (Exception ex)
        {
            postgres = false;
            app.Logger.LogWarning(ex, "System readiness worker or operations query failed");
        }
    }
    var fresh = workers
        .GroupBy(item => item.WorkerName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastSeenAt).First(), StringComparer.OrdinalIgnoreCase);
    static bool IsActiveWorker(WorkerRuntimeRow worker) =>
        string.Equals(worker.Status, "READY", StringComparison.OrdinalIgnoreCase)
        || string.Equals(worker.Status, "BUSY", StringComparison.OrdinalIgnoreCase)
        || string.Equals(worker.Status, "DEGRADED", StringComparison.OrdinalIgnoreCase);
    static bool IsFreshWorker(WorkerRuntimeRow worker, DateTimeOffset now)
    {
        var age = now.UtcDateTime - worker.LastSeenAt.ToUniversalTime();
        return age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(60);
    }
    static bool IsIdentityMatch(WorkerRuntimeRow worker, string expected)
        => string.Equals(expected, "dev", StringComparison.OrdinalIgnoreCase)
            || string.Equals(worker.Version, expected, StringComparison.Ordinal);
    // Core media processing is always required. The LLM worker is required
    // only when either assistant answers or automatic summaries are enabled.
    // Keeping that distinction here prevents an intentionally disabled
    // summary worker from making recording/ASR readiness look unhealthy.
    var autoSummaryEnabled = string.Equals(configuration["AUTO_SUMMARY_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
    var assistantEnabled = configuration.GetValue("ASSISTANT_ENABLED", true);
    var qwenEnabled = autoSummaryEnabled || assistantEnabled;
    var qwenMode = autoSummaryEnabled
        ? "AUTO_SUMMARY_ENABLED"
        : assistantEnabled ? "ASSISTANT_ONLY" : "DISABLED";
    var requiredWorkerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "outbox-relay", "import-worker", "media-worker", "gpu-worker"
    };
    if (qwenEnabled) requiredWorkerNames.Add("summary-worker");

    var workerReady = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    var identityMismatch = false;
    foreach (var name in new[] { "outbox-relay", "import-worker", "media-worker", "gpu-worker", "summary-worker" })
    {
        var required = requiredWorkerNames.Contains(name);
        if (!fresh.TryGetValue(name, out var item) || !IsFreshWorker(item, checkedAt))
        {
            workerReady[name] = new { status = "UNAVAILABLE", required, lastSeenAt = fresh.TryGetValue(name, out var stale) ? stale.LastSeenAt : (DateTime?)null };
            continue;
        }
        var matches = IsIdentityMatch(item, expectedBuildIdentity);
        identityMismatch |= required && !matches;
        workerReady[name] = new { status = matches ? item.Status : "IDENTITY_MISMATCH", required, lastSeenAt = item.LastSeenAt, version = item.Version, expectedBuildIdentity, currentJobId = item.CurrentJobId, capabilities = item.Capabilities, lastErrorCode = matches ? item.LastErrorCode : "WORKER_BUILD_IDENTITY_MISMATCH" };
    }

    var gpu = fresh.TryGetValue("gpu-worker", out var gpuWorker)
        && IsFreshWorker(gpuWorker, checkedAt)
        && IsActiveWorker(gpuWorker);
    var gpuCapabilities = gpuWorker?.Capabilities.RootElement;
    var cuda = gpu && gpuCapabilities.HasValue && gpuCapabilities.Value.TryGetProperty("cudaAvailable", out var cudaValue) && cudaValue.ValueKind == JsonValueKind.True;
    var hf = gpu && gpuCapabilities.HasValue && gpuCapabilities.Value.TryGetProperty("diarization", out var diarizationValue)
        ? diarizationValue.GetString() ?? "DEGRADED"
        : "DEGRADED";
    var requiredWorkersReady = requiredWorkerNames.All(name =>
        fresh.TryGetValue(name, out var worker) &&
        IsFreshWorker(worker, checkedAt) &&
        IsActiveWorker(worker) &&
        IsIdentityMatch(worker, expectedBuildIdentity));
    object qwen;
    if (!qwenEnabled)
    {
        qwen = new { status = "DISABLED", reason = "assistant_and_auto_summary_disabled", mode = qwenMode };
    }
    else if (!nats)
    {
        qwen = new { status = "DEGRADED", reason = "nats_unavailable", mode = qwenMode };
    }
    else if (!fresh.TryGetValue("summary-worker", out var summaryWorker) || !IsFreshWorker(summaryWorker, checkedAt))
    {
        qwen = new { status = "DEGRADED", reason = "summary_worker_stale", mode = qwenMode };
    }
    else if (!IsIdentityMatch(summaryWorker, expectedBuildIdentity))
    {
        qwen = new { status = "DEGRADED", reason = "summary_worker_identity_mismatch", mode = qwenMode };
    }
    else
    {
        var capabilities = summaryWorker.Capabilities.RootElement;
        var modelAvailable = capabilities.TryGetProperty("modelAvailable", out var model) && model.ValueKind == JsonValueKind.True;
        var manifestAvailable = capabilities.TryGetProperty("modelManifestAvailable", out var manifest) && manifest.ValueKind == JsonValueKind.True;
        var manifestValid = capabilities.TryGetProperty("modelManifestValid", out var validManifest) && validManifest.ValueKind == JsonValueKind.True;
        var llamaAvailable = capabilities.TryGetProperty("llamaRuntimeAvailable", out var llama) && llama.ValueKind == JsonValueKind.True;
        var gpuBusy = gpuWorker?.CurrentJobId is not null || string.Equals(gpuWorker?.Status, "BUSY", StringComparison.OrdinalIgnoreCase);
        var summaryBusy = summaryWorker.CurrentJobId is not null || string.Equals(summaryWorker.Status, "BUSY", StringComparison.OrdinalIgnoreCase);
        qwen = !modelAvailable ? new { status = "UNAVAILABLE", reason = "model_missing", mode = qwenMode }
            : !manifestAvailable ? new { status = "DEGRADED", reason = "model_manifest_missing", mode = qwenMode }
            : !manifestValid ? new { status = "UNAVAILABLE", reason = "model_manifest_mismatch", mode = qwenMode }
            : !llamaAvailable ? new { status = "UNAVAILABLE", reason = "llama_runtime_missing", mode = qwenMode }
            : !IsActiveWorker(summaryWorker) ? new { status = "DEGRADED", reason = string.Equals(summaryWorker.Status, "STARTING", StringComparison.OrdinalIgnoreCase) ? "summary_worker_starting" : "summary_worker_not_ready", mode = qwenMode }
            : string.Equals(summaryWorker.Status, "UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ? new { status = "UNAVAILABLE", reason = summaryWorker.LastErrorCode ?? "summary_worker_unavailable", mode = qwenMode }
            : string.Equals(summaryWorker.Status, "DEGRADED", StringComparison.OrdinalIgnoreCase) ? new { status = "DEGRADED", reason = summaryWorker.LastErrorCode ?? "summary_worker_degraded", mode = qwenMode }
            : summaryBusy || gpuBusy ? new { status = "BUSY", reason = summaryBusy ? "summary_processing" : "gpu_lease_busy", mode = qwenMode }
            : new { status = "READY", reason = "summary_worker_ready", mode = qwenMode };
    }
    var gpuWorkerFailed = gpuWorker is null
        || string.Equals(gpuWorker.Status, "FAILED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(gpuWorker.Status, "UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
    var gpuStatus = !cuda || gpuWorkerFailed
        ? "UNAVAILABLE"
        : gpuWorker?.CurrentJobId is not null || string.Equals(gpuWorker?.Status, "BUSY", StringComparison.OrdinalIgnoreCase)
            ? "BUSY"
            : "READY";
    var ready = postgres && nats && storage && requiredWorkersReady && cuda && !identityMismatch && releaseIdentityValid;

    return Results.Ok(new
    {
        ready,
        buildIdentity = expectedBuildIdentity,
        releaseIdentityValid,
        identityMismatch,
        checkedAt,
        components = new
        {
            gateway = new { status = "READY", checkedAt },
            postgres = new { status = postgres ? "READY" : "UNAVAILABLE" },
            nats = new { status = nats ? "READY" : "UNAVAILABLE" },
            storage = new { status = storage ? "READY" : "UNAVAILABLE", root = mediaRoot },
            recordingIngress = new { status = postgres && storage ? "READY" : "UNAVAILABLE", database = postgres ? "READY" : "UNAVAILABLE", storage = storage ? "READY" : "UNAVAILABLE" },
            workers = workerReady,
            cuda = new { status = gpuStatus },
            whisperx = new { status = requiredWorkersReady && cuda ? "READY" : "UNAVAILABLE", gpu = gpuStatus },
            hfDiarization = new { status = hf },
            recorder = new { status = "OPTIONAL" },
            qwen
        },
        queue = operations is null ? null : new
        {
            queuedJobs = operations.QueuedJobs,
            runningJobs = operations.RunningJobs,
            staleLeases = operations.StaleLeases,
            pendingOutbox = operations.PendingOutbox,
            activeGpuJobs = operations.ActiveGpuJobs,
            failedJobs24h = operations.FailedJobs24h,
            staleRecordingSessions = operations.StaleRecordingSessions
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
    var recordingTracks = await store.ListRecordingTracksAsync(id);
    var recordingCorrelation = await store.ListRecordingCorrelationAsync(id);
    var transcript = await db.GetTranscriptAsync(id);
    var summary = await store.GetLatestSummaryAsync(id);
    var traceId = context.Request.Headers["X-Trace-Id"].ToString();
    return Results.Ok(new
    {
        meeting = new { meeting.Id, meeting.Status, meeting.CreatedAt },
        media = media.Select(item => new { item.Id, item.Status, item.DurationMs, item.SizeBytes }),
        recordingTracks = recordingTracks.Select(track => new { track.Id, track.SessionId, track.TrackType, track.DeviceId, track.DeviceName, track.SelectionMode, track.RecordingProfile, track.SampleRate, track.Channels, track.Encoding, track.BitsPerSample, track.SourceEncoding, track.SourceSubFormat, track.ValidBitsPerSample }),
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
            localServerSessions = recordingCorrelation.Select(item => new { item.LocalSessionId, serverSessionId = item.ServerSessionId, item.PipelineCorrelationId }),
            mediaAssetIds = media.Select(item => item.Id),
            processingJobIds = jobs.Select(item => item.Id),
            transcriptId = transcript?.Id,
            summaryId = summary?.Id,
            requestTraceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId
        },
        timings = recordingCorrelation.Select(item => new { serverSessionId = item.ServerSessionId, item.PipelineCorrelationId, stages = item.Timings.RootElement })
    });
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, IConfiguration configuration) =>
{
    var username = request.Username?.Trim();
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.Json(new { error = "INVALID_LOGIN_REQUEST", retryable = false }, statusCode: StatusCodes.Status400BadRequest);

    var user = await db.FindUserAsync(username);
    if (user is null || !PasswordService.Verify(request.Password, user.PasswordHash))
        return Results.Json(new { error = "INVALID_CREDENTIALS", retryable = false }, statusCode: StatusCodes.Status401Unauthorized);

    await IssueAuthCookiesAsync(http, configuration, user);
    return Results.Ok(new { user = new { id = user.Id, username = user.Username, role = user.Role, mustChangePassword = user.MustChangePassword } });
});

app.MapPost("/api/auth/change-password", async (ChangePasswordRequest request, HttpContext http, IConfiguration configuration) =>
{
    if (CurrentUserId(http) is not Guid userId) return Results.Unauthorized();
    var currentSession = http.Request.Cookies["wa_session"];
    if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 12)
        return Results.BadRequest(new { error = "password_minimum_length_12" });
    if (!await db.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, currentSession))
        return Results.BadRequest(new { error = "password_change_rejected" });
    var user = await db.GetUserForSessionAsync(currentSession ?? string.Empty);
    if (user is null) return Results.Unauthorized();
    await IssueAuthCookiesAsync(http, configuration, user);
    if (!string.IsNullOrWhiteSpace(currentSession)) await db.RevokeSessionAsync(currentSession);
    return Results.Ok(new { changed = true, user = new { id = user.Id, username = user.Username, role = user.Role, mustChangePassword = false } });
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
        : Results.Ok(new { id = user.Id, username = user.Username, role = user.Role, mustChangePassword = user.MustChangePassword });
});

app.MapGet("/api/admin/users", async (HttpContext context) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    return Results.Ok(await db.ListUsersAsync());
});

app.MapPost("/api/admin/users", async (AdminUserCreateRequest request, HttpContext context) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    var username = request.Username?.Trim();
    var role = request.Role?.Trim();
    if (string.IsNullOrWhiteSpace(username) || username.Length > 160 || role is not ("Administrator" or "Operator" or "Editor" or "Reader"))
        return Results.BadRequest(new { error = "invalid_user" });
    var temporaryPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    var user = await db.CreateUserAsync(username, role, temporaryPassword);
    return user is null ? Results.Conflict(new { error = "user_exists" }) : Results.Created($"/api/admin/users/{user.Id}", new { user, temporaryPassword });
});

app.MapPatch("/api/admin/users/{userId:guid}", async (Guid userId, AdminUserUpdateRequest request, HttpContext context) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    if (request.Role is not null && request.Role is not ("Administrator" or "Operator" or "Editor" or "Reader"))
        return Results.BadRequest(new { error = "invalid_role" });
    return await db.UpdateUserAsync(userId, request.Role, request.IsActive) ? Results.Ok(new { updated = true }) : Results.NotFound();
});

app.MapPost("/api/admin/users/{userId:guid}/reset-password", async (Guid userId, HttpContext context) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    var temporaryPassword = await db.ResetPasswordAsync(userId);
    return temporaryPassword is null ? Results.NotFound() : Results.Ok(new { userId, temporaryPassword });
});

app.MapPost("/api/admin/users/{userId:guid}/disable", async (Guid userId, HttpContext context) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    var updated = await db.UpdateUserAsync(userId, null, false);
    if (updated) await db.RevokeUserSessionsAsync(userId);
    return updated ? Results.Ok(new { userId, isActive = false }) : Results.NotFound();
});

app.MapPost("/api/admin/users/{userId:guid}/enable", async (Guid userId, HttpContext context) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    return await db.UpdateUserAsync(userId, null, true) ? Results.Ok(new { userId, isActive = true }) : Results.NotFound();
});

app.MapPost("/api/admin/users/{userId:guid}/revoke-sessions", async (Guid userId, HttpContext context) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    return await db.RevokeUserSessionsAsync(userId) ? Results.Ok(new { userId, revoked = true }) : Results.NotFound();
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

app.MapGet("/api/transcripts", async (int? limit, int? offset, string? search, string? status, DateTime? dateFrom, DateTime? dateTo, HttpContext context) =>
{
    if (CurrentUserId(context) is not Guid userId) return Results.Unauthorized();
    if (search?.Length > 200 || status?.Length > 80) return Results.BadRequest(new { error = "invalid_registry_filter" });
    var rows = await db.ListTranscriptRegistryAsync(
        Math.Clamp(limit ?? 50, 1, 200), Math.Max(offset ?? 0, 0), search?.Trim(), status?.Trim(), dateFrom, dateTo, userId, IsPrivileged(context));
    return Results.Ok(rows);
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
    if (!MediaPolicy.IsAllowedExtension(request.OriginalName) || string.IsNullOrWhiteSpace(request.SourceType) || request.SourceType.Length > 80 || !System.Text.RegularExpressions.Regex.IsMatch(request.Sha256 ?? string.Empty, "^[0-9a-fA-F]{64}$") || request.SizeBytes <= 0 || request.SizeBytes > 8L * 1024 * 1024 * 1024)
        return Results.BadRequest(new { error = "unsupported_or_oversized_audio" });
    string sourcePath;
    try { sourcePath = StorageHelpers.StoragePath(request.StorageKey); }
    catch (InvalidOperationException) { return Results.BadRequest(new { error = "invalid_import_storage_key" }); }
    if (!File.Exists(sourcePath)) return Results.Conflict(new { error = "import_file_not_ready" });
    var actualSize = new FileInfo(sourcePath).Length;
    if (actualSize != request.SizeBytes) return Results.BadRequest(new { error = "import_size_mismatch" });
    var actualSha256 = await StorageHelpers.ComputeSha256Async(sourcePath);
    if (!string.Equals(actualSha256, request.Sha256, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "import_checksum_mismatch" });
    var job = await db.RegisterImportAsync(request);
    return Results.Accepted("/api/jobs/" + job.Id, job);
});

static bool AgentMatches(HttpContext context, Guid agentId) => context.Items.TryGetValue("agent_id", out var item) && item is Guid authenticated && authenticated == agentId;

static Guid? CurrentUserId(HttpContext context) => context.Items.TryGetValue("user_id", out var item) && item is Guid userId ? userId : null;

static bool IsPrivileged(HttpContext context) => context.Items.TryGetValue("user_role", out var item) && item is string role &&
    (string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Operator", StringComparison.OrdinalIgnoreCase));

static bool IsAdministrator(HttpContext context) => context.Items.TryGetValue("user_role", out var item) && item is string role &&
    string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase);

static bool IsValidEmbeddingJson(JsonDocument? document)
{
    if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        return document is null;
    var length = document.RootElement.GetArrayLength();
    if (length is < 1 or > 2048) return false;
    foreach (var item in document.RootElement.EnumerateArray())
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var value) || double.IsNaN(value) || double.IsInfinity(value)) return false;
    return true;
}

async Task<bool> CanAccessMeetingAsync(HttpContext context, Guid meetingId)
{
    // Assistant access is always evaluated against the authenticated Desktop
    // user. The legacy Voice Host token is intentionally not an ownership
    // bypass; managed Voice Host requests arrive through the Desktop broker.
    // context.Items.ContainsKey("voice_host") is therefore diagnostic-only,
    // never an authorization decision.
    if (IsPrivileged(context)) return true;
    var userId = CurrentUserId(context);
    return userId.HasValue && await db.UserOwnsMeetingAsync(meetingId, userId.Value);
}

static bool RoleAllows(string role, string method, PathString path)
{
    if (string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Operator", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.StartsWithSegments("/api/auth/change-password")) return true;
    if (path.StartsWithSegments("/api/agents/bootstrap")) return HttpMethods.IsPost(method);
    if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) return true;
    if (!string.Equals(role, "Editor", StringComparison.OrdinalIgnoreCase)) return false;
    if (HttpMethods.IsPatch(method)) return true;
    if (HttpMethods.IsDelete(method) && path.StartsWithSegments("/api/assistant/conversations")) return true;
    return HttpMethods.IsPost(method) &&
        (path == "/api/meetings" ||
         (path.StartsWithSegments("/api/meetings/") && path.Value?.EndsWith("/uploads", StringComparison.OrdinalIgnoreCase) == true) ||
         path.StartsWithSegments("/api/assistant/queries") || path.StartsWithSegments("/api/assistant/requests") || path.StartsWithSegments("/api/assistant/conversations") ||
         path.StartsWithSegments("/api/assistant/live-segments") ||
         path == "/api/speaker-profiles" || path.StartsWithSegments("/api/speaker-profiles/") ||
         path.Value?.Contains("/speakers/merge", StringComparison.OrdinalIgnoreCase) == true ||
         path.Value?.EndsWith("/summary/rebuild", StringComparison.OrdinalIgnoreCase) == true ||
         path.Value?.EndsWith("/transcript/reprocess", StringComparison.OrdinalIgnoreCase) == true);
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

static string GetClientUpdateRoot(IConfiguration configuration)
{
    var configured = configuration["CLIENT_UPDATE_ROOT"];
    return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? "/updates" : configured);
}

static bool IsSha256(string? value)
    => value is { Length: 64 } && value.All(Uri.IsHexDigit);

static bool IsClientUpdateCandidate(string channel, string? currentVersion, string? currentBuildIdentity, string? candidateVersion, string? candidateBuildIdentity)
{
    if (string.IsNullOrWhiteSpace(candidateVersion) || string.IsNullOrWhiteSpace(candidateBuildIdentity)) return false;
    if (candidateBuildIdentity.Contains("dev", StringComparison.OrdinalIgnoreCase)
        || candidateBuildIdentity.Contains("dirty", StringComparison.OrdinalIgnoreCase)) return false;

    static bool TrySemanticVersion(string? value, out Version version)
    {
        var normalized = (value ?? "0.0.0").Trim();
        var separator = normalized.IndexOfAny(['+', '-']);
        if (separator >= 0) normalized = normalized[..separator];
        if (Version.TryParse(normalized, out var parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    if (!TrySemanticVersion(candidateVersion, out var candidate)
        || !TrySemanticVersion(currentVersion, out var current)) return false;

    var comparison = candidate.CompareTo(current);
    if (comparison > 0) return true;
    return comparison == 0
        && string.Equals(channel, "pilot", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(currentBuildIdentity, candidateBuildIdentity, StringComparison.Ordinal);
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

app.MapPost("/api/agents/{agentId:guid}/rotate-token", async (Guid agentId, HttpContext context, UnifiedProductStore store) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    return await store.ReplaceAgentTokenAsync(agentId, token)
        ? Results.Ok(new { agentId, token }) // plaintext is intentionally returned only by this one-time response.
        : Results.NotFound();
});

app.MapPost("/api/agents/{agentId:guid}/revoke", async (Guid agentId, HttpContext context, UnifiedProductStore store) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    return await store.RevokeAgentTokenAsync(agentId) ? Results.Ok(new { agentId, revoked = true }) : Results.NotFound();
});

app.MapPost("/api/agents/{agentId:guid}/reenroll", async (Guid agentId, HttpContext context, UnifiedProductStore store) =>
{
    if (!IsAdministrator(context)) return Results.Forbid();
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    return await store.ReplaceAgentTokenAsync(agentId, token)
        ? Results.Ok(new { agentId, token })
        : Results.NotFound();
});

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

app.MapPost("/api/agents/bootstrap", async (AgentBootstrapRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (CurrentUserId(context) is not Guid userId) return Results.Unauthorized();
    if (request.InstallationId == Guid.Empty) return Results.BadRequest(new { error = "installation_id_required" });
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var result = await store.BootstrapAgentAsync(request.InstallationId, userId, string.IsNullOrWhiteSpace(request.Name) ? "WhisperX Atom Recorder" : request.Name.Trim(), request.AgentId, token, request.Version ?? "0.1.0", request.Capabilities ?? JsonDocument.Parse("{}"));
    if (result is null) return Results.BadRequest(new { error = "agent_bootstrap_rejected" });
    if (result.ReenrollRequired)
        return Results.Conflict(new { error = "REENROLL_REQUIRED", state = "REENROLL_REQUIRED", linked = false, reenrollRequired = true, agentId = result.Agent.Id });
    return Results.Ok(new
    {
        state = result.Linked ? "AGENT_READY" : "AGENT_LINK_PENDING",
        agentId = result.Agent.Id,
        agent = result.Agent,
        linked = result.Linked,
        reenrollRequired = false,
        token = result.Token
    });
});

app.MapPost("/api/v1/recording-sessions", async (CreateRecordingSessionRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var correlationId = request.PipelineCorrelationId ?? context.Request.Headers["X-Correlation-Id"].ToString();
    var result = await store.CreateRecordingSessionWithResultAsync(request.MeetingId, agentId, request.OwnerUserId, request.Title, request.StartedAt, correlationId, request.LocalSessionId, request.AcousticProfile);
    if (result.Session is null)
    {
        var status = result.ErrorCode switch
        {
            "MEETING_NOT_FOUND" => StatusCodes.Status404NotFound,
            "OWNER_REQUIRED" => StatusCodes.Status422UnprocessableEntity,
            "AGENT_USER_LINK_REQUIRED" => StatusCodes.Status403Forbidden,
            "MEETING_OWNER_MISMATCH" or "MEETING_CANCELLED" or "MEETING_BINDING_CONFLICT" => StatusCodes.Status409Conflict,
            "AGENT_AUTH_REJECTED" => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        return Results.Json(new { error = result.ErrorCode ?? "SERVER_STORAGE_ERROR", retryable = result.Retryable, traceId = context.Response.Headers["X-Trace-Id"].ToString() }, statusCode: status);
    }
    var payload = new { result.Session.Id, result.Session.MeetingId, result.Session.AgentId, result.Session.State, result.Session.StartedAt, result.Session.FinishedAt, result.Session.PipelineCorrelationId, result.Session.LocalSessionId, created = result.Created };
    return result.Created
        ? Results.Created($"/api/v1/recording-sessions/{result.Session.Id}", payload)
        : Results.Ok(payload);
});

app.MapPost("/api/v1/recording-sessions/{sessionId:guid}/tracks", async (Guid sessionId, CreateTrackRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    if (request.TrackType is not ("room-microphone" or "system-audio"))
        return Results.BadRequest(new { error = "recording_track_type_invalid" });
    if (request.SampleRate is < 8000 or > 192000 || request.Channels is < 1 or > 8)
        return Results.BadRequest(new { error = "recording_track_format_invalid" });
    if (request.BitsPerSample is not null and not (8 or 16 or 24 or 32))
        return Results.BadRequest(new { error = "recording_track_bits_invalid" });
    if (request.ValidBitsPerSample is not null && (request.ValidBitsPerSample < 1 || request.ValidBitsPerSample > request.BitsPerSample.GetValueOrDefault(32)))
        return Results.BadRequest(new { error = "recording_track_valid_bits_invalid" });
    var track = await store.CreateRecordingTrackAsync(agentId, sessionId, request.TrackType, request.DeviceId, request.DeviceName, request.SelectionMode, request.RecordingProfile, request.SampleRate, request.Channels, request.Encoding, request.BitsPerSample, request.SourceEncoding, request.SourceSubFormat, request.ValidBitsPerSample);
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
        long size;
        await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            size = await CopyRequestBodyWithLimitAsync(request, output, 128L * 1024 * 1024, context.RequestAborted);
        if (size <= 0)
        {
            File.Delete(partPath);
            return Results.BadRequest(new { error = "chunk_empty" });
        }
        var sha = await StorageHelpers.ComputeSha256Async(partPath);
        var expectedSha = request.Headers["X-Chunk-SHA256"].ToString();
        if (!string.IsNullOrWhiteSpace(expectedSha) && !System.Text.RegularExpressions.Regex.IsMatch(expectedSha, "^[0-9a-fA-F]{64}$"))
        {
            File.Delete(partPath);
            return Results.BadRequest(new { error = "chunk_checksum_invalid" });
        }
        if (!string.IsNullOrWhiteSpace(expectedSha) && !string.Equals(expectedSha, sha, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            return Results.BadRequest(new { error = "chunk_checksum_mismatch" });
        }
        var startHeader = request.Headers["X-Start-Sample"].ToString();
        var countHeader = request.Headers["X-Sample-Count"].ToString();
        if ((!string.IsNullOrWhiteSpace(startHeader) && !long.TryParse(startHeader, out _))
            || (!string.IsNullOrWhiteSpace(countHeader) && !long.TryParse(countHeader, out _)))
        {
            File.Delete(partPath);
            return Results.BadRequest(new { error = "chunk_sample_metadata_invalid" });
        }
        var startSample = long.TryParse(startHeader, out var parsedStart) ? parsedStart : 0;
        var sampleCount = long.TryParse(countHeader, out var parsedCount) ? parsedCount : 0;
        // A non-empty FLAC without a positive sample timeline is not a valid
        // raw-first chunk.  Accepting an omitted/zero count lets a legacy or
        // malformed client reach finalize, where the failure becomes an
        // opaque media/timeline error instead of an actionable upload error.
        if (!long.TryParse(countHeader, out parsedCount) || parsedCount <= 0 || startSample < 0)
        {
            File.Delete(partPath);
            return Results.BadRequest(new { error = "chunk_sample_metadata_invalid" });
        }
        sampleCount = parsedCount;
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
    catch (RequestBodyTooLargeException)
    {
        if (File.Exists(partPath)) File.Delete(partPath);
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
    catch
    {
        if (File.Exists(partPath)) File.Delete(partPath);
        throw;
    }
});

static async Task<long> CopyRequestBodyWithLimitAsync(HttpRequest request, Stream destination, long maxBytes, CancellationToken cancellationToken)
{
    var buffer = new byte[1024 * 1024];
    long total = 0;
    while (true)
    {
        var read = await request.Body.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (read == 0) break;
        total += read;
        if (total > maxBytes) throw new RequestBodyTooLargeException();
        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
    }
    await destination.FlushAsync(cancellationToken);
    return total;
}

app.MapGet("/api/v1/recording-sessions/{sessionId:guid}/tracks/{trackId:guid}/missing-chunks", async (Guid sessionId, Guid trackId, int expectedCount, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var missing = await store.MissingChunksAsync(agentId, sessionId, trackId, Math.Clamp(expectedCount, 0, 100000));
    return missing is null
        ? Results.NotFound(new { error = "RECORDING_SESSION_NOT_FOUND", retryable = false })
        : Results.Ok(new { missing });
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

app.MapGet("/api/v1/recording-sessions/{sessionId:guid}/pipeline", async (Guid sessionId, HttpContext context, UnifiedProductStore store) =>
{
    if (!context.Items.TryGetValue("agent_id", out var item) || item is not Guid agentId) return Results.Unauthorized();
    var chain = await store.GetRecordingPipelineChainAsync(agentId, sessionId);
    return chain is null
        ? Results.NotFound(new { error = "recording_pipeline_not_found" })
        : Results.Ok(chain);
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
app.MapGet("/api/meetings/{id:guid}/pipeline", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    return Results.Ok(await store.GetMeetingPipelineChainsAsync(id));
});
app.MapPost("/api/meetings/{id:guid}/summary/rebuild", async (Guid id, SummaryRebuildRequest? request, HttpContext context, UnifiedProductStore store, Database database) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    if (request?.Profile is { Length: > 40 } || request?.PromptVersion is { Length: > 120 } || request?.Reason is { Length: > 500 })
        return Results.BadRequest(new { error = "summary_options_too_long" });
    var eligibility = await store.GetSummaryEligibilityAsync(id, request?.TranscriptVersion);
    if (!eligibility.HasTranscript) return Results.Conflict(new { error = "transcript_required" });
    if (!eligibility.Allowed)
        return Results.Conflict(new { error = "SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY", reason = eligibility.Reason });
    var jobId = await store.QueueSummaryAsync(id, CurrentUserId(context), request);
    if (jobId is null) return Results.Conflict(new { error = "transcript_required" });

    // The Desktop job tracker requires the same durable contract as transcript
    // reprocessing.  Returning only { jobId } made it treat a queued summary
    // as an incomplete object and never observe its terminal state.
    var job = await database.GetJobAsync(jobId.Value);
    return job is null
        ? Results.Conflict(new { error = "summary_job_not_found" })
        : Results.Accepted($"/api/jobs/{job.Id}", job);
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
// Paginated registries avoid walking every meeting and issuing one detail
// request per row.  The store applies the user scope before filtering/counting.
app.MapGet("/api/summaries", async (int? page, int? pageSize, string? search, string? status, Guid? meetingId, string? sort, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context); if (userId is null) return Results.Unauthorized();
    return Results.Ok(await store.ListSummaryRegistryPageAsync(Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 200), search, status, meetingId, sort, userId.Value, IsPrivileged(context)));
});
app.MapGet("/api/speakers", async (int? page, int? pageSize, string? search, string? status, Guid? meetingId, string? sort, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context); if (userId is null) return Results.Unauthorized();
    return Results.Ok(await store.ListSpeakerRegistryPageAsync(Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 200), search, status, meetingId, sort, userId.Value, IsPrivileged(context)));
});
app.MapGet("/api/speaker-profiles", async (int? page, int? pageSize, string? search, string? status, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context); if (userId is null) return Results.Unauthorized();
    return Results.Ok(await store.ListSpeakerProfilesAsync(Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 200), search, status, userId.Value, IsPrivileged(context)));
});
app.MapPost("/api/speaker-profiles", async (SpeakerProfileCreateRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context); if (userId is null) return Results.Unauthorized();
    if (!IsValidEmbeddingJson(request.EmbeddingCentroid)) return Results.BadRequest(new { error = "invalid_speaker_embedding" });
    var profile = await store.CreateSpeakerProfileAsync(userId.Value, request.DisplayName ?? string.Empty, request.EmbeddingCentroid, request.EmbeddingModel);
    return profile is null ? Results.BadRequest(new { error = "invalid_speaker_profile" }) : Results.Created($"/api/speaker-profiles/{profile.Id}", profile);
});
app.MapPatch("/api/speaker-profiles/{id:guid}", async (Guid id, SpeakerProfileRenameRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context); if (userId is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.DisplayName)) return Results.BadRequest(new { error = "display_name_required" });
    var updated = await store.RenameSpeakerProfileAsync(id, userId.Value, IsPrivileged(context), request.DisplayName);
    return updated ? Results.Ok(new { ok = true }) : Results.NotFound();
});
app.MapPost("/api/speaker-profiles/{id:guid}/enroll", async (Guid id, SpeakerProfileEnrollRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context); if (userId is null) return Results.Unauthorized();
    if (request.Embedding is null || !IsValidEmbeddingJson(request.Embedding)) return Results.BadRequest(new { error = "invalid_speaker_embedding" });
    var updated = await store.EnrollSpeakerProfileAsync(id, userId.Value, IsPrivileged(context), request.Embedding, request.DurationMs);
    return updated ? Results.Ok(new { ok = true }) : Results.NotFound();
});
app.MapGet("/api/action-items", async (int? page, int? pageSize, string? search, string? status, Guid? meetingId, string? sort, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context); if (userId is null) return Results.Unauthorized();
    return Results.Ok(await store.ListActionItemRegistryPageAsync(Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 200), search, status, meetingId, sort, userId.Value, IsPrivileged(context)));
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
// Provisional ASR ingress for the active recording. This is intentionally
// separate from transcript V1/V2: it is short-lived, text-only, and rejected
// unless a matching recording session is currently active.
app.MapPost("/api/assistant/live-segments/{meetingId:guid}", async (Guid meetingId, LiveMeetingSegmentsRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    if (userId is null) return Results.Unauthorized();
    if (!await CanAccessMeetingAsync(context, meetingId)) return Results.NotFound();
    var result = await store.AppendLiveMeetingSegmentsAsync(
        meetingId,
        userId.Value,
        IsPrivileged(context),
        request.RecordingSessionId,
        request.Segments ?? Array.Empty<LiveMeetingSegmentRequest>());
    return result is null
        ? Results.Conflict(new { error = "LIVE_MEETING_NOT_ACTIVE", status = "LIVE_ASR_NOT_READY" })
        : Results.Accepted($"/api/assistant/live-segments/{meetingId}", new { recordingSessionId = result.RecordingSessionId, acceptedCount = result.AcceptedCount, expiresInSeconds = 300 });
});
app.MapGet("/api/assistant/live-context/{meetingId:guid}", async (Guid meetingId, HttpContext context, UnifiedProductStore store) =>
{
    if (CurrentUserId(context) is null) return Results.Unauthorized();
    if (!await CanAccessMeetingAsync(context, meetingId)) return Results.NotFound();
    return Results.Ok(new { meetingId, ready = await store.HasLiveMeetingContextAsync(meetingId), mode = "LIVE_MEETING", canonicalTranscript = false });
});
app.MapPost("/api/assistant/conversations", async (AssistantConversationCreateRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    if (userId is null) return Results.Unauthorized();
    var scope = request.ScopeType?.Trim().ToUpperInvariant();
    if (scope == "GLOBAL" && !IsPrivileged(context)
        && request.AssistantMode?.Trim().ToUpperInvariant() is not ("MEETING_MEMORY" or "MEETING_HISTORY")) return Results.Forbid();
    if (scope == "MEETING" && (!request.MeetingId.HasValue || !await CanAccessMeetingAsync(context, request.MeetingId.Value))) return Results.NotFound();
    if (scope is not ("MEETING" or "GLOBAL" or "GENERAL")) return Results.BadRequest(new { error = "invalid_assistant_scope" });
    var conversation = await store.CreateAssistantConversationAsync(userId.Value, request.Title, scope, request.MeetingId, request.AssistantMode);
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
        if (message.Status is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING" or "FAILED" or "NEEDS_REVIEW" or "NO_EVIDENCE" or "GROUNDING_REJECTED" or "LLM_UNAVAILABLE") return;
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
});
// Unified entry point used by Desktop text chat and the managed Voice Host.
// Voice never receives a server token: the Desktop broker forwards this call
// with the user's authenticated API session and its active meeting context.
app.MapPost("/api/assistant/requests", async (AssistantRequestRequest request, HttpContext context, UnifiedProductStore store) =>
{
    var userId = CurrentUserId(context);
    if (userId is null) return Results.Unauthorized();
    var assistantEnabled = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("ASSISTANT_ENABLED", true);
    if (!assistantEnabled)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    var question = request.Question?.Trim() ?? string.Empty;
    if (question.Length is 0 or > 2000) return Results.BadRequest(new { error = "assistant_query_invalid" });
    if (request.ActiveMeetingId is Guid meetingId && !await CanAccessMeetingAsync(context, meetingId))
        return Results.NotFound();

    var route = UnifiedProductStore.RouteAssistantRequest(question, request.RequestedMode, request.ActiveMeetingId, IsPrivileged(context));
    if (!string.IsNullOrWhiteSpace(route.ErrorCode))
        return Results.BadRequest(new { error = route.ErrorCode, status = "CLARIFICATION_REQUIRED", spokenText = route.Clarification });
    // Never answer from a stale canonical transcript while capture is active.
    // If a provisional context exists, transparently move AUTO/CURRENT
    // requests into the isolated LIVE_MEETING scope; otherwise fail closed.
    if (route.ResolvedMode == "CURRENT_MEETING" && request.ActiveMeetingId is Guid recordingMeeting
        && await store.HasActiveRecordingAsync(recordingMeeting))
    {
        if (!await store.HasLiveMeetingContextAsync(recordingMeeting))
            return Results.Conflict(new { error = "LIVE_MEETING_NOT_READY", status = "LIVE_ASR_NOT_READY", spokenText = "Пока нет свежего фрагмента текущего совещания для ответа." });
        route = route with { ResolvedMode = "LIVE_MEETING" };
    }
    if (route.ResolvedMode == "LIVE_MEETING")
    {
        if (request.ActiveMeetingId is not Guid liveMeeting || !await store.HasLiveMeetingContextAsync(liveMeeting))
            return Results.Conflict(new { error = "LIVE_MEETING_NOT_READY", status = "LIVE_ASR_NOT_READY", spokenText = "Пока нет свежего фрагмента текущего совещания для ответа." });
    }

    // Conversations are bound to a scope. Never reuse a user-owned chat for
    // another meeting or for general chat; create a fresh scoped conversation.
    if (request.ConversationId is Guid suppliedConversation)
    {
        var existing = await store.GetAssistantConversationAsync(suppliedConversation, userId.Value);
        var expectedMeeting = route.ResolvedMode is "CURRENT_MEETING" or "LIVE_MEETING" ? request.ActiveMeetingId : null;
        var expectedScope = route.ResolvedMode == "GENERAL_CHAT" ? "GENERAL" : expectedMeeting.HasValue ? "MEETING" : "GLOBAL";
        if (existing is null || !string.Equals(existing.ScopeType, expectedScope, StringComparison.OrdinalIgnoreCase) || existing.MeetingId != expectedMeeting)
            request = request with { ConversationId = null };
    }

    var source = string.Equals(request.Source, "VOICE", StringComparison.OrdinalIgnoreCase) ? "VOICE" : "DESKTOP";
    var resolvedMeetingId = route.ResolvedMode is "CURRENT_MEETING" or "LIVE_MEETING" ? request.ActiveMeetingId : null;
    // Keep voice and text requests in the same scoped conversation. A caller
    // may provide an existing conversation; otherwise create one atomically
    // in the resolved scope so follow-up questions retain context.
    var conversationId = request.ConversationId;
    if (conversationId is null)
    {
        var scope = route.ResolvedMode == "GENERAL_CHAT" ? "GENERAL" : resolvedMeetingId.HasValue ? "MEETING" : "GLOBAL";
        var conversation = await store.CreateAssistantConversationAsync(userId.Value, "Мифодий", scope, resolvedMeetingId, route.ResolvedMode);
        conversationId = conversation?.Id;
    }
    var query = await store.CreateAssistantQueryAsync(resolvedMeetingId, question, userId, route.ResolvedMode, source, conversationId, route.Confidence, request.CommandId, request.TraceId);
    if (query is null)
        return route.ResolvedMode == "LIVE_MEETING"
            ? Results.Conflict(new { error = "LIVE_MEETING_NOT_READY", status = "LIVE_ASR_NOT_READY", spokenText = "Пока нет свежего фрагмента текущего совещания для ответа." })
            : Results.Conflict(new { error = "assistant_context_not_ready", status = "TRANSCRIPT_NOT_READY" });
    return Results.Accepted($"/api/assistant/queries/{query.Id}", new
    {
        queryId = query.Id,
        conversationId,
        resolvedMode = query.AssistantMode,
        meetingId = query.MeetingId,
        status = query.Status,
        source = query.Source,
        routerConfidence = route.Confidence,
        pollUrl = $"/api/assistant/queries/{query.Id}",
        eventsUrl = $"/api/assistant/queries/{query.Id}/events"
    });
});
app.MapPost("/api/assistant/queries", async (AssistantQueryRequest request, HttpContext context, UnifiedProductStore store) =>
{
    if (request.MeetingId is Guid meetingId && !await CanAccessMeetingAsync(context, meetingId))
        return Results.NotFound();
    var mode = request.AssistantMode?.Trim().ToUpperInvariant();
    if (mode == "GENERAL_CHAT")
    {
        if (CurrentUserId(context) is null) return Results.Unauthorized();
        if (request.MeetingId is not null) return Results.BadRequest(new { error = "general_chat_cannot_use_meeting" });
    }
    else if (request.MeetingId is null && !IsPrivileged(context))
        return Results.BadRequest(new { error = "meeting_required" });
    var query = await store.CreateAssistantQueryAsync(request.MeetingId, request.Query, CurrentUserId(context), request.AssistantMode);
    return query is null ? Results.BadRequest(new { error = "meeting_not_ready_or_query_invalid" }) : Results.Accepted($"/api/assistant/queries/{query.Id}", query);
});
app.MapGet("/api/assistant/queries/{id:guid}", async (Guid id, HttpContext context, UnifiedProductStore store) =>
{
    var query = await store.GetAssistantQueryAsync(id, CurrentUserId(context), IsPrivileged(context));
    return query is null ? Results.NotFound() : Results.Ok(query);
});
app.MapGet("/api/assistant/queries/{id:guid}/events", async (Guid id, HttpContext context, HttpResponse response, UnifiedProductStore store, CancellationToken cancellationToken) =>
{
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    for (var attempt = 0; attempt < 120 && !cancellationToken.IsCancellationRequested; attempt++)
    {
        var query = await store.GetAssistantQueryAsync(id, CurrentUserId(context), IsPrivileged(context));
        if (query is null) { response.StatusCode = 404; return; }
        await response.WriteAsync($"event: status\ndata: {JsonSerializer.Serialize(query)}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        if (query.Status is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING" or "FAILED" or "NEEDS_REVIEW" or "NO_EVIDENCE" or "GROUNDING_REJECTED" or "LLM_UNAVAILABLE") return;
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

// The permanent archive is the download source.  The original storage key is
// retained as a recovery fallback for assets whose media derivatives have not
// been built yet; access is still checked against the meeting before the path
// is resolved.
app.MapGet("/api/media/{id:guid}/download", async (Guid id, string? variant, HttpContext context) =>
{
    var media = await db.GetMediaAsync(id);
    if (media is null || !await CanAccessMeetingAsync(context, media.MeetingId)) return Results.NotFound();

    // Imported video has two intentionally distinct assets: the untouched
    // source and the derived FLAC archive. Never return a FLAC payload using
    // the original .mp4/.mkv filename.
    var requestedVariant = string.IsNullOrWhiteSpace(variant) ? "archive" : variant.Trim().ToLowerInvariant();
    if (requestedVariant is not ("archive" or "original"))
        return Results.BadRequest(new { error = "invalid_media_download_variant" });
    var original = requestedVariant == "original";
    if (!original && string.IsNullOrWhiteSpace(media.ArchiveStorageKey) && MediaPolicy.IsVideoExtension(media.OriginalName))
        return Results.NotFound();
    var storageKey = original ? media.StorageKey : media.ArchiveStorageKey ?? media.StorageKey;
    if (string.IsNullOrWhiteSpace(storageKey)) return Results.NotFound();

    string path;
    try { path = StorageHelpers.StoragePath(storageKey); }
    catch (InvalidOperationException) { return Results.NotFound(); }
    if (!File.Exists(path)) return Results.NotFound();

    var extension = original ? Path.GetExtension(media.OriginalName) : Path.GetExtension(storageKey);
    if (string.IsNullOrWhiteSpace(extension)) extension = Path.GetExtension(storageKey);
    if (string.IsNullOrWhiteSpace(extension)) extension = original ? ".bin" : ".flac";
    var contentType = extension.ToLowerInvariant() switch
    {
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".ogg" or ".opus" => "audio/ogg",
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        _ => "application/octet-stream"
    };
    var originalBaseName = Path.GetFileNameWithoutExtension(media.OriginalName);
    var downloadName = original
        ? Path.GetFileName(media.OriginalName)
        : string.IsNullOrWhiteSpace(originalBaseName) ? $"meeting-{id:N}{extension}" : originalBaseName + extension;
    if (string.IsNullOrWhiteSpace(downloadName)) downloadName = $"meeting-{id:N}{extension}";
    return Results.File(File.OpenRead(path), contentType, downloadName, enableRangeProcessing: true);
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

app.MapGet("/api/meetings/{id:guid}/transcript", async (Guid id, int? version, bool? includeWords, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    var transcript = await db.GetTranscriptAsync(id, version, includeWords == true);
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

app.MapGet("/api/meetings/{id:guid}/transcript/versions", async (Guid id, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, id)) return Results.NotFound();
    return Results.Ok(await db.ListTranscriptVersionsAsync(id));
});

app.MapPatch("/api/meetings/{meetingId:guid}/transcript/segments/{segmentId:guid}",
    async (Guid meetingId, Guid segmentId, TranscriptSegmentEditRequest request, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, meetingId)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > 8000) return Results.BadRequest(new { error = "invalid_segment_text" });
    var version = await db.CreateEditedTranscriptVersionAsync(meetingId, segmentId, request.Text, CurrentUserId(context));
    return version is null ? Results.NotFound() : Results.Ok(version);
});

app.MapPost("/api/meetings/{meetingId:guid}/transcript/reprocess", async (Guid meetingId, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, meetingId)) return Results.NotFound();
    var job = await db.QueueTranscriptReprocessAsync(meetingId, CurrentUserId(context));
    return job is null ? Results.Conflict(new { error = "transcript_reprocess_unavailable" }) : Results.Accepted($"/api/jobs/{job.Id}", job);
});

app.MapPatch("/api/meetings/{meetingId:guid}/speakers/{speakerId:guid}",
    async (Guid meetingId, Guid speakerId, SpeakerRenameRequest request, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, meetingId)) return Results.NotFound();
    var updated = await db.RenameSpeakerAsync(meetingId, speakerId, request.DisplayName, CurrentUserId(context));
    return updated ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.MapPost("/api/meetings/{meetingId:guid}/speakers/merge",
    async (Guid meetingId, SpeakerMergeRequest request, HttpContext context) =>
{
    if (!await CanAccessMeetingAsync(context, meetingId)) return Results.NotFound();
    var merged = await db.MergeSpeakersAsync(meetingId, request.SourceSpeakerId, request.TargetSpeakerId, CurrentUserId(context));
    return merged ? Results.Ok(new { ok = true }) : Results.NotFound();
});

app.Run();

internal static class ClientUpdateJsonExtensions
{
    public static string? TryGetString(this JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}

public record AgentEnrollRequest(string Name, Guid? RoomId, string? Version, JsonDocument? Capabilities);
public record AgentLinkLocalRequest(Guid InstallationId, Guid? AgentId, string Name, Guid? RoomId, string? Version, JsonDocument? Capabilities);
public record AgentBootstrapRequest(Guid InstallationId, Guid? AgentId, string? Name, string? Version, JsonDocument? Capabilities);
public record AgentHeartbeatRequest(string? Status, string? Version, JsonDocument? Capabilities);
public record AgentCommandResultRequest(string? Status, JsonDocument? Result);
public record CreateRecordingSessionRequest(Guid? MeetingId, string? Title, DateTimeOffset? StartedAt, string? PipelineCorrelationId = null, string? LocalSessionId = null, Guid? OwnerUserId = null, string? AcousticProfile = "AUTO");
public record CreateTrackRequest(string TrackType, string? DeviceId, string? DeviceName = null, string? SelectionMode = null, string? RecordingProfile = null, int SampleRate = 48000, int Channels = 1, string? Encoding = null, int? BitsPerSample = null, string? SourceEncoding = null, string? SourceSubFormat = null, int? ValidBitsPerSample = null);
public record RecordingCommandRequest(Guid AgentId, string CommandType, JsonDocument? Payload);
public record UpdateTaskRequest(string Task, string? Responsible, DateTime? Deadline, string Status);
public record AssistantQueryRequest(string Query, Guid? MeetingId, string? AssistantMode = null);
public record AssistantRequestRequest(string Question, string? RequestedMode = "AUTO", Guid? ActiveMeetingId = null, Guid? ConversationId = null, string? Source = "DESKTOP", string? CommandId = null, string? TraceId = null);
public sealed record LiveMeetingSegmentRequest(Guid Id, long StartMs, long EndMs, string Text, double? Confidence = null, int Revision = 0,
    string? SourceTrackType = null, string? SourceTrackId = null, string? ChannelRole = null, string? QualityFlags = null, Guid? MeetingId = null);
public sealed record LiveMeetingSegmentsRequest(Guid? RecordingSessionId, IReadOnlyList<LiveMeetingSegmentRequest> Segments);
public record AssistantConversationCreateRequest(string? Title, string? ScopeType, Guid? MeetingId, string? AssistantMode = null);
public record AssistantConversationUpdateRequest(string? Title, bool? Archived);
public record AssistantMessageCreateRequest(string? Content, Guid? RetryOf);
public record SummaryRebuildRequest(string? Profile, int? TranscriptVersion, string? PromptVersion, string? Reason, JsonDocument? MeetingContext);
public record RecordingEventRequest(Guid Id, string EventType, long? MediaTimeMs, JsonDocument? Payload, DateTimeOffset? CreatedAt);
public record RecordingEventBatchRequest(IReadOnlyList<RecordingEventRequest> Events);

public sealed class RequestBodyTooLargeException : Exception { }

public record LoginRequest(string? Username, string? Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record AdminUserCreateRequest(string Username, string Role = "Reader");
public record AdminUserUpdateRequest(string? Role, bool? IsActive);
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
public sealed record SpeakerRow(Guid Id, string StableKey, string DisplayName, Guid? ProfileId = null, double? ProfileConfidence = null, string ProfileMatchStatus = "UNMATCHED", string? ProfileMatchReason = null, string? ProfileSuggestionName = null);
public record SpeakerRenameRequest(string DisplayName);
public record SpeakerMergeRequest(Guid SourceSpeakerId, Guid TargetSpeakerId);
public record SpeakerProfileCreateRequest(string DisplayName, JsonDocument? EmbeddingCentroid = null, string? EmbeddingModel = null);
public record SpeakerProfileRenameRequest(string DisplayName);
public record SpeakerProfileEnrollRequest(JsonDocument Embedding, long DurationMs = 0);
public record TranscriptSegmentEditRequest(string Text, string? Reason = null);
public sealed record TranscriptVersionRow(Guid Id, Guid MeetingId, int Version, string Status, string VersionKind, Guid? SourceTranscriptId, DateTime CreatedAt, string? EditReason);
public sealed record TranscriptRegistryRow(Guid TranscriptId, Guid MeetingId, string MeetingTitle, DateTime MeetingCreatedAt, int TranscriptVersion, string Status, bool IsPartial, double? QualityScore, long DurationMs, int SegmentCount, int SpeakerCount, DateTime CreatedAt);

public sealed record UserRow(Guid Id, string Username, string PasswordHash, string Role, bool MustChangePassword = false);
public sealed record AdminUserRow(Guid Id, string Username, string Role, bool IsActive, bool MustChangePassword, DateTime CreatedAt);
public sealed record TemporaryPasswordResult(AdminUserRow User, string TemporaryPassword);
public sealed record RefreshRotation(UserRow User, string RefreshToken);
public sealed record MeetingRow(Guid Id, string Title, string? Description, string Status, DateTime CreatedAt);
public sealed record JobRow(
    Guid Id,
    Guid MeetingId,
    string Type,
    string Status,
    string Stage,
    int Progress,
    int Attempt,
    string? Error,
    string? ErrorCode = null,
    string? PipelineCorrelationId = null);
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
        var normalized = (storageKey ?? string.Empty).Replace("\\", "/").Trim();
        if (normalized.StartsWith("data/", StringComparison.Ordinal)) normalized = "/" + normalized;
        if (!normalized.StartsWith("/data/", StringComparison.Ordinal) && !string.Equals(normalized, "/data", StringComparison.Ordinal))
            throw new InvalidOperationException("invalid_storage_key");

        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "/data")
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = normalized.Length == "/data".Length ? string.Empty : normalized["/data/".Length..];
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("invalid_storage_key");
        return full;
    }
}

public static class MediaPolicy
{
    public const long MaxUploadBytes = 8L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".mp4", ".mkv", ".mov", ".webm", ".avi" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".webm", ".avi" };
    public static bool IsAllowedExtension(string name) => Extensions.Contains(Path.GetExtension(name));
    public static bool IsVideoExtension(string name) => VideoExtensions.Contains(Path.GetExtension(name));
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

public sealed class OperationalRecoveryService(
    Database database,
    ILogger<OperationalRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await database.ReconcileInterruptedWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "operational_recovery_failed");
            }
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

        // Reopen only interrupted, bounded work after an API restart. Terminal
        // jobs and READY assets remain terminal; the client/server spool and
        // outbox continue to provide durable idempotency for recovery.
        await RecoverInterruptedWorkAsync(connection);
    }

    public async Task ReconcileInterruptedWorkAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await RecoverInterruptedWorkAsync(connection, cancellationToken);
    }

    private static async Task RecoverInterruptedWorkAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var jobs = new NpgsqlCommand("""
            UPDATE jobs
            SET status='QUEUED',
                stage=CASE
                    WHEN type='SUMMARIZE' THEN 'TRANSCRIPT_READY'
                    WHEN stage IN ('READY_FOR_ASR','TRANSCRIBING','ALIGNING','DIARIZING','QUALITY_CHECK','PERSISTING') THEN 'READY_FOR_ASR'
                    ELSE 'UPLOADED'
                END,
                worker_id=NULL,
                lease_expires_at=NULL,
                last_heartbeat=now(),
                error_code='WORKER_RESTART_RECOVERY',
                updated_at=now()
            WHERE status='RUNNING'
              AND lease_expires_at IS NOT NULL
              AND lease_expires_at < now()
              AND attempt < 1
            """, connection, transaction))
        {
            await jobs.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var exhausted = new NpgsqlCommand("""
            UPDATE jobs
            SET status='FAILED', stage='FAILED', worker_id=NULL,
                lease_expires_at=NULL, last_heartbeat=now(),
                error_code='RETRY_LIMIT_EXCEEDED',
                error_message=COALESCE(error_message,'Worker lease expired after retry limit'),
                updated_at=now()
            WHERE status='RUNNING'
              AND lease_expires_at IS NOT NULL
              AND lease_expires_at < now()
              AND attempt >= 1
            """, connection, transaction))
        {
            await exhausted.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var failedMeetings = new NpgsqlCommand("""
            UPDATE meetings AS meeting
            SET status='FAILED'
            FROM jobs AS job
            WHERE job.meeting_id=meeting.id
              AND (job.type IN ('TRANSCRIBE','TRANSCRIBE_REPROCESS')
                   OR job.type IN ('TRANSCRIBE_ASR','TRANSCRIPT_ENRICH'))
              AND job.status='FAILED'
              AND meeting.status IN ('INGESTING','MEDIA_PROCESSING','TRANSCRIBING','ALIGNING','DIARIZING')
              AND NOT EXISTS (
                  SELECT 1 FROM transcripts AS transcript
                  WHERE transcript.meeting_id=meeting.id
                    AND transcript.status IN ('READY','PARTIAL_READY')
              )
            """, connection, transaction))
        {
            await failedMeetings.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var sessions = new NpgsqlCommand("""
            UPDATE recording_sessions AS session
            SET state='AWAITING_AGENT_RECONNECT'
            FROM recorder_agents AS agent
            WHERE session.agent_id=agent.id
              AND session.state='RECORDING'
              AND COALESCE(agent.last_seen_at, session.created_at) < now() - interval '5 minutes'
              AND (session.local_session_id IS NULL
                   OR COALESCE(agent.capabilities->'deviceHealth'->>'activeSessionId', '')
                      <> session.local_session_id::text)
            """, connection, transaction))
        {
            await sessions.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var interruptedMeetings = new NpgsqlCommand("""
            UPDATE meetings AS meeting
            SET status='RECORDING_INTERRUPTED'
            FROM recording_sessions AS session
            WHERE session.meeting_id=meeting.id
              AND session.state='AWAITING_AGENT_RECONNECT'
              AND meeting.status='RECORDING'
            """, connection, transaction))
        {
            await interruptedMeetings.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var review = new NpgsqlCommand("""
            UPDATE recording_sessions
            SET state='ADMIN_REVIEW'
            WHERE state='AWAITING_AGENT_RECONNECT'
              AND created_at < now() - interval '24 hours'
            """, connection, transaction))
        {
            await review.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var reviewMeetings = new NpgsqlCommand("""
            UPDATE meetings AS meeting
            SET status='ADMIN_REVIEW'
            FROM recording_sessions AS session
            WHERE session.meeting_id=meeting.id
              AND session.state='ADMIN_REVIEW'
              AND meeting.status IN ('RECORDING','RECORDING_INTERRUPTED')
            """, connection, transaction))
        {
            await reviewMeetings.ExecuteNonQueryAsync(cancellationToken);
        }
        // LIVE_MEETING is provisional memory, never canonical transcript data.
        // Expiry is enforced by the recovery loop as well as the ingest path so
        // an idle server cannot retain live speech indefinitely. Keep a segment
        // briefly while a non-terminal assistant query still references it;
        // otherwise its FK evidence snapshot is allowed to cascade away.
        await using (var liveMemory = new NpgsqlCommand("""
            DELETE FROM live_meeting_segments AS segment
            WHERE segment.expires_at <= now()
              AND NOT EXISTS (
                  SELECT 1
                  FROM assistant_live_query_evidence AS evidence
                  JOIN assistant_queries AS query ON query.id=evidence.query_id
                  WHERE evidence.live_segment_id=segment.id
                    AND query.status NOT IN (
                        'READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED',
                        'NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED',
                        'LLM_UNAVAILABLE')
                    AND query.created_at > now()-interval '15 minutes'
              )
            """, connection, transaction))
        {
            await liveMemory.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ApplyMigrationsAsync(NpgsqlConnection connection)
    {
        await using (var table = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_migrations(version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
            await table.ExecuteNonQueryAsync();
        await using (var checksums = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_migration_checksums(version text PRIMARY KEY REFERENCES schema_migrations(version) ON DELETE CASCADE, sha256 text NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now())", connection))
            await checksums.ExecuteNonQueryAsync();
        var directory = Path.Combine(AppContext.BaseDirectory, "Migrations");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Migration directory not found: {directory}");
        foreach (var file in Directory.GetFiles(directory, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var version = Path.GetFileNameWithoutExtension(file);
            var sql = await File.ReadAllTextAsync(file);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
            await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=@version)", connection);
            check.Parameters.AddWithValue("version", version);
            if ((bool)(await check.ExecuteScalarAsync())!)
            {
                await using var known = new NpgsqlCommand("SELECT sha256 FROM schema_migration_checksums WHERE version=@version", connection);
                known.Parameters.AddWithValue("version", version);
                var stored = await known.ExecuteScalarAsync();
                if (stored is string knownChecksum && !string.Equals(knownChecksum, checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"MIGRATION_CHECKSUM_MISMATCH:{version}");
                if (stored is null or DBNull)
                {
                    await using var recordChecksum = new NpgsqlCommand("INSERT INTO schema_migration_checksums(version,sha256) VALUES(@version,@sha256) ON CONFLICT(version) DO NOTHING", connection);
                    recordChecksum.Parameters.AddWithValue("version", version);
                    recordChecksum.Parameters.AddWithValue("sha256", checksum);
                    await recordChecksum.ExecuteNonQueryAsync();
                }
                continue;
            }
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var migration = new NpgsqlCommand(sql, connection, transaction))
                await migration.ExecuteNonQueryAsync();
            await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES(@version)", connection, transaction))
            {
                record.Parameters.AddWithValue("version", version);
                await record.ExecuteNonQueryAsync();
            }
            await using (var recordChecksum = new NpgsqlCommand("INSERT INTO schema_migration_checksums(version,sha256) VALUES(@version,@sha256)", connection, transaction))
            {
                recordChecksum.Parameters.AddWithValue("version", version);
                recordChecksum.Parameters.AddWithValue("sha256", checksum);
                await recordChecksum.ExecuteNonQueryAsync();
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
            "SELECT id, username, password_hash, role, must_change_password FROM users WHERE username=@username AND is_active", connection);
        command.Parameters.AddWithValue("username", username);
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null :
            new UserRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
    }

    public async Task<IReadOnlyList<AdminUserRow>> ListUsersAsync()
    {
        var result = new List<AdminUserRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,username,role,is_active,must_change_password,created_at FROM users ORDER BY username", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new AdminUserRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetDateTime(5)));
        return result;
    }

    public async Task<AdminUserRow?> CreateUserAsync(string username, string role, string temporaryPassword)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO users(id,username,password_hash,role,must_change_password) VALUES(@id,@username,@hash,@role,true) RETURNING id,username,role,is_active,must_change_password,created_at", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("username", username.Trim());
        command.Parameters.AddWithValue("hash", PasswordService.Hash(temporaryPassword));
        command.Parameters.AddWithValue("role", role);
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            return !await reader.ReadAsync() ? null : new AdminUserRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetDateTime(5));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return null; }
    }

    public async Task<bool> UpdateUserAsync(Guid userId, string? role, bool? isActive)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE users SET role=COALESCE(@role,role),is_active=COALESCE(@active,is_active) WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("role", (object?)role ?? DBNull.Value);
        command.Parameters.AddWithValue("active", (object?)isActive ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<string?> ResetPasswordAsync(Guid userId)
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE users SET password_hash=@hash,must_change_password=true,password_changed_at=NULL WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("hash", PasswordService.Hash(password));
        return await command.ExecuteNonQueryAsync() > 0 ? password : null;
    }

    public async Task<bool> RevokeUserSessionsAsync(Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var sessions = new NpgsqlCommand("UPDATE sessions SET revoked_at=now() WHERE user_id=@id AND revoked_at IS NULL", connection, transaction);
        sessions.Parameters.AddWithValue("id", userId);
        await sessions.ExecuteNonQueryAsync();
        await using var refresh = new NpgsqlCommand("UPDATE refresh_sessions SET revoked_at=now() WHERE user_id=@id AND revoked_at IS NULL", connection, transaction);
        refresh.Parameters.AddWithValue("id", userId);
        var changed = await refresh.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return changed >= 0;
    }

    public async Task<bool> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, string? currentSessionToken = null)
    {
        if (newPassword.Length < 12) return false;
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var select = new NpgsqlCommand("SELECT password_hash FROM users WHERE id=@id AND is_active FOR UPDATE", connection, transaction);
        select.Parameters.AddWithValue("id", userId);
        var hash = await select.ExecuteScalarAsync() as string;
        if (hash is null || !PasswordService.Verify(currentPassword, hash)) { await transaction.RollbackAsync(); return false; }
        await using var update = new NpgsqlCommand("UPDATE users SET password_hash=@hash,must_change_password=false,password_changed_at=now() WHERE id=@id", connection, transaction);
        update.Parameters.AddWithValue("id", userId);
        update.Parameters.AddWithValue("hash", PasswordService.Hash(newPassword));
        await update.ExecuteNonQueryAsync();
        await using var sessions = new NpgsqlCommand("UPDATE sessions SET revoked_at=now() WHERE user_id=@id AND revoked_at IS NULL AND (@current_hash IS NULL OR token_hash<>@current_hash)", connection, transaction);
        sessions.Parameters.AddWithValue("id", userId);
        sessions.Parameters.AddWithValue("current_hash", currentSessionToken is null ? DBNull.Value : SessionHash(currentSessionToken));
        await sessions.ExecuteNonQueryAsync();
        await using var refresh = new NpgsqlCommand("UPDATE refresh_sessions SET revoked_at=now() WHERE user_id=@id AND revoked_at IS NULL", connection, transaction);
        refresh.Parameters.AddWithValue("id", userId);
        await refresh.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<UserRow?> GetUserForSessionAsync(string token)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT u.id, u.username, u.password_hash, u.role, u.must_change_password FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@hash AND s.expires_at > now() AND s.revoked_at IS NULL AND u.is_active", connection);
        command.Parameters.AddWithValue("hash", SessionHash(token));
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null :
            new UserRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
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
            "SELECT r.id,r.user_id,r.family_id,r.expires_at,r.revoked_at,r.replaced_by,u.id,u.username,u.password_hash,u.role,u.must_change_password FROM refresh_sessions r JOIN users u ON u.id=r.user_id WHERE r.token_hash=@hash FOR UPDATE", connection, transaction);
        select.Parameters.AddWithValue("hash", SessionHash(token));
        await using var reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            await transaction.CommitAsync();
            return null;
        }

        var id = reader.GetGuid(0);
        var user = new UserRow(reader.GetGuid(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetBoolean(10));
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

    public async Task<IReadOnlyList<TranscriptRegistryRow>> ListTranscriptRegistryAsync(int limit, int offset, string? search, string? status, DateTime? dateFrom, DateTime? dateTo, Guid ownerId, bool includeAll)
    {
        var result = new List<TranscriptRegistryRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT t.id,m.id,m.title,m.created_at,t.version,t.status,(t.status='PARTIAL_READY'),t.quality_score,
                   COALESCE(MAX(s.end_ms),0),COUNT(s.id),COUNT(DISTINCT s.speaker_id),t.created_at
            FROM meetings m
            JOIN LATERAL (SELECT * FROM transcripts WHERE meeting_id=m.id ORDER BY version DESC LIMIT 1) t ON true
            LEFT JOIN transcript_segments s ON s.transcript_id=t.id AND COALESCE(s.is_hidden,false)=false
            WHERE (@include_all OR m.owner_id=@owner)
              AND (@search IS NULL OR m.title ILIKE '%' || @search || '%')
              AND (@status IS NULL OR t.status=@status)
              AND (@date_from IS NULL OR m.created_at >= @date_from)
              AND (@date_to IS NULL OR m.created_at < @date_to)
            GROUP BY t.id,m.id,m.title,m.created_at,t.version,t.status,t.quality_score,t.created_at
            ORDER BY m.created_at DESC LIMIT @limit OFFSET @offset
            """, connection);
        // Explicit types are required for nullable filters. PostgreSQL cannot
        // infer the type of a NULL parameter used in an `IS NULL` predicate,
        // which previously made the default `/api/transcripts` request fail
        // with 42P08 before the Desktop could render the registry.
        command.Parameters.AddWithValue("include_all", includeAll); command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.Add("search", NpgsqlDbType.Text).Value = (object?)search ?? DBNull.Value;
        command.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status?.ToUpperInvariant() ?? DBNull.Value;
        command.Parameters.Add("date_from", NpgsqlDbType.TimestampTz).Value = (object?)dateFrom ?? DBNull.Value;
        command.Parameters.Add("date_to", NpgsqlDbType.TimestampTz).Value = (object?)dateTo ?? DBNull.Value;
        command.Parameters.AddWithValue("limit", limit); command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new TranscriptRegistryRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetDateTime(3), reader.GetInt32(4), reader.GetString(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : Convert.ToDouble(reader.GetValue(7)), reader.GetInt64(8), checked((int)reader.GetInt64(9)), checked((int)reader.GetInt64(10)), reader.GetDateTime(11)));
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
        await using var command = new NpgsqlCommand("SELECT id,stable_key,display_name,speaker_profile_id,profile_confidence,COALESCE(profile_match_status,'UNMATCHED'),profile_match_reason,profile_suggestion_name FROM meeting_speakers WHERE meeting_id=@id ORDER BY stable_key", connection); command.Parameters.AddWithValue("id", meetingId);
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new SpeakerRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? "UNMATCHED" : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))); return result;
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
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,type,status,stage,progress,attempt,error_message,error_code,pipeline_correlation_id FROM jobs WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return !await reader.ReadAsync() ? null : ReadJob(reader);
    }

    public async Task<IReadOnlyList<JobRow>> ListJobsAsync(Guid meetingId)
    {
        var result = new List<JobRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,type,status,stage,progress,attempt,error_message,error_code,pipeline_correlation_id FROM jobs WHERE meeting_id=@id ORDER BY created_at DESC", connection);
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
            "UPDATE jobs SET status='QUEUED',stage=CASE WHEN type='SUMMARIZE' THEN 'TRANSCRIPT_READY' WHEN type IN ('TRANSCRIBE','TRANSCRIBE_REPROCESS') THEN 'READY_FOR_ASR' WHEN type IN ('TRANSCRIBE_ASR','TRANSCRIPT_ENRICH') THEN 'READY_FOR_ASR' ELSE 'UPLOADED' END,progress=0,error_message=NULL,error_code=NULL,worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,attempt=attempt+1,updated_at=now() WHERE id=@id AND status IN ('FAILED','CANCELLED') RETURNING id,meeting_id,type,status,stage,progress,attempt,error_message,error_code,pipeline_correlation_id", connection, tx);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var job = ReadJob(reader);
        await reader.CloseAsync();

        await using (var meeting = new NpgsqlCommand(
            "UPDATE meetings SET status=@status WHERE id=@meeting AND status <> 'CANCELLED'", connection, tx))
        {
            meeting.Parameters.AddWithValue("status", job.Type == "SUMMARIZE" ? "SUMMARIZING" : job.Type is "TRANSCRIBE" or "TRANSCRIBE_REPROCESS" or "TRANSCRIBE_ASR" or "TRANSCRIPT_ENRICH" ? "TRANSCRIBING" : "INGESTING");
            meeting.Parameters.AddWithValue("meeting", job.MeetingId);
            await meeting.ExecuteNonQueryAsync();
        }

        var messageId = Guid.NewGuid();
        await using var publish = job.Type switch
        {
            "SUMMARIZE" => new NpgsqlCommand(
                "INSERT INTO outbox_messages(id,topic,payload) SELECT @outbox,'llm.summarize',jsonb_build_object('message_id',@message,'job_id',j.id,'meeting_id',j.meeting_id,'transcript_id',t.id,'correlation_id',(SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=j.meeting_id ORDER BY created_at DESC LIMIT 1)) FROM jobs j JOIN LATERAL (SELECT id FROM transcripts WHERE meeting_id=j.meeting_id ORDER BY version DESC LIMIT 1) t ON true WHERE j.id=@id", connection, tx),
            "TRANSCRIBE" or "TRANSCRIBE_REPROCESS" or "TRANSCRIBE_ASR" or "TRANSCRIPT_ENRICH" => new NpgsqlCommand(
                "INSERT INTO outbox_messages(id,topic,payload) SELECT @outbox,'ml.transcribe',jsonb_build_object('message_id',@message,'job_id',j.id,'meeting_id',j.meeting_id,'media_asset_id',j.media_asset_id,'stage',j.stage,'attempt',j.attempt,'storage_key',a.asr_storage_key,'source_type',a.source_type,'language','ru','acousticProfile',COALESCE((SELECT acoustic_profile FROM recording_sessions WHERE meeting_id=j.meeting_id ORDER BY created_at DESC LIMIT 1),'AUTO'),'correlation_id',(SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=j.meeting_id ORDER BY created_at DESC LIMIT 1)) FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE j.id=@id", connection, tx),
            _ => new NpgsqlCommand(
                "INSERT INTO outbox_messages(id,topic,payload) SELECT @outbox,'media.ingest',jsonb_build_object('message_id',@message,'job_id',j.id,'meeting_id',j.meeting_id,'media_asset_id',j.media_asset_id,'stage',j.stage,'attempt',j.attempt,'storage_key',a.storage_key,'source_type',a.source_type,'language','ru','acousticProfile',COALESCE((SELECT acoustic_profile FROM recording_sessions WHERE meeting_id=j.meeting_id ORDER BY created_at DESC LIMIT 1),'AUTO'),'correlation_id',(SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=j.meeting_id ORDER BY created_at DESC LIMIT 1)) FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE j.id=@id", connection, tx),
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
    public async Task<TranscriptRow> GetTranscriptAsync(Guid meetingId, int? requestedVersion = null, bool includeWords = false)
    {
        var segments = new List<TranscriptSegmentRow>();
        Guid transcriptId = Guid.Empty;
        string status = "PENDING";
        JsonDocument? warnings = null;
        JsonDocument? quality = null;
        double? qualityScore = null;
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT t.id,t.status,t.warnings,t.quality_metadata,t.quality_score,s.id,s.ordinal,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label),s.text,s.confidence,CASE WHEN @include_words THEN s.words ELSE NULL END,COALESCE(s.segment_kind,'SPEECH'),COALESCE(s.is_hidden,false) FROM transcripts t LEFT JOIN transcript_segments s ON s.transcript_id=t.id LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id WHERE t.meeting_id=@meeting AND t.version=COALESCE(@version,(SELECT MAX(version) FROM transcripts WHERE meeting_id=@meeting)) AND COALESCE(s.is_hidden,false)=false ORDER BY s.ordinal", connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        command.Parameters.AddWithValue("version", (object?)requestedVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("include_words", includeWords);
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

    public async Task<bool> RenameSpeakerAsync(Guid meetingId, Guid speakerId, string displayName, Guid? actorUserId = null)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("UPDATE meeting_speakers SET display_name=@name WHERE id=@speaker AND meeting_id=@meeting RETURNING display_name", connection, tx);
        command.Parameters.AddWithValue("name", displayName.Trim());
        command.Parameters.AddWithValue("speaker", speakerId);
        command.Parameters.AddWithValue("meeting", meetingId);
        var previous = await command.ExecuteScalarAsync();
        if (previous is not string) return false;
        await AppendAuditAsync(connection, tx, actorUserId, meetingId, "SPEAKER", speakerId, "SPEAKER_RENAMED", JsonSerializer.Serialize(new { displayName = previous }), JsonSerializer.Serialize(new { displayName = displayName.Trim() }));
        await tx.CommitAsync();
        return true;
    }

    public async Task<bool> MergeSpeakersAsync(Guid meetingId, Guid source, Guid target, Guid? actorUserId = null)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE transcript_segments SET speaker_id=@target WHERE speaker_id=@source AND transcript_id=(SELECT id FROM transcripts WHERE meeting_id=@meeting ORDER BY version DESC LIMIT 1)", connection, tx);
        update.Parameters.AddWithValue("target", target);
        update.Parameters.AddWithValue("source", source);
        update.Parameters.AddWithValue("meeting", meetingId);
        var count = await update.ExecuteNonQueryAsync();
        if (count > 0)
            await AppendAuditAsync(connection, tx, actorUserId, meetingId, "SPEAKER", source, "SPEAKER_MERGED_CURRENT_TRANSCRIPT", JsonSerializer.Serialize(new { source, target }), JsonSerializer.Serialize(new { updatedSegments = count }));
        await tx.CommitAsync();
        return count > 0;
    }

    public async Task<IReadOnlyList<TranscriptVersionRow>> ListTranscriptVersionsAsync(Guid meetingId)
    {
        var result = new List<TranscriptVersionRow>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id,meeting_id,version,status,COALESCE(version_kind,'GENERATED'),source_transcript_id,created_at,edit_reason FROM transcripts WHERE meeting_id=@meeting ORDER BY version DESC", connection);
        command.Parameters.AddWithValue("meeting", meetingId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new TranscriptVersionRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetDateTime(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return result;
    }

    public async Task<TranscriptVersionRow?> CreateEditedTranscriptVersionAsync(Guid meetingId, Guid segmentId, string text, Guid? actorUserId)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var current = new NpgsqlCommand("SELECT id,version,status,language,model_name,warnings,quality_metadata,quality_score,processing_profile,selected_asr_pass FROM transcripts WHERE meeting_id=@meeting ORDER BY version DESC LIMIT 1 FOR UPDATE", connection, tx);
        current.Parameters.AddWithValue("meeting", meetingId);
        await using var reader = await current.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var sourceId = reader.GetGuid(0); var nextVersion = reader.GetInt32(1) + 1; var status = reader.GetString(2);
        var language = reader.IsDBNull(3) ? null : reader.GetString(3); var model = reader.IsDBNull(4) ? null : reader.GetString(4);
        var warnings = reader.IsDBNull(5) ? "[]" : reader.GetString(5); var quality = reader.IsDBNull(6) ? "{}" : reader.GetString(6);
        var score = reader.IsDBNull(7) ? (object)DBNull.Value : reader.GetValue(7); var profile = reader.IsDBNull(8) ? null : reader.GetString(8); var pass = reader.IsDBNull(9) ? null : reader.GetString(9);
        await reader.CloseAsync();
        await using var sourceSegment = new NpgsqlCommand("SELECT ordinal FROM transcript_segments WHERE id=@segment AND transcript_id=@source", connection, tx);
        sourceSegment.Parameters.AddWithValue("segment", segmentId); sourceSegment.Parameters.AddWithValue("source", sourceId);
        if (await sourceSegment.ExecuteScalarAsync() is not int editedOrdinal) return null;
        var newId = Guid.NewGuid();
        await using (var create = new NpgsqlCommand("INSERT INTO transcripts(id,meeting_id,version,status,language,model_name,warnings,quality_metadata,quality_score,processing_profile,selected_asr_pass,source_transcript_id,version_kind,edited_by_user_id,edit_reason) VALUES(@id,@meeting,@version,@status,@language,@model,@warnings::jsonb,@quality::jsonb,@score,@profile,@pass,@source,'USER_EDITED',@actor,'MANUAL_SEGMENT_EDIT')", connection, tx))
        {
            create.Parameters.AddWithValue("id", newId); create.Parameters.AddWithValue("meeting", meetingId); create.Parameters.AddWithValue("version", nextVersion); create.Parameters.AddWithValue("status", status); create.Parameters.AddWithValue("language", (object?)language ?? DBNull.Value); create.Parameters.AddWithValue("model", (object?)model ?? DBNull.Value); create.Parameters.AddWithValue("warnings", warnings); create.Parameters.AddWithValue("quality", quality); create.Parameters.AddWithValue("score", score); create.Parameters.AddWithValue("profile", (object?)profile ?? DBNull.Value); create.Parameters.AddWithValue("pass", (object?)pass ?? DBNull.Value); create.Parameters.AddWithValue("source", sourceId); create.Parameters.AddWithValue("actor", (object?)actorUserId ?? DBNull.Value); await create.ExecuteNonQueryAsync();
        }
        await using (var copy = new NpgsqlCommand("INSERT INTO transcript_segments(id,transcript_id,ordinal,start_ms,end_ms,speaker_id,speaker_label,text,confidence,words,segment_kind,is_hidden) SELECT gen_random_uuid(),@target,ordinal,start_ms,end_ms,speaker_id,speaker_label,CASE WHEN ordinal=@ordinal THEN @text ELSE text END,confidence,words,segment_kind,is_hidden FROM transcript_segments WHERE transcript_id=@source", connection, tx))
        { copy.Parameters.AddWithValue("target", newId); copy.Parameters.AddWithValue("source", sourceId); copy.Parameters.AddWithValue("ordinal", editedOrdinal); copy.Parameters.AddWithValue("text", text.Trim()); await copy.ExecuteNonQueryAsync(); }
        await AppendAuditAsync(connection, tx, actorUserId, meetingId, "TRANSCRIPT", newId, "TRANSCRIPT_VERSION_USER_EDITED", JsonSerializer.Serialize(new { sourceId, segmentId }), JsonSerializer.Serialize(new { version = nextVersion, editedOrdinal }));
        await tx.CommitAsync();
        return new TranscriptVersionRow(newId, meetingId, nextVersion, status, "USER_EDITED", sourceId, DateTime.UtcNow, "MANUAL_SEGMENT_EDIT");
    }

    public async Task<JobRow?> QueueTranscriptReprocessAsync(Guid meetingId, Guid? actorUserId)
    {
        await using var connection = await OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
        await using var asset = new NpgsqlCommand("SELECT id,asr_storage_key FROM media_assets WHERE meeting_id=@meeting AND status='READY' AND asr_storage_key IS NOT NULL ORDER BY created_at DESC LIMIT 1", connection, tx);
        asset.Parameters.AddWithValue("meeting", meetingId); await using var reader = await asset.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null; var assetId = reader.GetGuid(0); var storageKey = reader.GetString(1); await reader.CloseAsync();
        var id = Guid.NewGuid();
        var correlation = await GetPipelineCorrelationIdForMeetingAsync(connection, tx, meetingId);
        await using var insert = new NpgsqlCommand("INSERT INTO jobs(id,meeting_id,media_asset_id,type,status,stage,progress,pipeline_correlation_id) VALUES(@id,@meeting,@asset,'TRANSCRIBE_REPROCESS','QUEUED','UPLOADED',0,@correlation)", connection, tx);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("meeting", meetingId); insert.Parameters.AddWithValue("asset", assetId); insert.Parameters.AddWithValue("correlation", (object?)correlation ?? DBNull.Value); await insert.ExecuteNonQueryAsync();
        var messageId = Guid.NewGuid();
        await using var outbox = new NpgsqlCommand("INSERT INTO outbox_messages(id,topic,payload) VALUES(@id,'ml.transcribe',jsonb_build_object('message_id',@message,'job_id',@job,'meeting_id',@meeting,'media_asset_id',@asset,'stage','UPLOADED','storage_key',@storage,'reprocess',true,'correlation_id',@correlation))", connection, tx);
        outbox.Parameters.AddWithValue("id", Guid.NewGuid()); outbox.Parameters.AddWithValue("message", messageId); outbox.Parameters.AddWithValue("job", id); outbox.Parameters.AddWithValue("meeting", meetingId); outbox.Parameters.AddWithValue("asset", assetId); outbox.Parameters.AddWithValue("storage", storageKey); outbox.Parameters.AddWithValue("correlation", (object?)correlation ?? DBNull.Value); await outbox.ExecuteNonQueryAsync();
        await AppendAuditAsync(connection, tx, actorUserId, meetingId, "TRANSCRIPT", null, "TRANSCRIPT_REPROCESS_QUEUED", null, JsonSerializer.Serialize(new { id, assetId }));
        await tx.CommitAsync(); return new JobRow(id, meetingId, "TRANSCRIBE_REPROCESS", "QUEUED", "UPLOADED", 0, 0, null);
    }

    private static async Task AppendAuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid? actorUserId, Guid meetingId, string entityType, Guid? entityId, string eventType, string? beforeState, string? afterState)
    {
        await using var audit = new NpgsqlCommand("INSERT INTO audit_events(id,actor_user_id,meeting_id,entity_type,entity_id,event_type,before_state,after_state) VALUES(gen_random_uuid(),@actor,@meeting,@type,@entity,@event,@before::jsonb,@after::jsonb)", connection, tx);
        audit.Parameters.AddWithValue("actor", (object?)actorUserId ?? DBNull.Value); audit.Parameters.AddWithValue("meeting", meetingId); audit.Parameters.AddWithValue("type", entityType); audit.Parameters.AddWithValue("entity", (object?)entityId ?? DBNull.Value); audit.Parameters.AddWithValue("event", eventType); audit.Parameters.AddWithValue("before", (object?)beforeState ?? DBNull.Value); audit.Parameters.AddWithValue("after", (object?)afterState ?? DBNull.Value); await audit.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string?> GetPipelineCorrelationIdForMeetingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid meetingId)
    {
        await using var command = new NpgsqlCommand("SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=@meeting ORDER BY created_at DESC LIMIT 1", connection, transaction);
        command.Parameters.AddWithValue("meeting", meetingId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static JobRow ReadJob(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null,
            reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null);

    private static MediaAssetRow ReadMedia(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10));

    private static string SessionHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));


}

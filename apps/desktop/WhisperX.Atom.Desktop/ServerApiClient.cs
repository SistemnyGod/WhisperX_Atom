using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX.Atom.Desktop;

public sealed record DesktopMeeting(string Id, string Title, string? Description, string Status, DateTimeOffset CreatedAt)
{
    [JsonIgnore]
    public string StatusText => UiStatusMapper.Text(Status);

    [JsonIgnore]
    public string CreatedAtText => CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
}
public sealed record DesktopMeetingCancellation(string MeetingId, string Status, int CancelledJobs);
public sealed record DesktopCurrentUser(Guid Id, string Username, string Role, bool MustChangePassword = false)
{
    public bool IsPrivileged => string.Equals(Role, "Administrator", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Role, "Operator", StringComparison.OrdinalIgnoreCase);
}
public sealed record DesktopTranscript(string Id, string MeetingId, string Status, IReadOnlyList<DesktopTranscriptSegment> Segments, bool IsPartial = false, JsonDocument? Warnings = null, JsonDocument? Quality = null, double? QualityScore = null);
public sealed record DesktopTranscriptVersion(string Id, string MeetingId, int Version, string Status, string VersionKind, Guid? SourceTranscriptId, DateTime CreatedAt, string? EditReason);
public sealed record DesktopTranscriptRegistry(string TranscriptId, string MeetingId, string MeetingTitle, DateTimeOffset MeetingCreatedAt, int TranscriptVersion, string Status, bool IsPartial, double? QualityScore, long DurationMs, int SegmentCount, int SpeakerCount, DateTimeOffset CreatedAt);
public sealed record DesktopTranscriptSegment(string Id, int Ordinal, long StartMs, long EndMs, string? Speaker, string Text, double? Confidence, JsonDocument? Words)
{
    [JsonIgnore]
    public string TimeLabel => $"{TimeSpan.FromMilliseconds(StartMs):hh\\:mm\\:ss}";
}
public sealed record DesktopSpeaker(string Id, string StableKey, string DisplayName);
public sealed record DesktopSummary(string Id, string MeetingId, Guid? TranscriptId, int Version, string Status, string ModelName, string PromptVersion, string SourceHash, JsonDocument Content, DateTime CreatedAt);
public sealed record DesktopDecision(string Id, string MeetingId, Guid? SummaryId, string Text, string Status, DateTime CreatedAt);
public sealed record DesktopTask(string Id, string MeetingId, Guid? SummaryId, string Task, string? Responsible, DateTime? Deadline, string Status, Guid? EvidenceSegmentId, DateTime CreatedAt);
public sealed record DesktopAgentEnrollment(string AgentId, string Token);
public sealed record DesktopAgentBootstrapResult(
    string AgentId,
    string? Token,
    bool ReenrollRequired = false,
    bool Linked = true,
    string State = "AGENT_READY");
public sealed record DesktopAgent(Guid Id, string Name, Guid? RoomId, string Status, DateTimeOffset? LastSeenAt, Guid? InstallationId = null,
    string EffectiveStatus = "OFFLINE", bool IsActive = false, int? HeartbeatAgeSeconds = null)
{
    [JsonIgnore]
    public string LastSeenText => LastSeenAt is null ? "—" : LastSeenAt.Value.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");

    [JsonIgnore]
    public string RoomText => RoomId?.ToString() ?? "Не назначена";

    [JsonIgnore]
    public string EffectiveStatusText => UiStatusMapper.Text(EffectiveStatus);
}
public sealed record DesktopSystemStatus(bool Ready, bool Postgres, long FreeBytes, long TotalBytes, DateTimeOffset CheckedAt);
public sealed record DesktopSystemVersion(string Product, int ApiVersion, string ReleaseVersion, string MinDesktopVersion, string MinRecorderVersion, DateTimeOffset ServerTimeUtc);
public sealed record DesktopProcessingReadiness(bool Ready, JsonElement Components, JsonElement? Queue, DateTimeOffset? CheckedAt);
public sealed record DesktopMedia(string Id, string MeetingId, string OriginalName, string? StorageKey, string? Sha256, long SizeBytes, long? DurationMs, string Status, string? ArchiveStorageKey, string? PreviewStorageKey, string? AsrStorageKey);
public sealed record DesktopJob(
    string Id,
    string MeetingId,
    string Type,
    string Status,
    string Stage,
    int Progress,
    int Attempt,
    string? Error,
    string? ErrorCode = null,
    string? PipelineCorrelationId = null)
{
    [JsonIgnore]
    public string StatusText => UiStatusMapper.Text(Status);

    [JsonIgnore]
    public string StageText => UiStatusMapper.Text(Stage);

    [JsonIgnore]
    public bool Retryable => IsRetryable(ErrorCode);

    private static bool IsRetryable(string? errorCode) => errorCode is not null
        && (errorCode.StartsWith("WORKER_", StringComparison.OrdinalIgnoreCase)
            || errorCode.StartsWith("GPU_", StringComparison.OrdinalIgnoreCase)
            || errorCode.StartsWith("CUDA_", StringComparison.OrdinalIgnoreCase)
            || errorCode.StartsWith("NETWORK_", StringComparison.OrdinalIgnoreCase)
            || errorCode.StartsWith("SERVER_", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("TRANSCRIPT_EMPTY", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("TRANSCRIPT_INVALID_TIMECODE", StringComparison.OrdinalIgnoreCase)
            || errorCode.Equals("AUDIO_PROCESSING_ERROR", StringComparison.OrdinalIgnoreCase));
}
public sealed record DesktopAssistantQuery(string Id, string? MeetingId, string Query, string Status, string? Answer, string? VoiceAnswer, JsonDocument Evidence, string? ErrorCode, DateTime CreatedAt, DateTime? CompletedAt);

public enum DesktopAuthState
{
    Unknown,
    Authenticated,
    Refreshing,
    LoginRequired,
    Offline
}

public sealed class DesktopApiException : Exception
{
    public DesktopApiException(int statusCode, string errorCode, string message, Exception? innerException = null, bool retryable = false, string? traceId = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Retryable = retryable;
        TraceId = traceId;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
    public bool Retryable { get; }
    public string? TraceId { get; }
}

public sealed class ServerApiClient : IDisposable
{
    private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(10);
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly HttpClient _uploadHttp;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _authVersion;
    private DesktopAuthState _authState = DesktopAuthState.Unknown;

    public event Action? SessionChanged;

    public ServerApiClient(string? baseUrl = null)
    {
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = _cookies };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(NormalizeBaseUrl(baseUrl ?? DesktopSettings.DefaultApiUrl())),
            Timeout = ApiRequestTimeout
        };
        _uploadHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var machineConfig = MachineServerConfig.Load();
        var serverOrigin = machineConfig?.Managed == true
            ? machineConfig.ServerOrigin
            : Environment.GetEnvironmentVariable("WHISPERX_TUS_URL")
                ?? machineConfig?.ServerOrigin
                ?? baseUrl
                ?? DesktopSettings.DefaultApiUrl();
        TusBaseAddress = new Uri(NormalizeBaseUrl(serverOrigin));
    }

    public Uri BaseAddress => _http.BaseAddress!;
    public Uri TusBaseAddress { get; }
    public DesktopAuthState AuthState => _authState;
    public DateTimeOffset? SessionExpiresAtUtc { get; private set; }

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("API URL должен быть HTTP(S)-адресом.", nameof(value));
        return uri.ToString().TrimEnd('/') + "/";
    }

    public string? GetSessionCookie()
    {
        var cookieHeader = _cookies.GetCookieHeader(BaseAddress);
        return string.IsNullOrWhiteSpace(cookieHeader) ? null : cookieHeader;
    }

    public void RestoreSession(string? cookieHeader) => RestoreSession(cookieHeader, null);

    public void RestoreSession(string? cookieHeader, DateTimeOffset? expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return;
        try
        {
            _cookies.SetCookies(BaseAddress, cookieHeader);
            _authState = DesktopAuthState.Unknown;
            SessionExpiresAtUtc = expiresAtUtc;
            Interlocked.Increment(ref _authVersion);
        }
        catch (CookieException)
        {
            ClearCookies();
            _authState = DesktopAuthState.LoginRequired;
            SessionExpiresAtUtc = null;
        }
    }

    public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("ready", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as TaskCanceledException. Treat that
            // as an unavailable backend, but preserve explicit page cancellation.
            return false;
        }
    }

    public async Task<DesktopSystemStatus?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAuthorizedAsync(HttpMethod.Get, "api/system/status", null, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<DesktopSystemStatus>(_json, cancellationToken);
        }
        catch (DesktopApiException) { return null; }
    }

    public async Task<DesktopProcessingReadiness?> GetProcessingReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAuthorizedAsync(HttpMethod.Get, "api/system/readiness", null, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var ready = root.TryGetProperty("ready", out var readyElement) && readyElement.ValueKind == JsonValueKind.True;
            var components = root.TryGetProperty("components", out var componentsElement) ? componentsElement.Clone() : default;
            JsonElement? queue = root.TryGetProperty("queue", out var queueElement) && queueElement.ValueKind != JsonValueKind.Null
                ? queueElement.Clone()
                : null;
            var checkedAt = root.TryGetProperty("checkedAt", out var checkedElement)
                && checkedElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(checkedElement.GetString(), out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
            return new DesktopProcessingReadiness(ready, components, queue, checkedAt);
        }
        catch (DesktopApiException) { return null; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task<DesktopSystemVersion?> GetSystemVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/system/version", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<DesktopSystemVersion>(_json, cancellationToken);
        }
        catch (HttpRequestException) { return null; }
    }
    public async Task<bool> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/login", new { username, password }, _json, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            _authState = DesktopAuthState.Authenticated;
            SessionExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(12);
            Interlocked.Increment(ref _authVersion);
            SessionChanged?.Invoke();
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "api/auth/change-password", new { currentPassword, newPassword }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var observedVersion = Interlocked.Read(ref _authVersion);
        return await RefreshAsync(observedVersion, cancellationToken);
    }

    public async Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        if (AuthState == DesktopAuthState.LoginRequired) return false;
        if (AuthState == DesktopAuthState.Authenticated && SessionExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(30)) return true;
        var currentUser = await GetCurrentUserAsync(cancellationToken);
        return currentUser is not null;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try { using var response = await _http.PostAsync("api/auth/logout", content: null, cancellationToken); }
        catch (HttpRequestException) { }
        ClearCookies();
        _authState = DesktopAuthState.LoginRequired;
        SessionExpiresAtUtc = null;
        Interlocked.Increment(ref _authVersion);
        SessionChanged?.Invoke();
    }

    public async Task<DesktopAgentEnrollment> LinkLocalAgentAsync(Guid installationId, Guid? agentId, string name, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "api/agents/link-local", new
        {
            installationId,
            agentId,
            name,
            version = "0.1.0",
            capabilities = new { desktop = true, microphone = true, systemAudio = true }
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        return new DesktopAgentEnrollment(root.GetProperty("agentId").GetString()!, root.GetProperty("token").GetString()!);
    }

    public async Task<DesktopAgentBootstrapResult> BootstrapLocalAgentAsync(Guid installationId, Guid? agentId, string name, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "api/agents/bootstrap", new
        {
            installationId,
            agentId,
            name,
            version = "0.1.0",
            capabilities = new { desktop = true, microphone = true, systemAudio = true }
        }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            var traceId = response.Headers.TryGetValues("X-Trace-Id", out var traceValues)
                ? traceValues.FirstOrDefault()
                : null;
            throw new DesktopApiException(
                (int)response.StatusCode,
                response.IsSuccessStatusCode ? "AGENT_BOOTSTRAP_RESPONSE_EMPTY" : "AGENT_BOOTSTRAP_FAILED",
                response.IsSuccessStatusCode
                    ? "Сервер вернул пустой ответ при привязке Recorder Agent."
                    : "Сервер не смог выполнить привязку Recorder Agent.",
                retryable: (int)response.StatusCode >= 500,
                traceId: traceId);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            var traceId = response.Headers.TryGetValues("X-Trace-Id", out var traceValues)
                ? traceValues.FirstOrDefault()
                : null;
            throw new DesktopApiException(
                (int)response.StatusCode,
                "AGENT_BOOTSTRAP_RESPONSE_INVALID",
                "Сервер вернул некорректный ответ при привязке Recorder Agent.",
                exception,
                retryable: (int)response.StatusCode >= 500,
                traceId: traceId);
        }

        using (document)
        {
        var root = document.RootElement;
        if (response.StatusCode == HttpStatusCode.Conflict && string.Equals(root.GetProperty("error").GetString(), "REENROLL_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return new DesktopAgentBootstrapResult(
                root.GetProperty("agentId").GetGuid().ToString(),
                null,
                true,
                false,
                root.TryGetProperty("state", out var reenrollState) ? reenrollState.GetString() ?? "REENROLL_REQUIRED" : "REENROLL_REQUIRED");
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString() ?? "AGENT_BOOTSTRAP_FAILED"
                : "AGENT_BOOTSTRAP_FAILED";
            var retryable = root.TryGetProperty("retryable", out var retryElement)
                && retryElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && retryElement.GetBoolean();
            var traceId = root.TryGetProperty("traceId", out var traceElement) && traceElement.ValueKind == JsonValueKind.String
                ? traceElement.GetString()
                : response.Headers.TryGetValues("X-Trace-Id", out var traceValues) ? traceValues.FirstOrDefault() : null;
            throw new DesktopApiException((int)response.StatusCode, errorCode, errorCode, retryable: retryable, traceId: traceId);
        }
        var token = root.TryGetProperty("token", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()
            : null;
        var linked = !root.TryGetProperty("linked", out var linkedElement)
            || linkedElement.ValueKind == JsonValueKind.True;
        var state = root.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.String
            ? stateElement.GetString() ?? (linked ? "AGENT_READY" : "AGENT_LINK_PENDING")
            : linked ? "AGENT_READY" : "AGENT_LINK_PENDING";
        return new DesktopAgentBootstrapResult(root.GetProperty("agentId").GetGuid().ToString(), token, false, linked, state);
        }
    }

    public async Task<DesktopAgentEnrollment> ReenrollAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/agents/{agentId}/reenroll", null, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument? document = null;
        try { if (!string.IsNullOrWhiteSpace(body)) document = JsonDocument.Parse(body); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = document is not null && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? "AGENT_REENROLL_FAILED"
                : $"AGENT_REENROLL_FAILED_{(int)response.StatusCode}";
            throw new DesktopApiException((int)response.StatusCode, errorCode, errorCode);
        }

        if (document is null)
            throw new DesktopApiException((int)response.StatusCode, "AGENT_REENROLL_RESPONSE_INVALID", "Сервер вернул пустой ответ на re-enroll Agent.");
        var root = document.RootElement;
        return new DesktopAgentEnrollment(
            root.GetProperty("agentId").GetGuid().ToString(),
            root.GetProperty("token").GetString() ?? throw new DesktopApiException(200, "AGENT_REENROLL_TOKEN_MISSING", "Сервер не вернул новый токен Agent."));
    }

    private async Task<bool> RefreshAsync(long observedVersion, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (Interlocked.Read(ref _authVersion) != observedVersion && AuthState == DesktopAuthState.Authenticated)
                return true;
            _authState = DesktopAuthState.Refreshing;
            try
            {
                using var response = await _http.PostAsync("api/auth/refresh", content: null, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _authState = DesktopAuthState.Authenticated;
                    SessionExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(12);
                    Interlocked.Increment(ref _authVersion);
                    SessionChanged?.Invoke();
                    return true;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    ClearCookies();
                    _authState = DesktopAuthState.LoginRequired;
                    SessionExpiresAtUtc = null;
                    Interlocked.Increment(ref _authVersion);
                    SessionChanged?.Invoke();
                    return false;
                }
                _authState = DesktopAuthState.Offline;
                return false;
            }
            catch (HttpRequestException)
            {
                _authState = DesktopAuthState.Offline;
                return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _authState = DesktopAuthState.Offline;
                return false;
            }
        }
        finally { _refreshGate.Release(); }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string uri, object? payload, CancellationToken cancellationToken)
    {
        var observedVersion = Interlocked.Read(ref _authVersion);
        HttpResponseMessage response;
        try { response = await SendAsync(method, uri, payload, cancellationToken); }
        catch (HttpRequestException) {
            _authState = DesktopAuthState.Offline;
            throw new DesktopApiException(0, "backend_unavailable", "API недоступен. Локальная запись продолжает работать.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            _authState = DesktopAuthState.Offline;
            throw new DesktopApiException(0, "backend_timeout", "API не ответил вовремя. Локальная запись продолжает работать.");
        }
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        if (!await RefreshAsync(observedVersion, cancellationToken))
            throw new DesktopApiException(401, "authentication_required", "Требуется повторный вход в API.");
        try { response = await SendAsync(method, uri, payload, cancellationToken); }
        catch (HttpRequestException) {
            _authState = DesktopAuthState.Offline;
            throw new DesktopApiException(0, "backend_unavailable", "API недоступен. Локальная запись продолжает работать.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            _authState = DesktopAuthState.Offline;
            throw new DesktopApiException(0, "backend_timeout", "API не ответил вовремя. Локальная запись продолжает работать.");
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _authState = DesktopAuthState.LoginRequired;
            throw new DesktopApiException(401, "authentication_required", "Требуется повторный вход в API.");
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: _json);
        return await _http.SendAsync(request, cancellationToken);
    }

    private void ClearCookies()
    {
        foreach (var cookie in _cookies.GetCookies(BaseAddress).Cast<Cookie>().ToArray())
        {
            try
            {
                _cookies.Add(BaseAddress, new Cookie(cookie.Name, string.Empty, cookie.Path, cookie.Domain)
                {
                    Expires = DateTime.UtcNow.AddYears(-1)
                });
            }
            catch (CookieException) { }
        }
    }

    public async Task<DesktopAgentEnrollment> EnrollAgentAsync(string name, string enrollmentSecret, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/agents/enroll");
        request.Headers.Add("X-Agent-Enrollment-Secret", enrollmentSecret);
        request.Content = JsonContent.Create(new { name, version = "0.1.0", capabilities = new { desktop = true, microphone = true, systemAudio = true } }, options: _json);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new DesktopApiException(401, "agent_enrollment_required", "Enrollment secret отклонён сервером.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        return new DesktopAgentEnrollment(root.GetProperty("agentId").GetString()!, root.GetProperty("token").GetString()!);
    }

    public async Task<DesktopMeeting> CreateMeetingAsync(string title, string? description = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "api/meetings", new { title, description }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DesktopMeeting>(_json, cancellationToken)
            ?? throw new InvalidOperationException("API returned an empty meeting.");
    }

    public async Task<IReadOnlyList<DesktopMeeting>> GetMeetingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "api/meetings", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopMeeting>>(_json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopMeeting>> GetMeetingsPageAsync(int limit = 200, int offset = 0, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings?limit={limit}&offset={offset}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopMeeting>>(_json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopTranscriptRegistry>> GetTranscriptRegistryPageAsync(int limit = 50, int offset = 0, string? search = null, string? status = null, DateTimeOffset? dateFrom = null, DateTimeOffset? dateTo = null, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var query = $"limit={limit}&offset={Math.Max(0, offset)}";
        if (!string.IsNullOrWhiteSpace(search)) query += "&search=" + Uri.EscapeDataString(search.Trim());
        if (!string.IsNullOrWhiteSpace(status)) query += "&status=" + Uri.EscapeDataString(status.Trim());
        if (dateFrom.HasValue) query += "&dateFrom=" + Uri.EscapeDataString(dateFrom.Value.UtcDateTime.ToString("O"));
        if (dateTo.HasValue) query += "&dateTo=" + Uri.EscapeDataString(dateTo.Value.UtcDateTime.ToString("O"));
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "api/transcripts?" + query, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopTranscriptRegistry>>(_json, cancellationToken) ?? [];
    }

    public async Task<DesktopCurrentUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAuthorizedAsync(HttpMethod.Get, "api/auth/me", null, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var user = await response.Content.ReadFromJsonAsync<DesktopCurrentUser>(_json, cancellationToken);
            if (user is not null) _authState = DesktopAuthState.Authenticated;
            return user;
        }
        catch (DesktopApiException) { return null; }
    }

    public async Task<IReadOnlyList<DesktopAgent>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "api/agents", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopAgent>>(_json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopJob>> GetJobsAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings/{meetingId}/jobs", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopJob>>(_json, cancellationToken) ?? [];
    }

    public async Task<DesktopJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/jobs/{jobId}", null, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopJob>(_json, cancellationToken);
    }

    public async Task<DesktopJob?> WaitForJobEventsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/jobs/{jobId}/events", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        DesktopJob? latest = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            try { latest = JsonSerializer.Deserialize<DesktopJob>(line[5..].Trim(), _json); }
            catch (JsonException) { continue; }
            if (latest is not null && IsTerminalJobStatus(latest.Status)) break;
        }
        return latest;
    }

    private static bool IsTerminalJobStatus(string? status) => status is not null
        && (status.Equals("READY", StringComparison.OrdinalIgnoreCase)
            || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
            || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase));

    public async Task<DesktopJob?> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/jobs/{jobId}/retry", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorCode = $"JOB_RETRY_FAILED_{(int)response.StatusCode}";
            var retryable = (int)response.StatusCode >= 500;
            string? traceId = response.Headers.TryGetValues("X-Trace-Id", out var traceValues) ? traceValues.FirstOrDefault() : null;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    errorCode = error.GetString() ?? errorCode;
                if (root.TryGetProperty("retryable", out var retry) && retry.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    retryable = retry.GetBoolean();
                if (root.TryGetProperty("traceId", out var trace) && trace.ValueKind == JsonValueKind.String)
                    traceId = trace.GetString() ?? traceId;
            }
            catch (JsonException) { }
            throw new DesktopApiException((int)response.StatusCode, errorCode, errorCode, retryable: retryable, traceId: traceId);
        }
        return await response.Content.ReadFromJsonAsync<DesktopJob>(_json, cancellationToken);
    }

    public async Task<DesktopMeetingCancellation?> CancelMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/meetings/{meetingId}/cancel", new { force = false }, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopMeetingCancellation>(_json, cancellationToken);
    }

    public async Task<bool> DeleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Delete, $"api/meetings/{meetingId}?force=false", null, cancellationToken);
        return response.IsSuccessStatusCode;
    }
    public async Task<IReadOnlyList<DesktopAssistantConversation>> GetAssistantConversationsAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/assistant/conversations?includeArchived={includeArchived.ToString().ToLowerInvariant()}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopAssistantConversation>>(_json, cancellationToken) ?? [];
    }

    public async Task<DesktopAssistantConversation?> CreateAssistantConversationAsync(string title, string scopeType, Guid? meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "api/assistant/conversations", new { title, scopeType, meetingId }, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopAssistantConversation>(_json, cancellationToken);
    }

    public async Task<IReadOnlyList<DesktopAssistantMessage>> GetAssistantMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/assistant/conversations/{conversationId}/messages", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopAssistantMessage>>(_json, cancellationToken) ?? [];
    }

    public async Task<DesktopAssistantMessageCreateResult?> CreateAssistantMessageAsync(Guid conversationId, string content, Guid? retryOf = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/assistant/conversations/{conversationId}/messages", new { content, retryOf }, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopAssistantMessageCreateResult>(_json, cancellationToken);
    }

    public async Task<DesktopAssistantMessage?> WaitForAssistantMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/assistant/conversations/{conversationId}/messages/{messageId}/events", null, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            DesktopAssistantMessage? latest = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                try { latest = JsonSerializer.Deserialize<DesktopAssistantMessage>(line[5..].Trim(), _json); }
                catch (JsonException) { continue; }
                if (latest is not null && IsTerminalAssistantStatus(latest.Status)) return latest;
            }
            return latest;
        }
        catch (HttpRequestException)
        {
            for (var attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 + attempt / 3)), cancellationToken);
                var messages = await GetAssistantMessagesAsync(conversationId, cancellationToken);
                var message = messages.FirstOrDefault(item => item.Id.Equals(messageId.ToString(), StringComparison.OrdinalIgnoreCase));
                if (message is not null && IsTerminalAssistantStatus(message.Status)) return message;
            }
            return null;
        }
    }

    public async Task<bool> UpdateAssistantConversationAsync(Guid conversationId, string? title = null, bool? archived = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Patch, $"api/assistant/conversations/{conversationId}", new { title, archived }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteAssistantConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Delete, $"api/assistant/conversations/{conversationId}", null, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static bool IsTerminalAssistantStatus(string status) => status.Equals("READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase);

    public async Task<DesktopAssistantQuery?> CreateAssistantQueryAsync(string query, Guid? meetingId = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "api/assistant/queries", new { query, meetingId }, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopAssistantQuery>(_json, cancellationToken);
    }

    public async Task<DesktopAssistantQuery?> GetAssistantQueryAsync(Guid queryId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/assistant/queries/{queryId}", null, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopAssistantQuery>(_json, cancellationToken);
    }
    public async Task<DesktopTranscript?> GetTranscriptAsync(Guid meetingId, int? version = null, CancellationToken cancellationToken = default)
    {
        var suffix = version.HasValue ? $"?version={version.Value}" : string.Empty;
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings/{meetingId}/transcript{suffix}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DesktopTranscript>(_json, cancellationToken);
    }

    public Task<DesktopTranscript?> GetTranscriptAsync(Guid meetingId, CancellationToken cancellationToken) =>
        GetTranscriptAsync(meetingId, null, cancellationToken);

    public async Task<IReadOnlyList<DesktopTranscriptVersion>> GetTranscriptVersionsAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings/{meetingId}/transcript/versions", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopTranscriptVersion>>(_json, cancellationToken) ?? [];
    }

    public async Task<DesktopTranscriptVersion?> EditTranscriptSegmentAsync(Guid meetingId, Guid segmentId, string text, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Patch, $"api/meetings/{meetingId}/transcript/segments/{segmentId}", new { text }, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopTranscriptVersion>(_json, cancellationToken);
    }

    public async Task<DesktopJob?> ReprocessTranscriptAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/meetings/{meetingId}/transcript/reprocess", new { }, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopJob>(_json, cancellationToken);
    }

    public async Task<IReadOnlyList<DesktopSpeaker>> GetSpeakersAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings/{meetingId}/speakers", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopSpeaker>>(_json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopMedia>> GetMediaAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings/{meetingId}/media", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopMedia>>(_json, cancellationToken) ?? [];
    }

    public async Task<DesktopSummary?> GetSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings/{meetingId}/summary", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DesktopSummary>(_json, cancellationToken);
    }

    public async Task<DesktopJob?> QueueSummaryRebuildAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/meetings/{meetingId}/summary/rebuild", null, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopJob>(_json, cancellationToken);
    }

    public async Task<bool> RebuildSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        return await QueueSummaryRebuildAsync(meetingId, cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<DesktopDecision>> GetDecisionsAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings/{meetingId}/decisions", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopDecision>>(_json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopTask>> GetTasksAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/meetings/{meetingId}/tasks", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopTask>>(_json, cancellationToken) ?? [];
    }

    public async Task<bool> RenameSpeakerAsync(Guid meetingId, Guid speakerId, string displayName, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Patch, $"api/meetings/{meetingId}/speakers/{speakerId}", new { displayName }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> MergeSpeakersAsync(Guid meetingId, Guid sourceSpeakerId, Guid targetSpeakerId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/meetings/{meetingId}/speakers/merge", new { sourceSpeakerId, targetSpeakerId }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateTaskAsync(DesktopTask task, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Patch, $"api/tasks/{task.Id}", new
        {
            task = task.Task,
            responsible = task.Responsible,
            deadline = task.Deadline,
            status = task.Status,
        }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<DesktopMeeting> ImportFileAsync(string path, string? title = null, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл записи не найден.", path);
        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > 8L * 1024 * 1024 * 1024) throw new InvalidOperationException("Файл должен быть больше 0 и не больше 8 ГБ.");
        var allowed = new[] { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".mp4", ".mkv", ".mov", ".webm", ".avi" };
        if (!allowed.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Формат файла не поддерживается.");

        var meeting = await CreateMeetingAsync(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.Name) : title.Trim(), cancellationToken: cancellationToken);
        using var reservationResponse = await SendAuthorizedAsync(HttpMethod.Post, $"api/meetings/{meeting.Id}/uploads", new { fileName = file.Name, sizeBytes = file.Length }, cancellationToken);
        reservationResponse.EnsureSuccessStatusCode();
        var reservation = await reservationResponse.Content.ReadFromJsonAsync<UploadReservation>(_json, cancellationToken)
            ?? throw new InvalidOperationException("API не вернул резервирование загрузки.");

        var uploadUri = Uri.TryCreate(reservation.UploadUrl, UriKind.Absolute, out var absolute)
            ? absolute : new Uri(TusBaseAddress, reservation.UploadUrl.TrimStart('/'));
        using var request = new HttpRequestMessage(new HttpMethod("POST"), new Uri(TusBaseAddress, "files/"));
        request.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
        request.Headers.TryAddWithoutValidation("Upload-Length", file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Upload-Metadata", string.Join(",",
            $"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Name))}",
            $"reservationId {Convert.ToBase64String(Encoding.UTF8.GetBytes(reservation.UploadId.ToString()))}"));
        using var createResponse = await _uploadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var location = createResponse.Headers.Location ?? uploadUri;
        if (!location.IsAbsoluteUri) location = new Uri(TusBaseAddress, location);

        const int chunkSize = 16 * 1024 * 1024;
        long offset = 0;
        var retries = 0;
        while (offset < file.Length)
        {
            try
            {
                offset = await GetTusOffsetAsync(location, cancellationToken);
                if (offset < 0 || offset > file.Length) throw new InvalidOperationException("TUS вернул некорректную позицию загрузки.");
                await using var input = File.OpenRead(path);
                input.Position = offset;
                while (offset < file.Length)
                {
                    var length = Math.Min(chunkSize, file.Length - offset);
                    using var patch = new HttpRequestMessage(new HttpMethod("PATCH"), location);
                    patch.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
                    patch.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    patch.Content = new StreamContent(new BoundedReadStream(input, length));
                    patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
                    patch.Content.Headers.ContentLength = length;
                    using var patchResponse = await _uploadHttp.SendAsync(patch, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    patchResponse.EnsureSuccessStatusCode();
                    var nextOffset = patchResponse.Headers.TryGetValues("Upload-Offset", out var values)
                        && long.TryParse(values.FirstOrDefault(), out var serverOffset)
                        ? serverOffset : offset + length;
                    if (nextOffset <= offset || nextOffset > file.Length) throw new InvalidOperationException("TUS вернул некорректную позицию загрузки.");
                    offset = nextOffset;
                    progress?.Report(offset);
                }
            }
            catch (HttpRequestException) when (retries++ < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, retries))), cancellationToken);
                // The next loop starts with HEAD and resumes from the server offset.
            }
            catch (HttpRequestException ex)
            {
                throw new DesktopApiException(0, "UPLOAD_CONNECTION_LOST", "Соединение с сервером загрузки потеряно. Загрузка продолжится с последнего подтверждённого блока.", ex);
            }
        }
        return meeting;
    }

    private async Task<long> GetTusOffsetAsync(Uri location, CancellationToken cancellationToken)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, location);
        head.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
        using var response = await _uploadHttp.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response.Headers.TryGetValues("Upload-Offset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var offset) ? offset : 0;
    }

    private sealed class BoundedReadStream(Stream inner, long remaining) : Stream
    {
        private long _remaining = remaining;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _remaining;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }
        protected override void Dispose(bool disposing) { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record UploadReservation(Guid UploadId, string UploadUrl, string? TusVersion, long MaxSizeBytes);

    public async Task<string?> DownloadPreviewAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/media/{mediaId}/preview", null, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "previews");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{mediaId}.ogg");
        var temporary = path + ".part";
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = File.Create(temporary))
            await input.CopyToAsync(output, cancellationToken);
        File.Move(temporary, path, true);
        CleanupPreviewCache(directory, path);
        return path;
    }

    private static void CleanupPreviewCache(string directory, string currentPath)
    {
        const int maximumFiles = 20;
        const long maximumBytes = 2L * 1024 * 1024 * 1024;
        var files = new DirectoryInfo(directory).EnumerateFiles("*.ogg").OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        long retainedBytes = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            retainedBytes += file.Length;
            if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (index < maximumFiles && retainedBytes <= maximumBytes) continue;
            try { file.Delete(); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _uploadHttp.Dispose();
    }
}

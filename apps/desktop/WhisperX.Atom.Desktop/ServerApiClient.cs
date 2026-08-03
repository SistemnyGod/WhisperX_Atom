using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperX.Atom.Desktop;

public sealed record DesktopMeeting(string Id, string Title, string? Description, string Status, DateTimeOffset CreatedAt);
public sealed record DesktopTranscript(string Id, string MeetingId, string Status, IReadOnlyList<DesktopTranscriptSegment> Segments);
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
public sealed record DesktopSystemStatus(bool Ready, bool Postgres, long FreeBytes, long TotalBytes, DateTimeOffset CheckedAt);
public sealed record DesktopMedia(string Id, string MeetingId, string OriginalName, string? StorageKey, string? Sha256, long SizeBytes, long? DurationMs, string Status, string? ArchiveStorageKey, string? PreviewStorageKey, string? AsrStorageKey);
public sealed record DesktopJob(string Id, string MeetingId, string Type, string Status, string Stage, int Progress, int Attempt, string? Error);

public sealed class ServerApiClient : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly HttpClient _uploadHttp;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public ServerApiClient(string baseUrl = "http://localhost:8080")
    {
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = _cookies };
        _http = new HttpClient(handler) { BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)) };
        _uploadHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        TusBaseAddress = new Uri(Environment.GetEnvironmentVariable("WHISPERX_TUS_URL") ?? "http://localhost:1080");
    }

    public Uri BaseAddress => _http.BaseAddress!;
    public Uri TusBaseAddress { get; }

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("API URL должен быть HTTP(S)-адресом.", nameof(value));
        return uri.ToString().TrimEnd('/') + "/";
    }

    public string? GetSessionCookie()
    {
        var cookies = _cookies.GetCookies(BaseAddress).Cast<Cookie>().Select(cookie => $"{cookie.Name}={cookie.Value}");
        return string.Join("; ", cookies);
    }

    public void RestoreSession(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return;
        foreach (var pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            try { _cookies.Add(BaseAddress, new Cookie(pair[..separator].Trim(), pair[(separator + 1)..].Trim(), "/")); }
            catch (CookieException) { }
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
    }

    public async Task<DesktopSystemStatus?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/system/status", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopSystemStatus>(_json, cancellationToken);
    }
    public async Task<bool> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/login", new { username, password }, _json, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<DesktopAgentEnrollment> EnrollAgentAsync(string name, string enrollmentSecret, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/agents/enroll");
        request.Headers.Add("X-Agent-Enrollment-Secret", enrollmentSecret);
        request.Content = JsonContent.Create(new { name, version = "0.1.0", capabilities = new { desktop = true, microphone = true, systemAudio = true } }, options: _json);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        return new DesktopAgentEnrollment(root.GetProperty("agentId").GetString()!, root.GetProperty("token").GetString()!);
    }

    public async Task<DesktopMeeting> CreateMeetingAsync(string title, string? description = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/meetings", new { title, description }, _json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DesktopMeeting>(_json, cancellationToken)
            ?? throw new InvalidOperationException("API returned an empty meeting.");
    }

    public async Task<IReadOnlyList<DesktopMeeting>> GetMeetingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/meetings", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopMeeting>>(_json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopJob>> GetJobsAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/meetings/{meetingId}/jobs", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopJob>>(_json, cancellationToken) ?? [];
    }

    public async Task<DesktopJob?> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"api/jobs/{jobId}/retry", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopJob>(_json, cancellationToken);
    }
    public async Task<DesktopTranscript?> GetTranscriptAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/meetings/{meetingId}/transcript", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DesktopTranscript>(_json, cancellationToken);
    }

    public async Task<IReadOnlyList<DesktopSpeaker>> GetSpeakersAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/meetings/{meetingId}/speakers", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopSpeaker>>(_json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopMedia>> GetMediaAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/meetings/{meetingId}/media", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopMedia>>(_json, cancellationToken) ?? [];
    }

    public async Task<DesktopSummary?> GetSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/meetings/{meetingId}/summary", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DesktopSummary>(_json, cancellationToken);
    }

    public async Task<bool> RebuildSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"api/meetings/{meetingId}/summary/rebuild", content: null, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<DesktopDecision>> GetDecisionsAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/meetings/{meetingId}/decisions", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopDecision>>(_json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopTask>> GetTasksAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/meetings/{meetingId}/tasks", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesktopTask>>(_json, cancellationToken) ?? [];
    }

    public async Task<bool> RenameSpeakerAsync(Guid meetingId, Guid speakerId, string displayName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PatchAsJsonAsync($"api/meetings/{meetingId}/speakers/{speakerId}", new { displayName }, _json, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> MergeSpeakersAsync(Guid meetingId, Guid sourceSpeakerId, Guid targetSpeakerId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"api/meetings/{meetingId}/speakers/merge", new { sourceSpeakerId, targetSpeakerId }, _json, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateTaskAsync(DesktopTask task, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PatchAsJsonAsync($"api/tasks/{task.Id}", new
        {
            task = task.Task,
            responsible = task.Responsible,
            deadline = task.Deadline,
            status = task.Status,
        }, _json, cancellationToken);
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
        using var reservationResponse = await _http.PostAsJsonAsync($"api/meetings/{meeting.Id}/uploads", new { fileName = file.Name, sizeBytes = file.Length }, _json, cancellationToken);
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

        await using var input = File.OpenRead(path);
        using var patch = new HttpRequestMessage(new HttpMethod("PATCH"), location);
        patch.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
        patch.Headers.TryAddWithoutValidation("Upload-Offset", "0");
        patch.Content = new StreamContent(input);
        patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        patch.Content.Headers.ContentLength = file.Length;
        using var patchResponse = await _uploadHttp.SendAsync(patch, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        patchResponse.EnsureSuccessStatusCode();
        progress?.Report(file.Length);
        return meeting;
    }

    private sealed record UploadReservation(Guid UploadId, string UploadUrl, string? TusVersion, long MaxSizeBytes);

    public async Task<string?> DownloadPreviewAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/media/{mediaId}/preview", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

public sealed class BackendService : IBackendService
{
    private ServerApiClient _client = null!;

    public BackendService(DesktopSettings settings)
    {
        ApplySettings(settings);
    }

    public string ApiUrl => _client.BaseAddress.ToString().TrimEnd('/');
    public bool HasSession => !string.IsNullOrWhiteSpace(SessionCookie);
    public string? SessionCookie => _client.GetSessionCookie();

    public void ApplySettings(DesktopSettings settings)
    {
        _client?.Dispose();
        _client = new ServerApiClient(settings.ApiUrl);
        _client.RestoreSession(settings.UnprotectSessionCookie());
    }

    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default) => _client.CheckReadyAsync(cancellationToken);
    public Task<DesktopSystemStatus?> GetSystemStatusAsync(CancellationToken cancellationToken = default) => _client.GetSystemStatusAsync(cancellationToken);
    public Task<IReadOnlyList<DesktopMeeting>> GetMeetingsAsync(CancellationToken cancellationToken = default) => _client.GetMeetingsAsync(cancellationToken);
    public Task<IReadOnlyList<DesktopMeeting>> GetMeetingsPageAsync(int limit, int offset, CancellationToken cancellationToken = default) => _client.GetMeetingsPageAsync(limit, offset, cancellationToken);
    public Task<DesktopCurrentUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default) => _client.GetCurrentUserAsync(cancellationToken);
    public Task<IReadOnlyList<DesktopAgent>> GetAgentsAsync(CancellationToken cancellationToken = default) => _client.GetAgentsAsync(cancellationToken);
    public Task<DesktopAssistantQuery?> CreateAssistantQueryAsync(string query, Guid? meetingId = null, CancellationToken cancellationToken = default) => _client.CreateAssistantQueryAsync(query, meetingId, cancellationToken);
    public Task<DesktopAssistantQuery?> GetAssistantQueryAsync(Guid queryId, CancellationToken cancellationToken = default) => _client.GetAssistantQueryAsync(queryId, cancellationToken);
    public Task<IReadOnlyList<DesktopJob>> GetJobsAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.GetJobsAsync(meetingId, cancellationToken);
    public Task<DesktopJob?> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default) => _client.RetryJobAsync(jobId, cancellationToken);
    public Task<DesktopTranscript?> GetTranscriptAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.GetTranscriptAsync(meetingId, cancellationToken);
    public Task<IReadOnlyList<DesktopSpeaker>> GetSpeakersAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.GetSpeakersAsync(meetingId, cancellationToken);
    public Task<IReadOnlyList<DesktopMedia>> GetMediaAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.GetMediaAsync(meetingId, cancellationToken);
    public Task<DesktopSummary?> GetSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.GetSummaryAsync(meetingId, cancellationToken);
    public Task<bool> RebuildSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.RebuildSummaryAsync(meetingId, cancellationToken);
    public Task<IReadOnlyList<DesktopDecision>> GetDecisionsAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.GetDecisionsAsync(meetingId, cancellationToken);
    public Task<IReadOnlyList<DesktopTask>> GetTasksAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.GetTasksAsync(meetingId, cancellationToken);
    public Task<bool> UpdateTaskAsync(DesktopTask task, CancellationToken cancellationToken = default) => _client.UpdateTaskAsync(task, cancellationToken);
    public Task<string?> DownloadPreviewAsync(Guid mediaId, CancellationToken cancellationToken = default) => _client.DownloadPreviewAsync(mediaId, cancellationToken);

    public async Task<bool> LoginAsync(string apiUrl, string username, string password, CancellationToken cancellationToken = default)
    {
        var candidate = new ServerApiClient(apiUrl);
        try
        {
            var ok = await candidate.LoginAsync(username, password, cancellationToken);
            if (!ok) return false;
            _client.Dispose();
            _client = candidate;
            return true;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public async Task<DesktopAgentEnrollment?> EnrollAgentAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        if (!HasSession) return null;
        return await _client.EnrollAgentAsync(name, secret, cancellationToken);
    }

    public Task<DesktopMeeting> ImportFileAsync(string path, string? title = null, CancellationToken cancellationToken = default) =>
        _client.ImportFileAsync(path, title, cancellationToken: cancellationToken);

    public void Dispose() => _client.Dispose();
}

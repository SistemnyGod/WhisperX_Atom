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
    public bool HasSession => AuthState != DesktopAuthState.LoginRequired && !string.IsNullOrWhiteSpace(SessionCookie);
    public string? SessionCookie => _client.GetSessionCookie();
    public DesktopAuthState AuthState => _client.AuthState;
    public DateTimeOffset? SessionExpiresAtUtc => _client.SessionExpiresAtUtc;

    public void ApplySettings(DesktopSettings settings)
    {
        if (_client is not null) _client.SessionChanged -= PersistSession;
        _client?.Dispose();
        _client = new ServerApiClient(settings.ApiUrl);
        _client.SessionChanged += PersistSession;
        _client.RestoreSession(settings.UnprotectSessionCookie(), settings.SessionExpiresAtUtc);
    }

    private void PersistSession()
    {
        var current = new DesktopSettingsStore().Load();
        // An expired/invalid session or a temporary API outage must not erase
        // the last protected cookie. SettingsViewModel.LogoutAsync explicitly
        // clears it after the user confirms a real logout.
        if (_client.AuthState == DesktopAuthState.LoginRequired
            && string.IsNullOrWhiteSpace(SessionCookie)
            && !string.IsNullOrWhiteSpace(current.ProtectedSessionCookie))
            return;
        DesktopSettings.Save(ApiUrl, current.Username, SessionCookie, current.ArchiveRoot,
            current.MicrophoneDeviceId, current.SystemAudioDeviceId, SessionExpiresAtUtc);
    }

    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default) => _client.CheckReadyAsync(cancellationToken);
    public Task<DesktopSystemStatus?> GetSystemStatusAsync(CancellationToken cancellationToken = default) => _client.GetSystemStatusAsync(cancellationToken);
    public Task<IReadOnlyList<DesktopMeeting>> GetMeetingsAsync(CancellationToken cancellationToken = default) => _client.GetMeetingsAsync(cancellationToken);
    public Task<IReadOnlyList<DesktopMeeting>> GetMeetingsPageAsync(int limit, int offset, CancellationToken cancellationToken = default) => _client.GetMeetingsPageAsync(limit, offset, cancellationToken);
    public Task<IReadOnlyList<DesktopTranscriptRegistry>> GetTranscriptRegistryPageAsync(int limit, int offset, string? search = null, string? status = null, DateTimeOffset? dateFrom = null, DateTimeOffset? dateTo = null, CancellationToken cancellationToken = default) => _client.GetTranscriptRegistryPageAsync(limit, offset, search, status, dateFrom, dateTo, cancellationToken);
    public Task<DesktopCurrentUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default) => _client.GetCurrentUserAsync(cancellationToken);
    public Task<IReadOnlyList<DesktopAgent>> GetAgentsAsync(CancellationToken cancellationToken = default) => _client.GetAgentsAsync(cancellationToken);
    public Task<IReadOnlyList<DesktopAssistantConversation>> GetAssistantConversationsAsync(bool includeArchived = false, CancellationToken cancellationToken = default) => _client.GetAssistantConversationsAsync(includeArchived, cancellationToken);
    public Task<DesktopAssistantConversation?> CreateAssistantConversationAsync(string title, string scopeType, Guid? meetingId, CancellationToken cancellationToken = default) => _client.CreateAssistantConversationAsync(title, scopeType, meetingId, cancellationToken);
    public Task<IReadOnlyList<DesktopAssistantMessage>> GetAssistantMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) => _client.GetAssistantMessagesAsync(conversationId, cancellationToken);
    public Task<DesktopAssistantMessageCreateResult?> CreateAssistantMessageAsync(Guid conversationId, string content, Guid? retryOf = null, CancellationToken cancellationToken = default) => _client.CreateAssistantMessageAsync(conversationId, content, retryOf, cancellationToken);
    public Task<DesktopAssistantMessage?> WaitForAssistantMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default) => _client.WaitForAssistantMessageAsync(conversationId, messageId, cancellationToken);
    public Task<bool> UpdateAssistantConversationAsync(Guid conversationId, string? title = null, bool? archived = null, CancellationToken cancellationToken = default) => _client.UpdateAssistantConversationAsync(conversationId, title, archived, cancellationToken);
    public Task<bool> DeleteAssistantConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) => _client.DeleteAssistantConversationAsync(conversationId, cancellationToken);
    public Task<DesktopAssistantQuery?> CreateAssistantQueryAsync(string query, Guid? meetingId = null, CancellationToken cancellationToken = default) => _client.CreateAssistantQueryAsync(query, meetingId, cancellationToken);
    public Task<DesktopAssistantQuery?> GetAssistantQueryAsync(Guid queryId, CancellationToken cancellationToken = default) => _client.GetAssistantQueryAsync(queryId, cancellationToken);
    public Task<IReadOnlyList<DesktopJob>> GetJobsAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.GetJobsAsync(meetingId, cancellationToken);
    public Task<DesktopJob?> WaitForJobEventsAsync(Guid jobId, CancellationToken cancellationToken = default) => _client.WaitForJobEventsAsync(jobId, cancellationToken);
    public Task<DesktopJob?> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default) => _client.RetryJobAsync(jobId, cancellationToken);
    public Task<DesktopMeetingCancellation?> CancelMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.CancelMeetingAsync(meetingId, cancellationToken);
    public Task<bool> DeleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default) => _client.DeleteMeetingAsync(meetingId, cancellationToken);
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
            if (!ok) { candidate.Dispose(); return false; }
            candidate.SessionChanged += PersistSession;
            _client.Dispose();
            _client.SessionChanged -= PersistSession;
            _client = candidate;
            return true;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => _client.RefreshAsync(cancellationToken);
    public Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => _client.EnsureAuthenticatedAsync(cancellationToken);
    public Task LogoutAsync(CancellationToken cancellationToken = default) => _client.LogoutAsync(cancellationToken);
    public Task<DesktopAgentEnrollment> LinkLocalAgentAsync(Guid installationId, Guid? agentId, string name, CancellationToken cancellationToken = default) =>
        _client.LinkLocalAgentAsync(installationId, agentId, name, cancellationToken);

    public Task<DesktopMeeting> CreateMeetingAsync(string title, string? description = null, CancellationToken cancellationToken = default) =>
        _client.CreateMeetingAsync(title, description, cancellationToken);

    public async Task<DesktopAgentEnrollment?> EnrollAgentAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        if (!HasSession) return null;
        return await _client.EnrollAgentAsync(name, secret, cancellationToken);
    }

    public Task<DesktopMeeting> ImportFileAsync(string path, string? title = null, CancellationToken cancellationToken = default) =>
        _client.ImportFileAsync(path, title, cancellationToken: cancellationToken);

    public void Dispose() => _client.Dispose();
}

using System.Text.Json;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;

namespace WhisperX_Atom_Desktop.Services;

public enum RecordingState
{
    Idle,
    Checking,
    Recording,
    Paused,
    Finalizing,
    Unavailable,
    Error
}

public sealed record RecordingSnapshot(
    RecordingState State,
    string? SessionId,
    Guid? MeetingId,
    long? MediaTimeMs,
    AgentIpcHealth? Health,
    string? Error);

public interface IRecorderService
{
    Task<AgentIpcResponse> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> PreflightAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> StartAsync(string title, Guid? meetingId = null, Guid? ownerUserId = null, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> PauseAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> ResumeAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> AddMarkerAsync(string eventType = "MARKER", CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> StopAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> RetryUploadAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> ConfigureAgentAsync(string serverUrl, Guid agentId, string token, string archiveRoot, string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> SetAudioDevicesAsync(string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> SetRecordingProfileAsync(string recordingProfile, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> TestAudioSourceAsync(string? deviceId, bool systemAudio = false, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> SetArchiveRootAsync(string archiveRoot, CancellationToken cancellationToken = default);
}

public interface IBackendService : IDisposable
{
    string ApiUrl { get; }
    bool HasSession { get; }
    string? SessionCookie { get; }
    DesktopAuthState AuthState { get; }
    DateTimeOffset? SessionExpiresAtUtc { get; }
    bool CanUseOffline { get; }
    void ApplySettings(DesktopSettings settings);
    Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default);
    Task<DesktopSystemStatus?> GetSystemStatusAsync(CancellationToken cancellationToken = default);
    Task<DesktopSystemVersion?> GetSystemVersionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopMeeting>> GetMeetingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopMeeting>> GetMeetingsPageAsync(int limit, int offset, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopTranscriptRegistry>> GetTranscriptRegistryPageAsync(int limit, int offset, string? search = null, string? status = null, DateTimeOffset? dateFrom = null, DateTimeOffset? dateTo = null, CancellationToken cancellationToken = default);
    Task<DesktopCurrentUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopAgent>> GetAgentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopAssistantConversation>> GetAssistantConversationsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<DesktopAssistantConversation?> CreateAssistantConversationAsync(string title, string scopeType, Guid? meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopAssistantMessage>> GetAssistantMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<DesktopAssistantMessageCreateResult?> CreateAssistantMessageAsync(Guid conversationId, string content, Guid? retryOf = null, CancellationToken cancellationToken = default);
    Task<DesktopAssistantMessage?> WaitForAssistantMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default);
    Task<bool> UpdateAssistantConversationAsync(Guid conversationId, string? title = null, bool? archived = null, CancellationToken cancellationToken = default);
    Task<bool> DeleteAssistantConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<DesktopAssistantQuery?> CreateAssistantQueryAsync(string query, Guid? meetingId = null, CancellationToken cancellationToken = default);
    Task<DesktopAssistantQuery?> GetAssistantQueryAsync(Guid queryId, CancellationToken cancellationToken = default);
    Task<bool> LoginAsync(string apiUrl, string username, string password, CancellationToken cancellationToken = default);
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);
    Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<DesktopAgentEnrollment> LinkLocalAgentAsync(Guid installationId, Guid? agentId, string name, CancellationToken cancellationToken = default);
    Task<DesktopAgentBootstrapResult> BootstrapLocalAgentAsync(Guid installationId, Guid? agentId, string name, CancellationToken cancellationToken = default);
    Task<DesktopMeeting> CreateMeetingAsync(string title, string? description = null, CancellationToken cancellationToken = default);
    Task<DesktopAgentEnrollment?> EnrollAgentAsync(string name, string secret, CancellationToken cancellationToken = default);
    Task<DesktopMeeting> ImportFileAsync(string path, string? title = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopJob>> GetJobsAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<DesktopJob?> WaitForJobEventsAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<DesktopJob?> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<DesktopMeetingCancellation?> CancelMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<bool> DeleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<DesktopTranscript?> GetTranscriptAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopTranscriptVersion>> GetTranscriptVersionsAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<DesktopTranscriptVersion?> EditTranscriptSegmentAsync(Guid meetingId, Guid segmentId, string text, CancellationToken cancellationToken = default);
    Task<DesktopJob?> ReprocessTranscriptAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopSpeaker>> GetSpeakersAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<bool> RenameSpeakerAsync(Guid meetingId, Guid speakerId, string displayName, CancellationToken cancellationToken = default);
    Task<bool> MergeSpeakersAsync(Guid meetingId, Guid sourceSpeakerId, Guid targetSpeakerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopMedia>> GetMediaAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<DesktopSummary?> GetSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<bool> RebuildSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopDecision>> GetDecisionsAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopTask>> GetTasksAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<bool> UpdateTaskAsync(DesktopTask task, CancellationToken cancellationToken = default);
    Task<string?> DownloadPreviewAsync(Guid mediaId, CancellationToken cancellationToken = default);
}

public sealed record AssistantEvidenceItem(
    string? MeetingId,
    string SegmentId,
    long? StartMs,
    long? EndMs,
    string? Timecode,
    string? Speaker,
    string? Text);
public sealed record DesktopAssistantConversation(string Id, string Title, string ScopeType, string? MeetingId, bool Archived, DateTime CreatedAt, DateTime UpdatedAt)
{
    public string ContextLabel => ScopeType.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) ? "Вся история" : "Совещание";
}
public sealed record DesktopAssistantMessage(string Id, string ConversationId, string Role, string Content, string Status, string? VoiceAnswer, JsonDocument Evidence, string? ErrorCode, string? QueryId, DateTime CreatedAt, DateTime? CompletedAt)
{
    public bool IsUser => Role.Equals("USER", StringComparison.OrdinalIgnoreCase);
    public string StatusText => UiStatusMapper.Text(Status);
}
public sealed record DesktopAssistantMessageCreateResult(DesktopAssistantMessage UserMessage, DesktopAssistantMessage AssistantMessage, string QueryId);

public sealed record MeetingNavigationTarget(string MeetingId, string? SegmentId = null, long? StartMs = null);
public sealed record MeetingNavigationRequest(FrontendServices Services, MeetingNavigationTarget Target);

public sealed class FrontendNavigationState
{
    public MeetingNavigationTarget? PendingMeetingTarget { get; set; }
}

public interface ISettingsStore
{
    DesktopSettings Load();
    void Save(DesktopSettings settings);
}

public static class AgentStatusFormatter
{
    public static string Format(AgentIpcResponse response)
    {
        if (!response.Ok) return "Recorder Agent: локальный сервис сообщил об ошибке";
        if (response.Health is null) return "Recorder Agent: локальный сервис недоступен";
        return response.Health.ServerConnectionState switch
        {
            "CONNECTED" => "Recorder Agent подключён к серверу",
            "AUTH_REJECTED" => "Recorder Agent: сервер отклонил токен, выполняется восстановление",
            "SERVER_UNAVAILABLE" => "Recorder Agent: сервер недоступен, запись сохраняется локально",
            "NOT_CONFIGURED" => "Recorder Agent не настроен",
            "SERVER_ERROR" => "Recorder Agent: ошибка сервера, повторная попытка позже",
            _ => "Recorder Agent: проверка подключения"
        };
    }
}

public sealed record AudioDeviceOption(string Id, string Name, bool IsDefault, string State)
{
    public string DisplayName => IsDefault ? $"{Name} · по умолчанию" : Name;
}

public sealed record RecordingProfileOption(string Code, string DisplayName);

public sealed class FrontendServices
{
    public FrontendServices(IRecorderService recorder, IBackendService backend, ISettingsStore settings)
    {
        Recorder = recorder;
        Backend = backend;
        Settings = settings;
        AgentBootstrap = new AgentBootstrapCoordinator(this);
    }

    public IRecorderService Recorder { get; }
    public IBackendService Backend { get; }
    public ISettingsStore Settings { get; }
    public FrontendNavigationState Navigation { get; } = new();
    public AgentBootstrapCoordinator AgentBootstrap { get; }
    public event Action? LoggedOut;

    public void RaiseLoggedOut() => LoggedOut?.Invoke();
}

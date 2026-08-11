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
    Task<AgentIpcResponse> StartAsync(string title, Guid? meetingId = null, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> PauseAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> ResumeAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> AddMarkerAsync(string eventType = "MARKER", CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> StopAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> RetryUploadAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> ConfigureAgentAsync(string serverUrl, Guid agentId, string token, string archiveRoot, string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> SetAudioDevicesAsync(string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> SetArchiveRootAsync(string archiveRoot, CancellationToken cancellationToken = default);
}

public interface IBackendService : IDisposable
{
    string ApiUrl { get; }
    bool HasSession { get; }
    string? SessionCookie { get; }
    void ApplySettings(DesktopSettings settings);
    Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default);
    Task<DesktopSystemStatus?> GetSystemStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopMeeting>> GetMeetingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopMeeting>> GetMeetingsPageAsync(int limit, int offset, CancellationToken cancellationToken = default);
    Task<DesktopCurrentUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopAgent>> GetAgentsAsync(CancellationToken cancellationToken = default);
    Task<DesktopAssistantQuery?> CreateAssistantQueryAsync(string query, Guid? meetingId = null, CancellationToken cancellationToken = default);
    Task<DesktopAssistantQuery?> GetAssistantQueryAsync(Guid queryId, CancellationToken cancellationToken = default);
    Task<bool> LoginAsync(string apiUrl, string username, string password, CancellationToken cancellationToken = default);
    Task<DesktopMeeting> CreateMeetingAsync(string title, string? description = null, CancellationToken cancellationToken = default);
    Task<DesktopAgentEnrollment?> EnrollAgentAsync(string name, string secret, CancellationToken cancellationToken = default);
    Task<DesktopMeeting> ImportFileAsync(string path, string? title = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopJob>> GetJobsAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<DesktopJob?> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<DesktopTranscript?> GetTranscriptAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopSpeaker>> GetSpeakersAsync(Guid meetingId, CancellationToken cancellationToken = default);
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

public sealed record AudioDeviceOption(string Id, string Name, bool IsDefault, string State)
{
    public string DisplayName => IsDefault ? $"{Name} · по умолчанию" : Name;
}

public sealed class FrontendServices(
    IRecorderService recorder,
    IBackendService backend,
    ISettingsStore settings)
{
    public IRecorderService Recorder { get; } = recorder;
    public IBackendService Backend { get; } = backend;
    public ISettingsStore Settings { get; } = settings;
    public FrontendNavigationState Navigation { get; } = new();
}

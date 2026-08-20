using System.Text.Json;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;

namespace WhisperX_Atom_Desktop.Services;

public enum RecordingState
{
    Idle,
    Checking,
    Starting,
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
    IAsyncEnumerable<AgentIpcResponse> SubscribeAudioDeviceEventsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentIpcResponse> SubscribeAudioTelemetryAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> PreflightAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> StartAsync(string title, Guid? meetingId = null, Guid? ownerUserId = null, bool localOnly = false, CancellationToken cancellationToken = default, string acousticProfile = "AUTO");
    Task<AgentIpcResponse> PauseAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> ResumeAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> AddMarkerAsync(string eventType = "MARKER", CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> StopAsync(CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> RetryUploadAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> ConfigureAgentAsync(string serverUrl, Guid agentId, string token, string archiveRoot, string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> UpdateServerUrlAsync(string serverUrl, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> SetAudioDevicesAsync(string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> SetRecordingProfileAsync(string recordingProfile, CancellationToken cancellationToken = default);
    Task<AgentIpcResponse> TestAudioSourceAsync(string? deviceId, bool systemAudio = false, CancellationToken cancellationToken = default, int durationSeconds = 3);
    Task<AgentIpcResponse> SetArchiveRootAsync(string archiveRoot, CancellationToken cancellationToken = default);
}

public interface IBackendService : IDisposable
{
    string ApiUrl { get; }
    bool HasSession { get; }
    string? SessionCookie { get; }
    DesktopAuthState AuthState { get; }
    string? LastConnectionErrorCode { get; }
    DateTimeOffset? SessionExpiresAtUtc { get; }
    bool CanUseOffline { get; }
    void ApplySettings(DesktopSettings settings);
    Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default);
    Task<DesktopSystemStatus?> GetSystemStatusAsync(CancellationToken cancellationToken = default);
    Task<DesktopProcessingReadiness?> GetProcessingReadinessAsync(CancellationToken cancellationToken = default);
    Task<DesktopSystemVersion?> GetSystemVersionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopMeeting>> GetMeetingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopMeeting>> GetMeetingsPageAsync(int limit, int offset, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopTranscriptRegistry>> GetTranscriptRegistryPageAsync(int limit, int offset, string? search = null, string? status = null, DateTimeOffset? dateFrom = null, DateTimeOffset? dateTo = null, CancellationToken cancellationToken = default);
    Task<DesktopCurrentUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopAgent>> GetAgentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopAssistantConversation>> GetAssistantConversationsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<DesktopAssistantConversation?> CreateAssistantConversationAsync(string title, string scopeType, Guid? meetingId, string? assistantMode = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopAssistantMessage>> GetAssistantMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<DesktopAssistantMessageCreateResult?> CreateAssistantMessageAsync(Guid conversationId, string content, Guid? retryOf = null, CancellationToken cancellationToken = default);
    Task<DesktopAssistantMessage?> WaitForAssistantMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default);
    Task<bool> UpdateAssistantConversationAsync(Guid conversationId, string? title = null, bool? archived = null, CancellationToken cancellationToken = default);
    Task<bool> DeleteAssistantConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<DesktopAssistantQuery?> CreateAssistantQueryAsync(string query, Guid? meetingId = null, string? assistantMode = null, CancellationToken cancellationToken = default);
    Task<DesktopAssistantQuery?> GetAssistantQueryAsync(Guid queryId, CancellationToken cancellationToken = default);
    Task<DesktopAssistantRequestAccepted?> CreateAssistantRequestAsync(string question, string? requestedMode = "AUTO", Guid? activeMeetingId = null, Guid? conversationId = null, string source = "DESKTOP", string? commandId = null, string? traceId = null, CancellationToken cancellationToken = default);
    Task<bool> PublishLiveMeetingSegmentsAsync(Guid meetingId, Guid? recordingSessionId, IReadOnlyList<DesktopLiveMeetingSegment> segments, CancellationToken cancellationToken = default);
    Task<bool> LoginAsync(string apiUrl, string username, string password, CancellationToken cancellationToken = default);
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);
    Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<DesktopAgentEnrollment> LinkLocalAgentAsync(Guid installationId, Guid? agentId, string name, CancellationToken cancellationToken = default);
    Task<DesktopAgentBootstrapResult> BootstrapLocalAgentAsync(Guid installationId, Guid? agentId, string name, CancellationToken cancellationToken = default);
    Task<DesktopAgentEnrollment> ReenrollAgentAsync(Guid agentId, CancellationToken cancellationToken = default);
    Task<DesktopMeeting> CreateMeetingAsync(string title, string? description = null, CancellationToken cancellationToken = default);
    Task<DesktopAgentEnrollment?> EnrollAgentAsync(string name, string secret, CancellationToken cancellationToken = default);
    Task<DesktopMeeting> ImportFileAsync(string path, string? title = null, CancellationToken cancellationToken = default);
    Task<DesktopMeeting> ImportFileWithProgressAsync(string path, string? title, IProgress<DesktopImportProgress>? progress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopJob>> GetJobsAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<DesktopJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
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
    Task<DesktopRegistryPage<DesktopSummaryRegistryRow>> GetSummaryRegistryPageAsync(int page = 1, int pageSize = 50, string? search = null, string? status = null, Guid? meetingId = null, string? sort = null, CancellationToken cancellationToken = default);
    Task<DesktopRegistryPage<DesktopSpeakerRegistryRow>> GetSpeakerRegistryPageAsync(int page = 1, int pageSize = 50, string? search = null, string? status = null, Guid? meetingId = null, string? sort = null, CancellationToken cancellationToken = default);
    Task<DesktopRegistryPage<DesktopActionItemRegistryRow>> GetActionItemRegistryPageAsync(int page = 1, int pageSize = 50, string? search = null, string? status = null, Guid? meetingId = null, string? sort = null, CancellationToken cancellationToken = default);
    Task<DesktopJob?> QueueSummaryRebuildAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<bool> RebuildSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopDecision>> GetDecisionsAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopTask>> GetTasksAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<bool> UpdateTaskAsync(DesktopTask task, CancellationToken cancellationToken = default);
    Task<string?> DownloadPreviewAsync(Guid mediaId, CancellationToken cancellationToken = default);
    Task<bool> DownloadMediaAsync(Guid mediaId, string destinationPath, CancellationToken cancellationToken = default);
    Task<bool> DownloadOriginalMediaAsync(Guid mediaId, string destinationPath, CancellationToken cancellationToken = default);
}

public sealed record AssistantEvidenceItem(
    string? MeetingId,
    string SegmentId,
    long? StartMs,
    long? EndMs,
    string? Timecode,
    string? Speaker,
    string? Text);
public sealed record DesktopAssistantConversation(string Id, string Title, string ScopeType, string? MeetingId, bool Archived, DateTime CreatedAt, DateTime UpdatedAt, string AssistantMode = "MEETING_MEMORY")
{
    public string ContextLabel => AssistantMode.ToUpperInvariant() switch
    {
        "GENERAL_CHAT" => "Обычный чат",
        "CURRENT_MEETING" => "Текущее совещание",
        "LIVE_MEETING" => "Текущее совещание · live",
        _ when ScopeType.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) => "Память совещаний",
        _ => "Совещание"
    };
}
public sealed record DesktopAssistantMessage(string Id, string ConversationId, string Role, string Content, string Status, string? VoiceAnswer, JsonDocument Evidence, string? ErrorCode, string? QueryId, DateTime CreatedAt, DateTime? CompletedAt, JsonElement? Timings = null)
{
    public bool IsUser => Role.Equals("USER", StringComparison.OrdinalIgnoreCase);
    public string StatusText => UiStatusMapper.Text(Status);
    public string TimingsText
    {
        get
        {
            if (Timings is not JsonElement value || value.ValueKind != JsonValueKind.Object) return string.Empty;
            var parts = new List<string>();
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var milliseconds)) continue;
                parts.Add($"{property.Name}: {milliseconds:0} мс");
            }
            return string.Join(" · ", parts);
        }
    }
}
public sealed record DesktopAssistantMessageCreateResult(DesktopAssistantMessage UserMessage, DesktopAssistantMessage AssistantMessage, string QueryId);

public sealed record MeetingNavigationTarget(string MeetingId, string? SegmentId = null, long? StartMs = null);
public sealed record MeetingNavigationRequest(FrontendServices Services, MeetingNavigationTarget Target);

public sealed class ActiveMeetingContext
{
    private readonly object _gate = new();
    public Guid? MeetingId { get { lock (_gate) return _meetingId; } }
    public string? Title { get { lock (_gate) return _title; } }
    private Guid? _meetingId;
    private string? _title;
    public void Set(Guid meetingId, string? title) { lock (_gate) { _meetingId = meetingId; _title = title; } }
    public void Clear() { lock (_gate) { _meetingId = null; _title = null; } }
}

/// <summary>
/// Keeps only opaque server conversation identifiers for voice follow-ups.
/// The key deliberately includes the authenticated user and the resolved
/// scope, so a question from another meeting or user cannot inherit context.
/// </summary>
public sealed class VoiceAssistantConversationStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<(Guid UserId, string Mode, Guid? MeetingId), (Guid ConversationId, DateTimeOffset ExpiresAt)> _items = new();

    public VoiceAssistantConversationStore(string? root = null, Func<DateTimeOffset>? clock = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "Assistant");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "voice-conversations.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Load();
    }

    public Guid? Get(Guid userId, string mode, Guid? meetingId)
    {
        lock (_gate)
        {
            Prune();
            return _items.TryGetValue((userId, NormalizeMode(mode), meetingId), out var item) ? item.ConversationId : null;
        }
    }

    public void Set(Guid userId, string mode, Guid? meetingId, Guid conversationId)
    {
        lock (_gate)
        {
            Prune();
            _items[(userId, NormalizeMode(mode), meetingId)] = (conversationId, _clock().AddMinutes(30));
            PersistLocked();
        }
    }

    public void Clear() { lock (_gate) { _items.Clear(); PersistLocked(); } }

    private void Prune()
    {
        foreach (var key in _items.Where(item => item.Value.ExpiresAt <= _clock()).Select(item => item.Key).ToArray())
            _items.Remove(key);
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var persisted = JsonSerializer.Deserialize<Dictionary<string, PersistedConversation>>(File.ReadAllText(_path)) ?? [];
                foreach (var item in persisted)
                {
                    var parts = item.Key.Split('|', 3);
                    if (parts.Length != 3 || !Guid.TryParse(parts[0], out var userId) || !Guid.TryParse(item.Value.ConversationId, out var conversationId)) continue;
                    Guid? meetingId = Guid.TryParse(parts[2], out var parsedMeeting) ? parsedMeeting : null;
                    _items[(userId, parts[1], meetingId)] = (conversationId, item.Value.ExpiresAt);
                }
                Prune();
            }
            catch { _items.Clear(); }
        }
    }

    private void PersistLocked()
    {
        try
        {
            var persisted = _items.ToDictionary(
                item => $"{item.Key.UserId:N}|{item.Key.Mode}|{item.Key.MeetingId?.ToString("N") ?? "-"}",
                item => new PersistedConversation(item.Value.ConversationId.ToString("N"), item.Value.ExpiresAt),
                StringComparer.Ordinal);
            var temporary = _path + ".part";
            File.WriteAllText(temporary, JsonSerializer.Serialize(persisted));
            File.Move(temporary, _path, true);
        }
        catch { /* runtime persistence is best effort; identifiers never contain user content */ }
    }

    private sealed record PersistedConversation(string ConversationId, DateTimeOffset ExpiresAt);

    private static string NormalizeMode(string? mode) => string.Equals(mode, "MEETING_HISTORY", StringComparison.OrdinalIgnoreCase)
        ? "MEETING_MEMORY"
        : (mode ?? "AUTO").Trim().ToUpperInvariant();
}

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
        if (!response.Ok)
        {
            return response.Error?.ToUpperInvariant() switch
            {
                "RECORDER_IPC_ACCESS_DENIED" => "Recorder Agent: доступ к локальному IPC запрещён",
                "RECORDER_IPC_TIMEOUT" or "RECORDER_HOST_PIPE_UNRESPONSIVE" => "Recorder Agent: локальный Host не отвечает",
                "RECORDER_HOST_NOT_RUNNING" => "Recorder Agent: локальный Host не запущен",
                "RECORDER_HOST_INIT_FAILED" => "Recorder Agent: Host не завершил инициализацию; откройте диагностику",
                "SPOOL_READONLY" => "Recorder Agent: локальное хранилище доступно только для чтения",
                "RECORDER_RUNTIME_LEASE_HELD" => "Recorder Agent: другой Host или legacy Service уже владеет хранилищем",
                "RECORDER_HOST_BUILD_MISMATCH" or "RECORDER_HOST_DUPLICATE" or "RECORDER_HOST_UPDATE_REQUIRED" or "RECORDER_HOST_UPDATE_RESTART_REQUIRED" => "Recorder Agent: конфликт установленного Host; требуется оставить одну актуальную сборку",
                "AUDIO_INPUT_NODE_CREATE_FAILED" => "Recorder Agent: Windows не создала вход микрофона",
                "AUDIO_GRAPH_UNRECOVERABLE" => "Recorder Agent: AudioGraph аварийно остановил захват",
            "AUDIO_PIPELINE_OVERRUN" => "Recorder Agent: обработка аудиокадров не успевает за захватом",
            "AUDIO_FRAME_GAP" => "Recorder Agent: обнаружен пропуск аудиокадров",
            "AUDIO_FRAME_OVERLAP" => "Recorder Agent: обнаружен повтор аудиокадров",
            "AUDIO_FRAME_FORMAT_MISMATCH" => "Recorder Agent: формат аудиопотока изменился во время записи",
            "AUDIO_FRAME_SIZE_MISMATCH" => "Recorder Agent: получен повреждённый аудиокадр",
                "AUDIO_CAPTURE_START_TIMEOUT" => "Recorder Agent: микрофон не начал отдавать кадры вовремя",
                "AUDIO_CAPTURE_BUSY" => "Recorder Agent: текущая запись уже использует микрофон",
                "AUDIO_DEVICE_ACCESS_DENIED" => "Recorder Agent: Windows запретила доступ к микрофону",
                "AUDIO_NO_FRAMES" or "AUDIO_NO_DATA" => "Recorder Agent: от микрофона не поступают аудиокадры",
                "AUDIO_BUFFER_FORMAT_MISMATCH" => "Recorder Agent: несовместимый формат аудиобуфера",
                "SERVER_NETWORK_UNREACHABLE" => "Recorder Agent: сервер недоступен по сети; запись сохраняется локально",
                "SERVER_TIMEOUT" => "Recorder Agent: сервер не ответил вовремя; запись сохраняется локально",
                "SERVER_NOT_CONFIGURED" => "Recorder Agent: адрес LAN-сервера не настроен",
                "PROCESSING_UNAVAILABLE" => "Recorder Agent: WhisperX worker временно недоступен; запись сохранена локально",
                "UPLOAD_RETRY_PENDING" => "Recorder Agent: отправка ожидает повторной попытки",
                "AGENT_AUTH_REJECTED" => "Recorder Agent: сервер отклонил авторизацию",
                _ when !string.IsNullOrWhiteSpace(response.Error) => $"Recorder Agent: {response.Error}",
                _ => "Recorder Agent: локальный сервис сообщил об ошибке"
            };
        }
        if (response.Health is null) return "Recorder Agent: локальный сервис недоступен";
        return response.Health.ServerConnectionState switch
        {
            "CONNECTED" => "Recorder Agent подключён к серверу",
            "AUTH_REJECTED" => "Recorder Agent: сервер отклонил токен, выполняется восстановление",
            "SERVER_UNAVAILABLE" => "Recorder Agent: сервер недоступен, запись сохраняется локально",
            "SERVER_NETWORK_UNREACHABLE" => "Recorder Agent: сервер недоступен по сети, запись сохраняется локально",
            "SERVER_TIMEOUT" => "Recorder Agent: сервер не ответил вовремя, запись сохраняется локально",
            "NOT_CONFIGURED" => "Recorder Agent не настроен",
            "SERVER_ERROR" => "Recorder Agent: ошибка сервера, повторная попытка позже",
            _ => "Recorder Agent: проверка подключения"
        };
    }
}

public sealed record AudioDeviceOption(string Id, string Name, bool IsDefault, string State)
{
    public string DisplayName => IsDefault ? $"{Name} · по умолчанию" : Name;

    public string StateText => State?.ToUpperInvariant() switch
    {
        "ACTIVE" or "READY" or "DISCOVERED" => "Доступно",
        "DEFAULT" => "По умолчанию",
        "UNAVAILABLE" or "DEVICE_LOST" => "Недоступно",
        _ => "Проверяется"
    };
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
        JobTracker = new ProcessingJobTracker(backend);
        RecorderService = new RecorderServiceController(recorder);
        RecordingCommands = new RecordingCommandService(recorder);
        VoiceHost = new VoiceHostController(this);
        Diagnostics = new ClientRuntimeDiagnostics(this);
        Updates = new ClientUpdateService(this);
    }

    public IRecorderService Recorder { get; }
    public IBackendService Backend { get; }
    public ISettingsStore Settings { get; }
    public FrontendNavigationState Navigation { get; } = new();
    public ActiveMeetingContext ActiveMeeting { get; } = new();
    public VoiceAssistantConversationStore VoiceAssistantConversations { get; } = new();
    public AssistantDeliveryStore AssistantDelivery { get; } = new();
    public AgentBootstrapCoordinator AgentBootstrap { get; }
    public ProcessingJobTracker JobTracker { get; }
    public RecorderServiceController RecorderService { get; }
    public RecordingCommandService RecordingCommands { get; }
    public VoiceHostController VoiceHost { get; }
    public ClientRuntimeDiagnostics Diagnostics { get; }
    public ClientUpdateService Updates { get; }
    public event Action<Guid, Guid?>? AssistantResultAvailable
    {
        add => VoiceHost.AssistantResultAvailable += value;
        remove => VoiceHost.AssistantResultAvailable -= value;
    }

    public async Task<ProductRuntimeSnapshot> GetRuntimeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        AgentIpcResponse? recorder = null;
        DesktopSystemStatus? backend = null;
        DesktopProcessingReadiness? processing = null;
        try { recorder = await Recorder.GetHealthAsync(cancellationToken).ConfigureAwait(false); } catch { }
        try { backend = await Backend.GetSystemStatusAsync(cancellationToken).ConfigureAwait(false); } catch { }
        try { processing = await Backend.GetProcessingReadinessAsync(cancellationToken).ConfigureAwait(false); } catch { }
        return ProductRuntimeSnapshot.From(Backend.AuthState, recorder, backend, processing);
    }
    public event Action? LoggedOut;

    public void RaiseLoggedOut() => LoggedOut?.Invoke();

    public void ClearUserAssistantState()
    {
        ActiveMeeting.Clear();
        VoiceAssistantConversations.Clear();
        VoiceHost.ClearAssistantState();
    }
}

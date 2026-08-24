using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class AssistantContextOption
{
    public AssistantContextOption(string label, string? meetingId, DesktopMeeting? meeting)
    {
        Label = label;
        MeetingId = meetingId;
        Meeting = meeting;
    }

    public string Label { get; }
    public string? MeetingId { get; }
    public DesktopMeeting? Meeting { get; }
    public bool IsGlobal => string.IsNullOrWhiteSpace(MeetingId);
}

public sealed class AssistantModeOption
{
    public AssistantModeOption(string label, string value) { Label = label; Value = value; }
    public string Label { get; }
    public string Value { get; }
}

public sealed class AssistantViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private CancellationTokenSource? _queryCts;
    private bool _isLoading;
    private bool _isAsking;
    private bool _isGlobalAllowed;
    private string _question = string.Empty;
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;
    private string _answerText = string.Empty;
    private string _voiceAnswerText = string.Empty;
    private string _answerTypeText = string.Empty;
    private string _sourceSummaryText = string.Empty;
    private string _missingFieldsText = string.Empty;
    private string _roleText = string.Empty;
    private AssistantContextOption? _selectedContext;
    private AssistantModeOption? _selectedMode;
    private DesktopAssistantConversation? _selectedConversation;
    private DesktopAssistantMessage? _selectedMessage;

    public AssistantViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<AssistantContextOption> Contexts { get; } = [];
    public ObservableCollection<AssistantModeOption> Modes { get; } =
    [
        new("Обычный чат", "GENERAL_CHAT"),
        new("История совещаний", "MEETING_MEMORY"),
        new("Текущее совещание", "CURRENT_MEETING")
    ];
    public ObservableCollection<DesktopAssistantConversation> Conversations { get; } = [];
    public ObservableCollection<DesktopAssistantMessage> Messages { get; } = [];
    public ObservableCollection<AssistantEvidenceItem> Evidence { get; } = [];

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsAsking { get => _isAsking; private set => SetProperty(ref _isAsking, value); }
    public bool IsGlobalAllowed { get => _isGlobalAllowed; private set => SetProperty(ref _isGlobalAllowed, value); }
    public string Question { get => _question; set => SetProperty(ref _question, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string AnswerText { get => _answerText; private set => SetProperty(ref _answerText, value); }
    public string VoiceAnswerText { get => _voiceAnswerText; private set => SetProperty(ref _voiceAnswerText, value); }
    public string AnswerTypeText { get => _answerTypeText; private set => SetProperty(ref _answerTypeText, value); }
    public string SourceSummaryText { get => _sourceSummaryText; private set => SetProperty(ref _sourceSummaryText, value); }
    public string MissingFieldsText { get => _missingFieldsText; private set => SetProperty(ref _missingFieldsText, value); }
    public string RoleText { get => _roleText; private set => SetProperty(ref _roleText, value); }
    public bool HasConversations => Conversations.Count > 0;
    public bool HasMessages => Messages.Count > 0;
    /// <summary>Whether the selected chat still has server-side work queued.</summary>
    public bool HasPendingMessages => Messages.Any(item => !item.IsUser && !IsTerminalStatus(item.Status));
    public bool HasAnswer => !string.IsNullOrWhiteSpace(AnswerText);
    public bool HasEvidence => Evidence.Count > 0;
    public bool HasAnswerWithoutEvidence => HasAnswer && !HasEvidence;
    public bool CanAsk => SelectedConversation is not null && !IsLoading && !IsAsking;

    public AssistantContextOption? SelectedContext
    {
        get => _selectedContext;
        set
        {
            if (!SetProperty(ref _selectedContext, value)) return;
            OnPropertyChanged(nameof(IsGlobalContext));
            OnPropertyChanged(nameof(SelectedMeetingId));
        }
    }

    public AssistantModeOption? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value)) return;
            OnPropertyChanged(nameof(IsGeneralChat));
            OnPropertyChanged(nameof(IsMeetingMemory));
            OnPropertyChanged(nameof(IsCurrentMeeting));
            if (value?.Value == "GENERAL_CHAT") SelectedContext = null;
            else if (value?.Value == "MEETING_MEMORY") SelectedContext = Contexts.FirstOrDefault(item => item.IsGlobal);
            else if (value?.Value == "CURRENT_MEETING") SelectedContext = Contexts.FirstOrDefault(item => !item.IsGlobal);
        }
    }

    public DesktopAssistantConversation? SelectedConversation
    {
        get => _selectedConversation;
        private set
        {
            if (!SetProperty(ref _selectedConversation, value)) return;
            OnPropertyChanged(nameof(CanAsk));
        }
    }

    public DesktopAssistantMessage? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (!SetProperty(ref _selectedMessage, value)) return;
            ApplySelectedMessage(value);
        }
    }

    public bool IsGlobalContext => SelectedContext?.IsGlobal == true;
    public bool IsGeneralChat => SelectedMode?.Value == "GENERAL_CHAT";
    public bool IsMeetingMemory => SelectedMode?.Value == "MEETING_MEMORY";
    public bool IsCurrentMeeting => SelectedMode?.Value == "CURRENT_MEETING";
    public string? SelectedMeetingId => SelectedContext?.MeetingId;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;
        StatusText = string.Empty;
        try
        {
            if (!await _services.Backend.CheckReadyAsync(cancellationToken))
            {
                ErrorText = "API недоступен. Проверьте backend и подключение.";
                return;
            }
            if (!await _services.Backend.EnsureAuthenticatedAsync(cancellationToken))
            {
                ErrorText = "Войдите в API, чтобы использовать ИИ-помощник.";
                return;
            }
            var user = await _services.Backend.GetCurrentUserAsync(cancellationToken);
            if (user is null)
            {
                ErrorText = "Не удалось определить текущего пользователя API.";
                return;
            }
            IsGlobalAllowed = user.IsPrivileged;
            RoleText = $"Роль API: {user.Role}";
            await LoadContextsAsync(cancellationToken);
            SelectedMode = Modes.FirstOrDefault();
            await LoadConversationsAsync(cancellationToken);
            StatusText = HasConversations ? "Выберите чат или создайте новый." : "Выберите режим и создайте чат.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ErrorText = SafeError(ex); }
        finally { IsLoading = false; OnPropertyChanged(nameof(CanAsk)); }
    }

    public async Task LoadConversationsAsync(CancellationToken cancellationToken = default)
    {
        var conversations = await _services.Backend.GetAssistantConversationsAsync(false, cancellationToken);
        Conversations.Clear();
        foreach (var conversation in conversations) Conversations.Add(conversation);
        OnPropertyChanged(nameof(HasConversations));
    }

    public async Task CreateConversationAsync(CancellationToken cancellationToken = default)
    {
        if (!IsGeneralChat && SelectedContext is null)
        {
            ErrorText = "Выберите встречу или глобальный контекст.";
            return;
        }
        var scope = IsGeneralChat ? "GENERAL" : IsMeetingMemory ? "GLOBAL" : "MEETING";
        Guid? meetingId = IsCurrentMeeting && Guid.TryParse(SelectedContext?.MeetingId, out var parsed) ? parsed : null;
        var conversation = await _services.Backend.CreateAssistantConversationAsync("Новый чат", scope, meetingId, SelectedMode?.Value, cancellationToken);
        if (conversation is null)
        {
            ErrorText = IsGeneralChat ? "Не удалось создать обычный чат." : "Для этого контекста ещё нет пригодной стенограммы.";
            return;
        }
        Conversations.Insert(0, conversation);
        OnPropertyChanged(nameof(HasConversations));
        await SelectConversationAsync(conversation, cancellationToken);
    }

    public async Task SelectConversationAsync(DesktopAssistantConversation? conversation, CancellationToken cancellationToken = default)
    {
        if (conversation is null) return;
        SelectedConversation = conversation;
        ErrorText = string.Empty;
        var messages = await _services.Backend.GetAssistantMessagesAsync(Guid.Parse(conversation.Id), cancellationToken);
        Messages.Clear();
        foreach (var message in messages) Messages.Add(message);
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasPendingMessages));
        SelectedMessage = Messages.LastOrDefault(item => !item.IsUser) ?? Messages.LastOrDefault();
        StatusText = Messages.LastOrDefault() is { } last ? DisplayStatus(last.Status, last.ErrorCode, last.ProcessingStage) : "Чат готов к вопросу.";
    }

    public async Task RefreshSelectedConversationAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedConversation is null) return;
        var selectedId = SelectedMessage?.Id;
        var messages = await _services.Backend.GetAssistantMessagesAsync(Guid.Parse(SelectedConversation.Id), cancellationToken);
        Messages.Clear();
        foreach (var message in messages) Messages.Add(message);
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasPendingMessages));
        SelectedMessage = Messages.FirstOrDefault(item => item.Id == selectedId)
            ?? Messages.LastOrDefault(item => !item.IsUser)
            ?? Messages.LastOrDefault();
        if (Messages.LastOrDefault() is { } last) StatusText = DisplayStatus(last.Status, last.ErrorCode, last.ProcessingStage);
    }

    public async Task AskAsync(CancellationToken pageToken, Guid? retryOf = null)
    {
        var question = Question.Trim();
        if (question.Length == 0) { ErrorText = "Введите вопрос."; return; }
        if (SelectedConversation is null)
        {
            await CreateConversationAsync(pageToken);
            if (SelectedConversation is null) return;
        }
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = CancellationTokenSource.CreateLinkedTokenSource(pageToken);
        var cancellationToken = _queryCts.Token;
        IsAsking = true;
        ErrorText = string.Empty;
        StatusText = "Запрос отправляется…";
        var acceptedByServer = false;
        try
        {
            var result = await _services.Backend.CreateAssistantMessageAsync(Guid.Parse(SelectedConversation!.Id), question, retryOf, cancellationToken);
            if (result is null)
            {
                StatusText = "Запрос не принят";
                ErrorText = "API не принял сообщение помощника.";
                return;
            }
            acceptedByServer = true;
            Messages.Add(result.UserMessage);
            Messages.Add(result.AssistantMessage);
            Question = string.Empty;
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(HasPendingMessages));
            SelectedMessage = result.AssistantMessage;
            StatusText = "Ответ готовится…";

            // The POST is durable. Bound the foreground wait so a stalled SSE
            // connection or a cold Qwen model cannot leave the composer in an
            // endless "sending" state. The page refresh loop observes the
            // same server-side message after this short window.
            DesktopAssistantMessage? completed;
            using (var responseWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                responseWait.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    completed = await _services.Backend.WaitForAssistantMessageAsync(
                        Guid.Parse(SelectedConversation.Id),
                        Guid.Parse(result.AssistantMessage.Id),
                        responseWait.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    completed = null;
                }
            }
            if (completed is not null) ReplaceMessage(completed);
            if (completed is null)
            {
                // HTTP 202 means the durable request was accepted.  A cold
                // model or a queued GPU job is normal background work, not a
                // client error and must not turn the page red.
                StatusText = "Ответ готовится на сервере — чат обновится автоматически";
                ErrorText = string.Empty;
                return;
            }
            SelectedMessage = completed;
            StatusText = DisplayStatus(completed.Status, completed.ErrorCode, completed.ProcessingStage);
            await LoadConversationsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (acceptedByServer) StatusText = "Запрос сохранён; ожидание приостановлено";
        }
        catch (Exception ex)
        {
            StatusText = acceptedByServer ? "Запрос принят; связь при ожидании прервалась" : "Не удалось отправить запрос";
            ErrorText = acceptedByServer ? string.Empty : SafeError(ex);
        }
        finally { IsAsking = false; OnPropertyChanged(nameof(CanAsk)); }
    }

    public async Task RetryAsync(DesktopAssistantMessage? message, CancellationToken cancellationToken = default)
    {
        if (message is null || message.IsUser || SelectedConversation is null) return;
        var index = Messages.IndexOf(message);
        var question = index > 0 && Messages[index - 1].IsUser ? Messages[index - 1].Content : string.Empty;
        if (string.IsNullOrWhiteSpace(question)) return;
        Question = question;
        if (Guid.TryParse(message.Id, out var retryId)) await AskAsync(cancellationToken, retryId);
    }

    public async Task DeleteConversationAsync(DesktopAssistantConversation? conversation, CancellationToken cancellationToken = default)
    {
        if (conversation is null || !await _services.Backend.DeleteAssistantConversationAsync(Guid.Parse(conversation.Id), cancellationToken)) return;
        Conversations.Remove(conversation);
        if (SelectedConversation?.Id == conversation.Id)
        {
            SelectedConversation = null;
            Messages.Clear();
            Evidence.Clear();
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(HasPendingMessages));
        }
        OnPropertyChanged(nameof(HasConversations));
    }

    public async Task ArchiveConversationAsync(DesktopAssistantConversation? conversation, CancellationToken cancellationToken = default)
    {
        if (conversation is null || !await _services.Backend.UpdateAssistantConversationAsync(Guid.Parse(conversation.Id), archived: true, cancellationToken: cancellationToken)) return;
        Conversations.Remove(conversation);
        OnPropertyChanged(nameof(HasConversations));
    }

    public async Task RenameConversationAsync(DesktopAssistantConversation? conversation, string title, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (conversation is null || title.Length is 0 or > 120) return;
        if (!await _services.Backend.UpdateAssistantConversationAsync(Guid.Parse(conversation.Id), title, null, cancellationToken)) return;
        var updated = conversation with { Title = title, UpdatedAt = DateTime.UtcNow };
        var index = Conversations.IndexOf(conversation);
        if (index >= 0) Conversations[index] = updated;
        if (SelectedConversation?.Id == conversation.Id) SelectedConversation = updated;
    }

    public void CancelPending()
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = null;
    }

    private async Task LoadContextsAsync(CancellationToken cancellationToken)
    {
        Contexts.Clear();
        // Every user gets a history scope; the server applies ownership/RBAC
        // filtering. Privileged users see the same scope labelled globally.
        Contexts.Add(new AssistantContextOption(IsGlobalAllowed ? "Вся история" : "Моя история", null, null));
        var meetings = await LoadAllMeetingsAsync(cancellationToken);
        foreach (var meeting in meetings.Where(item => item.Status is "READY" or "PARTIAL_READY" or "TRANSCRIPT_READY"))
            Contexts.Add(new AssistantContextOption(meeting.Title, meeting.Id, meeting));
        SelectedContext = Contexts.FirstOrDefault();
    }

    private async Task<IReadOnlyList<DesktopMeeting>> LoadAllMeetingsAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var result = new List<DesktopMeeting>();
        for (var offset = 0; ;)
        {
            var page = await _services.Backend.GetMeetingsPageAsync(pageSize, offset, cancellationToken);
            if (page.Count == 0) break;
            result.AddRange(page);
            if (page.Count < pageSize) break;
            offset += page.Count;
        }
        return result.OrderByDescending(item => item.CreatedAt).ToList();
    }

    private void ReplaceMessage(DesktopAssistantMessage message)
    {
        var index = Messages.ToList().FindIndex(item => item.Id == message.Id);
        if (index < 0) return;
        Messages[index] = message;
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasPendingMessages));
    }

    private void ApplySelectedMessage(DesktopAssistantMessage? message)
    {
        AnswerText = message is { IsUser: false } ? message.Content : string.Empty;
        VoiceAnswerText = message is { IsUser: false } ? message.VoiceAnswer ?? string.Empty : string.Empty;
        AnswerTypeText = string.Empty;
        SourceSummaryText = string.Empty;
        MissingFieldsText = string.Empty;
        if (message is { IsUser: false, AnswerMetadata: not null } metadata && metadata.AnswerMetadata.RootElement.ValueKind == JsonValueKind.Object)
        {
            var root = metadata.AnswerMetadata.RootElement;
            AnswerTypeText = root.TryGetProperty("answerType", out var answerType) && answerType.ValueKind == JsonValueKind.String
                ? answerType.GetString() ?? string.Empty
                : string.Empty;
            if (root.TryGetProperty("sourceRanges", out var sourceRanges) && sourceRanges.ValueKind == JsonValueKind.Array)
                SourceSummaryText = sourceRanges.GetArrayLength() == 0 ? string.Empty : $"Источники: {sourceRanges.GetArrayLength()} фрагм.";
            if (root.TryGetProperty("missingFields", out var missingFields) && missingFields.ValueKind == JsonValueKind.Array)
                MissingFieldsText = string.Join(", ", missingFields.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item))!);
        }
        Evidence.Clear();
        if (message?.Evidence.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in message.Evidence.RootElement.EnumerateArray())
            {
                var id = GetString(item, "segmentId");
                if (string.IsNullOrWhiteSpace(id)) continue;
                Evidence.Add(new AssistantEvidenceItem(
                    GetString(item, "meetingId"), id, GetLong(item, "startMs"), GetLong(item, "endMs"),
                    GetString(item, "timecode"), GetString(item, "speaker"), GetString(item, "text"),
                    GetString(item, "sourceTrackType"), GetString(item, "channelRole")));
            }
        }
        OnPropertyChanged(nameof(HasEvidence));
        OnPropertyChanged(nameof(HasAnswer));
        OnPropertyChanged(nameof(HasAnswerWithoutEvidence));
    }

    private static string? GetString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? GetLong(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private static string DisplayStatus(string status, string? errorCode = null, string? processingStage = null)
    {
        if (string.Equals(errorCode, "ASSISTANT_WAITING_FOR_GPU", StringComparison.OrdinalIgnoreCase))
            return "Мифодий ждёт освобождения GPU";
        if (string.Equals(errorCode, "ASSISTANT_GPU_BUSY_TIMEOUT", StringComparison.OrdinalIgnoreCase))
            return "GPU занят слишком долго — повторите вопрос позже";
        if (string.Equals(errorCode, "LOCAL_COMMAND_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "Нужна явная команда записи: начните или остановите запись голосом";
        if (string.Equals(errorCode, "VOICE_COMMAND_REPEAT_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "Повторите голосовую команду — состояние записи не изменено";
        if (string.Equals(errorCode, "VOICE_ASSISTANT_ACCEPTANCE_TIMEOUT", StringComparison.OrdinalIgnoreCase))
            return "Сервер принимает запрос дольше обычного";
        if (!string.IsNullOrWhiteSpace(processingStage))
        {
            var stageText = UiStatusMapper.Text($"PROCESSING_STAGE_{processingStage.Trim().ToUpperInvariant()}");
            if (!string.Equals(stageText, "Состояние не определено", StringComparison.Ordinal)) return stageText;
        }
        return status.ToUpperInvariant() switch
        {
        "QUEUED" => "Запрос в очереди",
        "RUNNING" => "Помощник обрабатывает вопрос",
        "READY" => "Ответ готов",
        "NEEDS_REVIEW" => "Ответ требует проверки источников",
        "ANSWERED" => "Ответ готов",
        "ANSWERED_WITH_WARNING" => "Ответ готов с предупреждением",
        "NO_EVIDENCE" => "В источниках нет подтверждённого ответа",
        "LIVE_MEETING_NOT_READY" => "Свежий live-фрагмент ещё не готов",
        "GROUNDING_REJECTED" => "Ответ отклонён проверкой источников",
        "LLM_UNAVAILABLE" => "ИИ-помощник временно недоступен",
        "CLARIFICATION_REQUIRED" => "Нужно уточнить контекст вопроса",
        "FAILED" => "Помощник завершил запрос с ошибкой",
            _ => "Состояние неизвестно"
        };
    }

    private static bool IsTerminalStatus(string? status) => status is not null &&
        (status.Equals("READY", StringComparison.OrdinalIgnoreCase)
         || status.Equals("ANSWERED", StringComparison.OrdinalIgnoreCase)
         || status.Equals("ANSWERED_WITH_WARNING", StringComparison.OrdinalIgnoreCase)
         || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
         || status.Equals("NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase)
         || status.Equals("NO_EVIDENCE", StringComparison.OrdinalIgnoreCase)
         || status.Equals("GROUNDING_REJECTED", StringComparison.OrdinalIgnoreCase)
         || status.Equals("LLM_UNAVAILABLE", StringComparison.OrdinalIgnoreCase));
    private static string SafeError(Exception ex) => UiErrorFormatter.Format(ex, "Помощник не выполнил запрос.");
}

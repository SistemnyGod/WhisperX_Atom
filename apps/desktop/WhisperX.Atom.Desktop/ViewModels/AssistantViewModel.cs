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
    private string _roleText = string.Empty;
    private AssistantContextOption? _selectedContext;

    public AssistantViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<AssistantContextOption> Contexts { get; } = [];
    public ObservableCollection<AssistantEvidenceItem> Evidence { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsAsking
    {
        get => _isAsking;
        private set => SetProperty(ref _isAsking, value);
    }

    public bool IsGlobalAllowed
    {
        get => _isGlobalAllowed;
        private set => SetProperty(ref _isGlobalAllowed, value);
    }

    public string Question
    {
        get => _question;
        set => SetProperty(ref _question, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string AnswerText
    {
        get => _answerText;
        private set
        {
            if (!SetProperty(ref _answerText, value)) return;
            OnPropertyChanged(nameof(HasAnswer));
            OnPropertyChanged(nameof(HasAnswerWithoutEvidence));
        }
    }

    public string VoiceAnswerText
    {
        get => _voiceAnswerText;
        private set => SetProperty(ref _voiceAnswerText, value);
    }

    public string RoleText
    {
        get => _roleText;
        private set => SetProperty(ref _roleText, value);
    }

    public AssistantContextOption? SelectedContext
    {
        get => _selectedContext;
        set
        {
            if (!SetProperty(ref _selectedContext, value)) return;
            if (IsAsking) _queryCts?.Cancel();
            OnPropertyChanged(nameof(IsGlobalContext));
            OnPropertyChanged(nameof(SelectedMeetingId));
            if (!IsAsking) StatusText = value is null ? "Выберите контекст вопроса." : GetContextStatus(value);
        }
    }

    public bool IsGlobalContext => SelectedContext?.IsGlobal == true;
    public string? SelectedMeetingId => SelectedContext?.MeetingId;
    public bool HasAnswer => !string.IsNullOrWhiteSpace(AnswerText);
    public bool HasEvidence => Evidence.Count > 0;
    public bool HasAnswerWithoutEvidence => HasAnswer && !HasEvidence;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;
        StatusText = string.Empty;
        Contexts.Clear();
        Evidence.Clear();
        AnswerText = string.Empty;
        VoiceAnswerText = string.Empty;

        try
        {
            if (!await _services.Backend.CheckReadyAsync(cancellationToken))
            {
                ErrorText = "API недоступен. Проверьте backend и подключение.";
                return;
            }

            if (!_services.Backend.HasSession)
            {
                ErrorText = "Войдите в API, чтобы использовать помощника.";
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
            if (IsGlobalAllowed) Contexts.Add(new AssistantContextOption("Вся история", null, null));

            var meetings = await LoadAllMeetingsAsync(cancellationToken);
            foreach (var meeting in meetings)
                Contexts.Add(new AssistantContextOption(meeting.Title, meeting.Id, meeting));

            var target = _services.Navigation.PendingMeetingTarget;
            var selected = target is not null
                ? Contexts.FirstOrDefault(item => item.MeetingId == target.MeetingId)
                : null;
            SelectedContext = selected ?? (IsGlobalAllowed ? Contexts.FirstOrDefault(item => item.IsGlobal) : Contexts.FirstOrDefault());
            StatusText = SelectedContext is null
                ? "Доступных встреч для вопроса пока нет."
                : GetContextStatus(SelectedContext);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ErrorText = SafeError(ex); }
        finally { IsLoading = false; }
    }

    public async Task AskAsync(CancellationToken pageToken)
    {
        var question = Question.Trim();
        if (question.Length == 0)
        {
            ErrorText = "Введите вопрос.";
            return;
        }

        if (SelectedContext is null)
        {
            ErrorText = "Выберите встречу или доступный глобальный контекст.";
            return;
        }

        if (SelectedContext.IsGlobal && !IsGlobalAllowed)
        {
            ErrorText = "Вопросы по всей истории доступны только Administrator и Operator. Выберите встречу.";
            return;
        }

        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = CancellationTokenSource.CreateLinkedTokenSource(pageToken);
        var cancellationToken = _queryCts.Token;
        IsAsking = true;
        ErrorText = string.Empty;
        AnswerText = string.Empty;
        VoiceAnswerText = string.Empty;
        Evidence.Clear();
        StatusText = "Запрос отправляется…";

        try
        {
            Guid? meetingId = null;
            if (!SelectedContext.IsGlobal)
            {
                if (!Guid.TryParse(SelectedContext.MeetingId, out var parsedMeetingId))
                {
                    ErrorText = "Идентификатор встречи имеет неверный формат.";
                    return;
                }
                meetingId = parsedMeetingId;
            }

            var created = await _services.Backend.CreateAssistantQueryAsync(question, meetingId, cancellationToken);
            if (created is null || !Guid.TryParse(created.Id, out var queryId))
            {
                ErrorText = "API не принял запрос помощника.";
                return;
            }

            ApplyQuery(created);
            for (var attempt = 0; attempt < 120 && !IsTerminal(created.Status); attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                created = await _services.Backend.GetAssistantQueryAsync(queryId, cancellationToken);
                if (created is null)
                {
                    ErrorText = "Результат запроса больше недоступен в API.";
                    return;
                }
                ApplyQuery(created);
            }

            if (!IsTerminal(created.Status))
            {
                ErrorText = "Помощник не ответил за четыре минуты. Повторите запрос позже.";
                StatusText = "Таймаут обработки";
            }
            else if (created.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                ErrorText = string.IsNullOrWhiteSpace(created.ErrorCode) ? "Помощник завершил запрос с ошибкой." : created.ErrorCode!;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorText = SafeError(ex); }
        finally { IsAsking = false; }
    }

    public void CancelPending()
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = null;
    }

    private void ApplyQuery(DesktopAssistantQuery query)
    {
        StatusText = DisplayStatus(query.Status);
        AnswerText = query.Answer ?? string.Empty;
        VoiceAnswerText = query.VoiceAnswer ?? string.Empty;
        Evidence.Clear();
        if (query.Evidence.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var item in query.Evidence.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("segmentId", out var segmentElement)) continue;
            var segmentId = segmentElement.GetString();
            if (string.IsNullOrWhiteSpace(segmentId)) continue;
            Evidence.Add(new AssistantEvidenceItem(
                GetString(item, "meetingId"),
                segmentId!,
                GetLong(item, "startMs"),
                GetLong(item, "endMs"),
                GetString(item, "timecode"),
                GetString(item, "speaker"),
                GetString(item, "text")));
        }
        OnPropertyChanged(nameof(HasEvidence));
        OnPropertyChanged(nameof(HasAnswerWithoutEvidence));
    }

    private async Task<IReadOnlyList<DesktopMeeting>> LoadAllMeetingsAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var meetings = new List<DesktopMeeting>();
        var offset = 0;
        while (true)
        {
            var page = await _services.Backend.GetMeetingsPageAsync(pageSize, offset, cancellationToken);
            if (page.Count == 0) break;
            meetings.AddRange(page);
            if (page.Count < pageSize) break;
            offset += page.Count;
        }
        return meetings.OrderByDescending(item => item.CreatedAt).ToList();
    }

    private static string? GetString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? GetLong(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) return null;
        return number;
    }

    private static bool IsTerminal(string status) => status.Equals("READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase)
        || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase);

    private static string DisplayStatus(string status) => status.ToUpperInvariant() switch
    {
        "QUEUED" => "Запрос в очереди",
        "RUNNING" => "Помощник обрабатывает вопрос",
        "READY" => "Ответ готов",
        "NEEDS_REVIEW" => "Ответ требует проверки источников",
        "FAILED" => "Помощник завершил запрос с ошибкой",
        _ => string.IsNullOrWhiteSpace(status) ? "Состояние неизвестно" : status,
    };

    private static string GetContextStatus(AssistantContextOption context) => context.IsGlobal
        ? "Контекст: вся история"
        : $"Контекст: {context.Label}";

    private static string SafeError(Exception ex) => UiErrorFormatter.Format(ex, "Помощник не выполнил запрос.");
}

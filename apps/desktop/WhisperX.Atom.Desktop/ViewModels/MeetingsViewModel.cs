using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class MeetingsViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private readonly List<DesktopMeeting> _allMeetings = [];
    private bool _isLoading;
    private string _searchText = string.Empty;
    private string _statusFilter = "Все статусы";
    private string _errorText = string.Empty;
    private string _statusText = "";
    private DesktopMeeting? _selectedMeeting;

    public MeetingsViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<DesktopMeeting> FilteredMeetings { get; } = [];
    public ObservableCollection<string> StatusFilters { get; } = ["Все статусы", "В обработке", "Готово", "Ошибка", "Отменено"];

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ApplyFilter();
        }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (!SetProperty(ref _statusFilter, value)) return;
            ApplyFilter();
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public DesktopMeeting? SelectedMeeting
    {
        get => _selectedMeeting;
        set => SetProperty(ref _selectedMeeting, value);
    }

    public bool HasMeetings => _allMeetings.Count > 0;
    public bool HasFilteredMeetings => FilteredMeetings.Count > 0;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;
        StatusText = string.Empty;
        SelectedMeeting = null;

        try
        {
            if (!await _services.Backend.CheckReadyAsync(cancellationToken))
            {
                _allMeetings.Clear();
                ApplyFilter();
                ErrorText = "API недоступен. Проверьте backend и адрес подключения.";
                return;
            }

            if (!_services.Backend.HasSession)
            {
                _allMeetings.Clear();
                ApplyFilter();
                StatusText = "Войдите в API, чтобы загрузить совещания.";
                return;
            }

            var meetings = await _services.Backend.GetMeetingsAsync(cancellationToken);
            _allMeetings.Clear();
            _allMeetings.AddRange(meetings.OrderByDescending(meeting => meeting.CreatedAt));
            ApplyFilter();
            StatusText = _allMeetings.Count == 0
                ? "Совещаний пока нет."
                : $"Загружено совещаний: {_allMeetings.Count}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _allMeetings.Clear();
            ApplyFilter();
            StatusText = string.Empty;
            ErrorText = SafeError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var meetings = string.IsNullOrWhiteSpace(query)
            ? _allMeetings
            : _allMeetings.Where(meeting =>
                meeting.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (meeting.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        meetings = StatusFilter switch
        {
            "В обработке" => meetings.Where(meeting => !IsTerminal(meeting.Status)).ToList(),
            "Готово" => meetings.Where(meeting => meeting.Status.Equals("READY", StringComparison.OrdinalIgnoreCase) || meeting.Status.Equals("PARTIAL_READY", StringComparison.OrdinalIgnoreCase)).ToList(),
            "Ошибка" => meetings.Where(meeting => meeting.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)).ToList(),
            "Отменено" => meetings.Where(meeting => meeting.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)).ToList(),
            _ => meetings
        };

        FilteredMeetings.Clear();
        foreach (var meeting in meetings) FilteredMeetings.Add(meeting);
        OnPropertyChanged(nameof(HasMeetings));
        OnPropertyChanged(nameof(HasFilteredMeetings));
    }

    private static bool IsTerminal(string status) => status.Equals("READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("PARTIAL_READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);

    private static string SafeError(Exception ex) => ex is HttpRequestException
        ? "Не удалось подключиться к API. Проверьте backend и вход в API."
        : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}

public sealed class MeetingWorkspaceViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private bool _isLoading;
    private bool _isRefreshingSummary;
    private string _errorText = string.Empty;
    private string _durationText = "—";
    private string _pipelineText = "Данные ещё не загружены";
    private string _summaryText = "Саммари пока не готово.";
    private string _summaryMetaText = string.Empty;
    private string? _previewPath;
    private DesktopMeeting? _meeting;
    private DesktopSummary? _summary;
    private DesktopJob? _latestJob;

    public MeetingWorkspaceViewModel(FrontendServices services) => _services = services;

    public DesktopMeeting? Meeting
    {
        get => _meeting;
        private set
        {
            if (!SetProperty(ref _meeting, value)) return;
            OnPropertyChanged(nameof(HasMeeting));
            OnPropertyChanged(nameof(MeetingDateText));
        }
    }

    public ObservableCollection<DesktopJob> Jobs { get; } = [];
    public ObservableCollection<DesktopTranscriptSegment> TranscriptSegments { get; } = [];
    public ObservableCollection<DesktopSpeaker> Speakers { get; } = [];
    public ObservableCollection<DesktopMedia> Media { get; } = [];
    public ObservableCollection<DesktopDecision> Decisions { get; } = [];
    public ObservableCollection<DesktopTask> Tasks { get; } = [];

    public DesktopSummary? Summary
    {
        get => _summary;
        private set
        {
            if (!SetProperty(ref _summary, value)) return;
            OnPropertyChanged(nameof(HasSummary));
        }
    }

    public DesktopJob? LatestJob
    {
        get => _latestJob;
        private set
        {
            if (!SetProperty(ref _latestJob, value)) return;
            OnPropertyChanged(nameof(CanRetryLatestJob));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsRefreshingSummary
    {
        get => _isRefreshingSummary;
        private set => SetProperty(ref _isRefreshingSummary, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string PipelineText
    {
        get => _pipelineText;
        private set => SetProperty(ref _pipelineText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string SummaryMetaText
    {
        get => _summaryMetaText;
        private set => SetProperty(ref _summaryMetaText, value);
    }

    public string? PreviewPath
    {
        get => _previewPath;
        private set
        {
            if (!SetProperty(ref _previewPath, value)) return;
            OnPropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasMeeting => Meeting is not null;
    public bool HasSummary => Summary is not null;
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewPath) && File.Exists(PreviewPath);
    public bool CanRetryLatestJob => LatestJob is not null && IsRetryable(LatestJob.Status);
    public string MeetingDateText => Meeting is null ? string.Empty : Meeting.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public void ClearSelection()
    {
        Meeting = null;
        ErrorText = string.Empty;
        PreviewPath = null;
        ClearCollections();
    }

    public async Task LoadAsync(DesktopMeeting meeting, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(meeting.Id, out var meetingId))
        {
            Meeting = meeting;
            ErrorText = "Идентификатор совещания имеет неверный формат.";
            return;
        }

        Meeting = meeting;
        IsLoading = true;
        ErrorText = string.Empty;
        PreviewPath = null;
        ClearCollections();

        var errors = new List<string>();
        try
        {
            if (!_services.Backend.HasSession)
            {
                ErrorText = "Войдите в API, чтобы открыть данные совещания.";
                return;
            }

            var jobsTask = LoadPartAsync(() => _services.Backend.GetJobsAsync(meetingId, cancellationToken), "pipeline", errors);
            var transcriptTask = LoadPartAsync(() => _services.Backend.GetTranscriptAsync(meetingId, cancellationToken), "стенограмму", errors);
            var speakersTask = LoadPartAsync(() => _services.Backend.GetSpeakersAsync(meetingId, cancellationToken), "спикеров", errors);
            var mediaTask = LoadPartAsync(() => _services.Backend.GetMediaAsync(meetingId, cancellationToken), "файлы", errors);
            var summaryTask = LoadPartAsync(() => _services.Backend.GetSummaryAsync(meetingId, cancellationToken), "саммари", errors);
            var decisionsTask = LoadPartAsync(() => _services.Backend.GetDecisionsAsync(meetingId, cancellationToken), "решения", errors);
            var tasksTask = LoadPartAsync(() => _services.Backend.GetTasksAsync(meetingId, cancellationToken), "поручения", errors);

            await Task.WhenAll(jobsTask, transcriptTask, speakersTask, mediaTask, summaryTask, decisionsTask, tasksTask);

            var jobs = await jobsTask;
            foreach (var job in jobs ?? []) Jobs.Add(job);
            LatestJob = Jobs.OrderByDescending(job => job.Attempt).FirstOrDefault() ?? Jobs.LastOrDefault();
            PipelineText = LatestJob is null
                ? "Обработка ещё не запущена"
                : $"{DisplayStatus(LatestJob.Status)} · {DisplayStage(LatestJob.Stage)} · {LatestJob.Progress}%";

            var transcript = await transcriptTask;
            if (transcript is not null)
                foreach (var segment in transcript.Segments.OrderBy(segment => segment.Ordinal)) TranscriptSegments.Add(segment);

            foreach (var speaker in await speakersTask ?? []) Speakers.Add(speaker);
            foreach (var media in await mediaTask ?? []) Media.Add(media);
            foreach (var decision in await decisionsTask ?? []) Decisions.Add(decision);
            foreach (var item in await tasksTask ?? []) Tasks.Add(item);
            Summary = await summaryTask;
            SummaryText = FormatSummary(Summary);
            SummaryMetaText = Summary is null ? string.Empty : $"Версия {Summary.Version} · {Summary.ModelName}";
            DurationText = FormatDuration(Media.Select(item => item.DurationMs).Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            IsLoading = false;
            if (errors.Count > 0) ErrorText = string.Join(Environment.NewLine, errors);
        }
    }

    public async Task<DesktopJob?> RetryLatestJobAsync(CancellationToken cancellationToken = default)
    {
        if (LatestJob is null || !Guid.TryParse(LatestJob.Id, out var jobId)) return null;
        var retried = await _services.Backend.RetryJobAsync(jobId, cancellationToken);
        if (retried is not null)
        {
            var index = Jobs.IndexOf(LatestJob);
            if (index >= 0) Jobs[index] = retried;
            LatestJob = retried;
            PipelineText = $"{DisplayStatus(retried.Status)} · {DisplayStage(retried.Stage)} · {retried.Progress}%";
        }
        return retried;
    }

    public async Task<bool> RebuildSummaryAsync(CancellationToken cancellationToken = default)
    {
        if (Meeting is null || !Guid.TryParse(Meeting.Id, out var meetingId)) return false;
        IsRefreshingSummary = true;
        try
        {
            var accepted = await _services.Backend.RebuildSummaryAsync(meetingId, cancellationToken);
            if (!accepted) return false;
            Summary = await _services.Backend.GetSummaryAsync(meetingId, cancellationToken);
            SummaryText = FormatSummary(Summary);
            SummaryMetaText = Summary is null ? string.Empty : $"Версия {Summary.Version} · {Summary.ModelName}";
            return true;
        }
        finally
        {
            IsRefreshingSummary = false;
        }
    }

    public async Task<bool> UpdateTaskAsync(DesktopTask item, CancellationToken cancellationToken = default) =>
        await _services.Backend.UpdateTaskAsync(item, cancellationToken);

    public async Task<string?> LoadPreviewAsync(DesktopMedia media, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(media.Id, out var mediaId)) return null;
        PreviewPath = await _services.Backend.DownloadPreviewAsync(mediaId, cancellationToken);
        return PreviewPath;
    }

    private void ClearCollections()
    {
        Jobs.Clear();
        TranscriptSegments.Clear();
        Speakers.Clear();
        Media.Clear();
        Decisions.Clear();
        Tasks.Clear();
        Summary = null;
        LatestJob = null;
        DurationText = "—";
        PipelineText = "Загрузка состояния обработки…";
        SummaryText = "Саммари пока не готово.";
        SummaryMetaText = string.Empty;
    }

    private static async Task<T?> LoadPartAsync<T>(Func<Task<T>> factory, string label, ICollection<string> errors)
    {
        try { return await factory(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"Не удалось загрузить {label}: {SafeError(ex)}");
            return default;
        }
    }

    private static string FormatSummary(DesktopSummary? summary)
    {
        if (summary is null) return "Саммари пока не готово.";
        var root = summary.Content.RootElement;
        if (root.TryGetProperty("summary", out var summaryText) && summaryText.ValueKind == System.Text.Json.JsonValueKind.String)
            return summaryText.GetString() ?? "Саммари готово, но текст отсутствует.";
        return root.ToString();
    }

    private static string FormatDuration(long durationMs) => durationMs <= 0
        ? "—"
        : TimeSpan.FromMilliseconds(durationMs).ToString(durationMs >= 3_600_000 ? @"hh\:mm\:ss" : @"mm\:ss");

    private static string DisplayStatus(string status) => status switch
    {
        "READY" => "Готово",
        "RUNNING" => "В обработке",
        "FAILED" => "Ошибка",
        "CANCELLED" => "Отменено",
        _ => string.IsNullOrWhiteSpace(status) ? "—" : status
    };

    private static string DisplayStage(string stage) => string.IsNullOrWhiteSpace(stage) ? "Этап не указан" : stage;
    private static bool IsRetryable(string status) => status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
    private static string SafeError(Exception ex) => ex is HttpRequestException
        ? "API недоступен"
        : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}

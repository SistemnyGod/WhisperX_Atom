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
    private CancellationTokenSource? _filterDebounce;

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
            ScheduleFilter();
        }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (!SetProperty(ref _statusFilter, value)) return;
            ScheduleFilter();
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
                ApplyFilterNow();
                ErrorText = "API недоступен. Проверьте backend и адрес подключения.";
                return;
            }

            if (!_services.Backend.HasSession)
            {
                _allMeetings.Clear();
                ApplyFilterNow();
                StatusText = "Войдите в API, чтобы загрузить совещания.";
                return;
            }

            var meetings = await _services.Backend.GetMeetingsAsync(cancellationToken);
            _allMeetings.Clear();
            _allMeetings.AddRange(meetings.OrderByDescending(meeting => meeting.CreatedAt));
            ApplyFilterNow();
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
            ApplyFilterNow();
            StatusText = string.Empty;
            ErrorText = SafeError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ScheduleFilter()
    {
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        var cancellation = new CancellationTokenSource();
        _filterDebounce = cancellation;
        _ = ApplyFilterDebouncedAsync(cancellation);
    }

    private async Task ApplyFilterDebouncedAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(250, cancellation.Token);
            ApplyFilterNow();
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this filter operation.
        }
        finally
        {
            if (ReferenceEquals(_filterDebounce, cancellation))
            {
                _filterDebounce = null;
                cancellation.Dispose();
            }
        }
    }

    private void ApplyFilterNow()
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

        if (!FilteredMeetings.SequenceEqual(meetings))
        {
            FilteredMeetings.Clear();
            foreach (var meeting in meetings) FilteredMeetings.Add(meeting);
        }
        OnPropertyChanged(nameof(HasMeetings));
        OnPropertyChanged(nameof(HasFilteredMeetings));
    }

    private static bool IsTerminal(string status) => status.Equals("READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("PARTIAL_READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);

    private static string SafeError(Exception ex) => UiErrorFormatter.Format(ex, "Не удалось загрузить совещания.");
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
    private DesktopTranscript? _transcript;
    private DesktopSummary? _summary;
    private DesktopJob? _latestJob;
    private DesktopPipelineSnapshot? _pipelineSnapshot;

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
    public ObservableCollection<DesktopTranscriptVersion> TranscriptVersions { get; } = [];

    public DesktopTranscript? Transcript
    {
        get => _transcript;
        private set
        {
            if (!SetProperty(ref _transcript, value)) return;
            OnPropertyChanged(nameof(HasTranscript));
            OnPropertyChanged(nameof(TranscriptMetaText));
            OnPropertyChanged(nameof(TranscriptQualityText));
            OnPropertyChanged(nameof(TranscriptWarningText));
        }
    }

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

    /// <summary>
    /// Canonical server-owned pipeline state.  Jobs remain available for
    /// detailed diagnostics and rolling compatibility, but the user-facing
    /// status is derived from this snapshot whenever the API provides it.
    /// </summary>
    public DesktopPipelineSnapshot? PipelineSnapshot
    {
        get => _pipelineSnapshot;
        private set => SetProperty(ref _pipelineSnapshot, value);
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
    public bool HasTranscript => Transcript is not null && Transcript.Segments.Count > 0;
    public bool HasSummary => Summary is not null;
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewPath) && File.Exists(PreviewPath);
    public bool CanRetryLatestJob => LatestJob is not null && IsRetryable(LatestJob.Status);
    public string MeetingDateText => Meeting is null ? string.Empty : Meeting.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string TranscriptMetaText => Transcript is null
        ? "Стенограмма ещё не готова"
        : $"{Transcript.Segments.Count} сегментов · {FormatDuration(Transcript.Segments.Count == 0 ? 0 : Transcript.Segments.Max(item => item.EndMs))}";
    public string TranscriptQualityText => Transcript?.QualityScore is double score
        ? $"Качество: {QualityCategory(score)} ({score:0.#})"
        : "Качество не рассчитано";
    public string TranscriptWarningText => Transcript?.IsPartial == true
        ? "Стенограмма получена с предупреждениями. Текст доступен для чтения и экспорта."
        : string.Empty;

    public void ClearSelection()
    {
        _services.ActiveMeeting.Clear();
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
        _services.ActiveMeeting.Set(meetingId, meeting.Title);
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
            var pipelineTask = LoadPartAsync(() => _services.Backend.GetMeetingPipelineAsync(meetingId, cancellationToken), "состояние конвейера", errors);
            var transcriptTask = LoadPartAsync(() => _services.Backend.GetTranscriptAsync(meetingId, cancellationToken), "стенограмму", errors);
            var speakersTask = LoadPartAsync(() => _services.Backend.GetSpeakersAsync(meetingId, cancellationToken), "спикеров", errors);
            var mediaTask = LoadPartAsync(() => _services.Backend.GetMediaAsync(meetingId, cancellationToken), "файлы", errors);
            var summaryTask = LoadPartAsync(() => _services.Backend.GetSummaryAsync(meetingId, cancellationToken), "саммари", errors);
            var decisionsTask = LoadPartAsync(() => _services.Backend.GetDecisionsAsync(meetingId, cancellationToken), "решения", errors);
            var tasksTask = LoadPartAsync(() => _services.Backend.GetTasksAsync(meetingId, cancellationToken), "поручения", errors);
            var versionsTask = LoadPartAsync(() => _services.Backend.GetTranscriptVersionsAsync(meetingId, cancellationToken), "версии стенограммы", errors);

            await Task.WhenAll(jobsTask, pipelineTask, transcriptTask, speakersTask, mediaTask, summaryTask, decisionsTask, tasksTask, versionsTask);

            var jobs = await jobsTask;
            foreach (var job in jobs ?? []) Jobs.Add(job);
            LatestJob = Jobs.OrderByDescending(job => job.Attempt).FirstOrDefault() ?? Jobs.LastOrDefault();
            PipelineSnapshot = (await pipelineTask)?.LastOrDefault()?.Snapshot;
            PipelineText = PipelineSnapshot is not null
                ? FormatPipelineSnapshot(PipelineSnapshot)
                : LatestJob is null
                    ? "Обработка ещё не запущена"
                    : $"{DisplayStatus(LatestJob.Status)} · {DisplayStage(LatestJob.Stage)} · {LatestJob.Progress}%";

            var transcript = await transcriptTask;
            Transcript = transcript;
            if (transcript is not null)
                foreach (var segment in transcript.Segments.OrderBy(segment => segment.Ordinal)) TranscriptSegments.Add(segment);

            foreach (var speaker in await speakersTask ?? []) Speakers.Add(speaker);
            foreach (var media in await mediaTask ?? []) Media.Add(media);
            foreach (var decision in await decisionsTask ?? []) Decisions.Add(decision);
            foreach (var item in await tasksTask ?? []) Tasks.Add(item);
            foreach (var version in await versionsTask ?? []) TranscriptVersions.Add(version);
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
            var readiness = await _services.Backend.GetProcessingReadinessAsync(cancellationToken);
            if (UiStatusMapper.IsQwenDisabled(readiness))
            {
                ErrorText = UiStatusMapper.SummaryDisabledMessage;
                PipelineText = "Саммари отключено настройками сервера";
                return false;
            }
            var job = await _services.Backend.QueueSummaryRebuildAsync(meetingId, cancellationToken);
            if (job is null)
            {
                ErrorText = "Саммари не поставлено в очередь. Проверьте, что Qwen включена и стенограмма готова.";
                return false;
            }
            LatestJob = job;
            PipelineText = $"{DisplayStatus(job.Status)} · {DisplayStage(job.Stage)} · {job.Progress}%";
            var completed = await _services.JobTracker.WaitForTerminalAsync(job, current =>
            {
                LatestJob = current;
                PipelineText = $"{DisplayStatus(current.Status)} · {DisplayStage(current.Stage)} · {current.Progress}%";
            }, cancellationToken);
            if (completed is null || !string.Equals(completed.Status, "READY", StringComparison.OrdinalIgnoreCase))
            {
                ErrorText = completed?.Error ?? "Пересборка саммари завершилась ошибкой.";
                return false;
            }
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

    public async Task<bool> EditTranscriptSegmentAsync(Guid segmentId, string text, CancellationToken cancellationToken = default)
    {
        if (Meeting is null || string.IsNullOrWhiteSpace(text) || !Guid.TryParse(Meeting.Id, out var meetingId)) return false;
        var version = await _services.Backend.EditTranscriptSegmentAsync(meetingId, segmentId, text.Trim(), cancellationToken);
        if (version is null) return false;
        var transcript = await _services.Backend.GetTranscriptAsync(meetingId, cancellationToken);
        if (transcript is not null)
        {
            Transcript = transcript;
            TranscriptSegments.Clear();
            foreach (var segment in transcript.Segments.OrderBy(item => item.Ordinal)) TranscriptSegments.Add(segment);
        }
        TranscriptVersions.Clear();
        foreach (var item in await _services.Backend.GetTranscriptVersionsAsync(meetingId, cancellationToken)) TranscriptVersions.Add(item);
        return true;
    }

    public async Task<bool> ReprocessTranscriptAsync(CancellationToken cancellationToken = default)
    {
        if (Meeting is null || !Guid.TryParse(Meeting.Id, out var meetingId)) return false;
        var job = await _services.Backend.ReprocessTranscriptAsync(meetingId, cancellationToken);
        if (job is null) return false;
        LatestJob = job;
        PipelineText = $"{DisplayStatus(job.Status)} · {DisplayStage(job.Stage)} · {job.Progress}%";
        var completed = await _services.JobTracker.WaitForTerminalAsync(job, current =>
        {
            LatestJob = current;
            PipelineText = $"{DisplayStatus(current.Status)} · {DisplayStage(current.Stage)} · {current.Progress}%";
        }, cancellationToken);
        if (completed is null || !string.Equals(completed.Status, "READY", StringComparison.OrdinalIgnoreCase))
        {
            ErrorText = completed?.Error ?? "Повторная транскрибация завершилась ошибкой.";
            return false;
        }
        Transcript = await _services.Backend.GetTranscriptAsync(meetingId, cancellationToken);
        TranscriptSegments.Clear();
        if (Transcript is not null)
            foreach (var segment in Transcript.Segments.OrderBy(item => item.Ordinal)) TranscriptSegments.Add(segment);
        TranscriptVersions.Clear();
        foreach (var version in await _services.Backend.GetTranscriptVersionsAsync(meetingId, cancellationToken)) TranscriptVersions.Add(version);
        OnPropertyChanged(nameof(HasTranscript));
        return true;
    }

    public async Task<bool> RenameSpeakerAsync(Guid speakerId, string displayName, CancellationToken cancellationToken = default)
    {
        if (Meeting is null || !Guid.TryParse(Meeting.Id, out var meetingId)) return false;
        if (!await _services.Backend.RenameSpeakerAsync(meetingId, speakerId, displayName.Trim(), cancellationToken)) return false;
        await ReloadTranscriptAndSpeakersAsync(meetingId, cancellationToken);
        return true;
    }

    public async Task<bool> MergeSpeakersAsync(Guid sourceSpeakerId, Guid targetSpeakerId, CancellationToken cancellationToken = default)
    {
        if (Meeting is null || !Guid.TryParse(Meeting.Id, out var meetingId)) return false;
        if (!await _services.Backend.MergeSpeakersAsync(meetingId, sourceSpeakerId, targetSpeakerId, cancellationToken)) return false;
        await ReloadTranscriptAndSpeakersAsync(meetingId, cancellationToken);
        return true;
    }

    private async Task ReloadTranscriptAndSpeakersAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        var transcript = await _services.Backend.GetTranscriptAsync(meetingId, cancellationToken);
        Transcript = transcript;
        TranscriptSegments.Clear();
        if (transcript is not null)
            foreach (var segment in transcript.Segments.OrderBy(item => item.Ordinal)) TranscriptSegments.Add(segment);
        Speakers.Clear();
        foreach (var speaker in await _services.Backend.GetSpeakersAsync(meetingId, cancellationToken)) Speakers.Add(speaker);
    }

    public async Task<string?> LoadPreviewAsync(DesktopMedia media, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(media.Id, out var mediaId)) return null;
        PreviewPath = await _services.Backend.DownloadPreviewAsync(mediaId, cancellationToken);
        return PreviewPath;
    }

    public async Task<bool> DownloadMediaAsync(DesktopMedia media, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(media.Id, out var mediaId)) return false;
        return await _services.Backend.DownloadMediaAsync(mediaId, destinationPath, cancellationToken);
    }

    public async Task<bool> DownloadOriginalMediaAsync(DesktopMedia media, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(media.Id, out var mediaId)) return false;
        return await _services.Backend.DownloadOriginalMediaAsync(mediaId, destinationPath, cancellationToken);
    }

    private void ClearCollections()
    {
        Jobs.Clear();
        TranscriptSegments.Clear();
        Speakers.Clear();
        Media.Clear();
        Decisions.Clear();
        Tasks.Clear();
        TranscriptVersions.Clear();
        Transcript = null;
        Summary = null;
        LatestJob = null;
        PipelineSnapshot = null;
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
        return MeetingProtocolParser.Parse(summary?.Content).DisplayText;
    }

    private static string FormatDuration(long durationMs) => durationMs <= 0
        ? "—"
        : TimeSpan.FromMilliseconds(durationMs).ToString(durationMs >= 3_600_000 ? @"hh\:mm\:ss" : @"mm\:ss");

    private static string QualityCategory(double score) => score switch
    {
        >= 85 => "Высокое",
        >= 65 => "Среднее",
        _ => "Требует проверки"
    };

    private static string DisplayStatus(string status) => string.IsNullOrWhiteSpace(status) ? "—" : UiStatusMapper.Text(status);

    private static string FormatPipelineSnapshot(DesktopPipelineSnapshot snapshot)
    {
        var text = $"{DisplayPipelineStatus(snapshot.OverallStatus)} · {DisplayPipelineStage(snapshot.CurrentStage)}";
        if (!string.IsNullOrWhiteSpace(snapshot.BlockedBy))
            text += $" · ожидание: {DisplayBlockedBy(snapshot.BlockedBy)}";
        if (snapshot.Retryable && !string.IsNullOrWhiteSpace(snapshot.ErrorCode))
            text += $" · {snapshot.ErrorCode}";
        return text;
    }

    private static string DisplayPipelineStatus(string status) => status.ToUpperInvariant() switch
    {
        "READY" => "Готово",
        "PARTIAL_READY" => "Частично готово",
        "WAITING" => "Ожидает обработки",
        "PROCESSING" => "Обрабатывается",
        "DEGRADED" => "С предупреждением",
        "FAILED" => "Ошибка обработки",
        _ => DisplayStatus(status)
    };

    private static string DisplayPipelineStage(string stage) => stage.ToUpperInvariant() switch
    {
        "MEDIA" => "Подготовка медиа",
        "ASR" => "Распознавание речи",
        "TRANSCRIPT_V1" => "Стенограмма V1",
        "ENRICHMENT" => "Выравнивание и диаризация",
        "TRANSCRIPT_V2" => "Стенограмма V2",
        "SUMMARY" => "Саммари",
        "COMPLETE" => "Все этапы завершены",
        _ => DisplayStage(stage)
    };

    private static string DisplayBlockedBy(string blockedBy) => blockedBy.ToUpperInvariant() switch
    {
        "MEDIA_WORKER" => "Media Worker",
        "GPU_WORKER" => "GPU Worker",
        "ENRICHMENT_WORKER" => "Enrichment Worker",
        "SUMMARY_WORKER" => "Summary Worker",
        "ASR" => "ASR",
        "ENRICHMENT" => "обогащение стенограммы",
        _ => blockedBy
    };

    private static string DisplayStage(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return "Этап не указан";
        var mapped = UiStatusMapper.Text(stage);
        return string.Equals(mapped, "Состояние не определено", StringComparison.Ordinal)
            ? stage
            : mapped;
    }
    private static bool IsRetryable(string status) => status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
    private static string SafeError(Exception ex) => UiErrorFormatter.Format(ex, "Не удалось загрузить данные совещания.");
}

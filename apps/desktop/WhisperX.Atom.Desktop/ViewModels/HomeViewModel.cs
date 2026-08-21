using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _isLoading;
    private bool _apiAvailable;
    private bool _agentAvailable;
    private string _apiStatus = "Проверка API…";
    private string _agentStatus = "Проверка Recorder Agent…";
    private string _recorderSummary = "Проверка Recorder…";
    private string _serverSummary = "Проверка сервера…";
    private string _whisperXSummary = "Проверка WhisperX…";
    private string _voiceStatus = "Мифодий: проверка состояния";
    private string _recordingStatus = "Проверяется состояние записи…";
    private string _effectiveMicrophoneText = "Микрофон ещё не подтверждён";
    private string _microphoneSignalText = "Сигнал проверяется перед стартом записи";
    private string _microphoneSignalState = "UNKNOWN";
    private double _microphoneLevel;
    private string _microphoneDbLabel = "Нет измерения";
    private IReadOnlyList<double> _microphoneWaveform = Array.Empty<double>();
    private bool _microphoneTelemetryStale = true;
    private string _storageText = "Ожидание проверки";
    private string _pendingUploadsText = "—";
    private string _archiveText = "Путь архива будет показан после проверки Agent";
    private string _recordingState = "Idle";
    private long? _mediaTimeMs;
    private string _errorText = string.Empty;
    private string _meetingsMessage = "Войдите в API, чтобы загрузить совещания";
    private string _activeRecordingsText = "—";
    private string _processingText = "—";
    private string _readySummariesText = "—";
    private string _openTasksText = "—";
    private string _activeProcessingTitle = "Нет активной обработки";
    private string _activeProcessingStage = "Очередь обработки пуста";
    private int _activeProcessingProgress;

    public HomeViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<DesktopMeeting> RecentMeetings { get; } = [];
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool ApiAvailable { get => _apiAvailable; private set => SetProperty(ref _apiAvailable, value); }
    public bool AgentAvailable { get => _agentAvailable; private set => SetProperty(ref _agentAvailable, value); }
    public string ApiStatus { get => _apiStatus; private set => SetProperty(ref _apiStatus, value); }
    public string AgentStatus { get => _agentStatus; private set => SetProperty(ref _agentStatus, value); }
    public string RecorderSummary { get => _recorderSummary; private set => SetProperty(ref _recorderSummary, value); }
    public string ServerSummary { get => _serverSummary; private set => SetProperty(ref _serverSummary, value); }
    public string WhisperXSummary { get => _whisperXSummary; private set => SetProperty(ref _whisperXSummary, value); }
    public string VoiceStatus { get => _voiceStatus; private set => SetProperty(ref _voiceStatus, value); }
    public string RecordingStatus { get => _recordingStatus; private set => SetProperty(ref _recordingStatus, value); }
    public string EffectiveMicrophoneText { get => _effectiveMicrophoneText; private set => SetProperty(ref _effectiveMicrophoneText, value); }
    public string MicrophoneSignalText { get => _microphoneSignalText; private set => SetProperty(ref _microphoneSignalText, value); }
    public string MicrophoneSignalState { get => _microphoneSignalState; private set => SetProperty(ref _microphoneSignalState, value); }
    public double MicrophoneLevel { get => _microphoneLevel; private set => SetProperty(ref _microphoneLevel, value); }
    public string MicrophoneDbLabel { get => _microphoneDbLabel; private set => SetProperty(ref _microphoneDbLabel, value); }
    public IReadOnlyList<double> MicrophoneWaveform { get => _microphoneWaveform; private set => SetProperty(ref _microphoneWaveform, value); }
    public bool MicrophoneTelemetryStale { get => _microphoneTelemetryStale; private set => SetProperty(ref _microphoneTelemetryStale, value); }
    public string StorageText { get => _storageText; private set => SetProperty(ref _storageText, value); }
    public string PendingUploadsText { get => _pendingUploadsText; private set => SetProperty(ref _pendingUploadsText, value); }
    public string ArchiveText { get => _archiveText; private set => SetProperty(ref _archiveText, value); }
    public string RecordingBadgeText => _recordingState.ToUpperInvariant() switch
    {
        "RECORDING" => "LIVE",
        "PAUSED" => "ПАУЗА",
        "FINALIZING" => "СОХРАНЕНИЕ",
        "ERROR" => "ОШИБКА",
        "UNAVAILABLE" => "НЕДОСТУПЕН",
        _ => "ГОТОВО"
    };
    public string MediaTimeText => _mediaTimeMs is long value
        ? TimeSpan.FromMilliseconds(Math.Max(0, value)).ToString(@"hh\:mm\:ss")
        : "—";
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string MeetingsMessage { get => _meetingsMessage; private set => SetProperty(ref _meetingsMessage, value); }
    public string ActiveRecordingsText { get => _activeRecordingsText; private set => SetProperty(ref _activeRecordingsText, value); }
    public string ProcessingText { get => _processingText; private set => SetProperty(ref _processingText, value); }
    public string ReadySummariesText { get => _readySummariesText; private set => SetProperty(ref _readySummariesText, value); }
    public string OpenTasksText { get => _openTasksText; private set => SetProperty(ref _openTasksText, value); }
    public string ActiveProcessingTitle { get => _activeProcessingTitle; private set => SetProperty(ref _activeProcessingTitle, value); }
    public string ActiveProcessingStage { get => _activeProcessingStage; private set => SetProperty(ref _activeProcessingStage, value); }
    public int ActiveProcessingProgress
    {
        get => _activeProcessingProgress;
        private set
        {
            if (!SetProperty(ref _activeProcessingProgress, value)) return;
            OnPropertyChanged(nameof(ActiveProcessingProgressText));
        }
    }
    public string ActiveProcessingProgressText => $"{ActiveProcessingProgress}%";
    public bool HasMeetings => RecentMeetings.Count > 0;

    public async Task StartPollingAsync(CancellationToken cancellationToken = default)
    {
        if (_pollCts is not null) return;
        var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollCts = pollCts;
        await RefreshAsync(pollCts.Token);
        if (_pollCts is null || pollCts.IsCancellationRequested) return;
        _pollTask = PollLoopAsync(pollCts.Token);
    }

    public async Task StopPollingAsync()
    {
        if (_pollCts is null) return;
        _pollCts.Cancel();
        try { if (_pollTask is not null) await _pollTask; } catch (OperationCanceledException) { }
        _pollTask = null;
        _pollCts.Dispose();
        _pollCts = null;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken)) await RefreshRecorderAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;

        await RefreshRecorderAsync(cancellationToken);

        try
        {
            await RefreshBackendAsync(cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshRecorderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _services.Recorder.GetHealthAsync(cancellationToken);
            _recordingState = response.State;
            _mediaTimeMs = response.MediaTimeMs;
            ActiveRecordingsText = response.State is "Recording" or "Paused" ? "1" : "0";
            OnPropertyChanged(nameof(RecordingBadgeText));
            OnPropertyChanged(nameof(MediaTimeText));
            AgentAvailable = response.IsReachable;
            AgentStatus = AgentStatusFormatter.Format(response);
            RecorderSummary = response.IsReachable
                ? response.State switch
                {
                    "Recording" => "Идёт запись",
                    "Paused" => "Пауза записи",
                    "Finalizing" => "Сохранение записи",
                    _ => "Готов к записи"
                }
                : "Recorder недоступен";
            RecordingStatus = response.State switch
            {
                "Recording" => "Идёт запись",
                "Paused" => "Запись приостановлена",
                "Finalizing" => "Сохранение и отправка записи",
                "Unavailable" => "Запись недоступна",
                "Error" => "Ошибка записи",
                "Checking" => "Проверка Recorder Agent",
                _ => "Ожидание записи"
            };
            if (response.Health is { } health)
            {
                StorageText = FormatStorage(health.FreeBytes, health.TotalBytes);
                PendingUploadsText = health.PendingUploadSessions.ToString(CultureInfo.InvariantCulture);
                ArchiveText = string.IsNullOrWhiteSpace(health.ArchiveRoot) ? "Путь архива не передан Agent" : health.ArchiveRoot;
                EffectiveMicrophoneText = string.IsNullOrWhiteSpace(health.EffectiveMicrophoneDeviceName)
                    ? string.IsNullOrWhiteSpace(health.SelectedMicrophoneDeviceId) ? "Windows по умолчанию" : "Сохранённое устройство"
                    : $"{health.EffectiveMicrophoneDeviceName}";
                MicrophoneSignalState = health.MicrophoneSignalState ?? "UNKNOWN";
                MicrophoneSignalText = FormatMicrophoneSignal(health.MicrophoneSignalState, health.MicrophoneRmsDb);
                MicrophoneTelemetryStale = health.MicrophoneTelemetryStale;
                MicrophoneLevel = Math.Clamp((health.MicrophonePeak ?? 0d) * 100d, 0d, 100d);
                MicrophoneDbLabel = health.MicrophoneRmsDb is double rms ? $"{rms:0.0} dBFS" : "Нет измерения";
                MicrophoneWaveform = AppendWaveformSample(MicrophoneWaveform, health.MicrophonePeak);
            }
            else
            {
                StorageText = "Нет данных";
                PendingUploadsText = "—";
                EffectiveMicrophoneText = "Нет данных от Recorder Agent";
                MicrophoneSignalState = "UNKNOWN";
                MicrophoneSignalText = "Сигнал недоступен";
                MicrophoneTelemetryStale = true;
                MicrophoneLevel = 0;
                MicrophoneDbLabel = "Нет измерения";
                MicrophoneWaveform = Array.Empty<double>();
            }

            try
            {
                var voice = await new WhisperX.Atom.Desktop.VoiceHostClient().GetStatusAsync(cancellationToken);
                VoiceStatus = FormatVoiceStatus(voice, MicrophoneSignalState);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { VoiceStatus = "Мифодий недоступен"; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AgentAvailable = false;
            _recordingState = "Unavailable";
            _mediaTimeMs = null;
            OnPropertyChanged(nameof(RecordingBadgeText));
            OnPropertyChanged(nameof(MediaTimeText));
            AgentStatus = "Recorder Agent недоступен";
            RecorderSummary = "Recorder недоступен";
            VoiceStatus = "Мифодий недоступен";
            RecordingStatus = "Запись недоступна";
            StorageText = "Ожидание проверки";
            PendingUploadsText = "—";
            EffectiveMicrophoneText = "Микрофон недоступен";
            MicrophoneSignalState = "UNAVAILABLE";
            MicrophoneSignalText = "Сигнал недоступен — проверьте Recorder Agent";
            MicrophoneTelemetryStale = true;
            MicrophoneLevel = 0;
            MicrophoneDbLabel = "Нет измерения";
            MicrophoneWaveform = Array.Empty<double>();
            ErrorText = SafeError(ex);
        }
    }

    private async Task RefreshBackendAsync(CancellationToken cancellationToken)
    {
        try
        {
            ApiAvailable = await _services.Backend.CheckReadyAsync(cancellationToken);
            ApiStatus = ApiAvailable ? "API доступен" : "API недоступен";
            ServerSummary = ApiAvailable
                ? (_services.Backend.HasSession ? "Подключён" : "Требуется вход")
                : "Сервер недоступен";
            var processingReadiness = ApiAvailable && _services.Backend.HasSession
                ? await _services.Backend.GetProcessingReadinessAsync(cancellationToken)
                : null;
            var whisperStatus = UiStatusMapper.ComponentStatus(processingReadiness, "whisperx");
            WhisperXSummary = !ApiAvailable
                ? "Сервер недоступен"
                : whisperStatus switch
                {
                    "READY" => "Готов к обработке",
                    "BUSY" => "Занят обработкой",
                    "DEGRADED" => "Требует восстановления",
                    "UNAVAILABLE" => "Недоступен",
                    _ when processingReadiness is null => "Статус не подтверждён",
                    _ => "Ожидает записи"
                };
            if (!ApiAvailable)
            {
                MeetingsMessage = "API недоступен. Проверьте подключение в Настройках.";
                RecentMeetings.Clear();
                ResetMeetingMetrics("История недоступна", "Подключите API, чтобы увидеть конвейер");
            }
            else if (!_services.Backend.HasSession)
            {
                MeetingsMessage = "Войдите в API, чтобы загрузить совещания";
                RecentMeetings.Clear();
                ResetMeetingMetrics("Войдите в API", "После входа здесь появится состояние конвейера");
            }
            else
            {
                var meetings = await _services.Backend.GetMeetingsAsync(cancellationToken);
                RecentMeetings.Clear();
                foreach (var meeting in meetings.OrderByDescending(x => x.CreatedAt).Take(7)) RecentMeetings.Add(meeting);
                await RefreshMeetingMetricsAsync(cancellationToken);
                MeetingsMessage = RecentMeetings.Count == 0 ? "Совещаний пока нет" : string.Empty;
                OnPropertyChanged(nameof(HasMeetings));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ApiAvailable = false;
            ApiStatus = "API недоступен";
            MeetingsMessage = _services.Backend.HasSession ? "Не удалось загрузить совещания" : "Войдите в API, чтобы загрузить совещания";
            ErrorText = string.IsNullOrWhiteSpace(ErrorText) ? SafeError(ex) : ErrorText;
            ServerSummary = "Сервер недоступен";
            WhisperXSummary = "Статус не получен";
            RecentMeetings.Clear();
            ResetMeetingMetrics("История недоступна", "Подключите API, чтобы увидеть конвейер");
            OnPropertyChanged(nameof(HasMeetings));
        }
    }

    private void ResetMeetingMetrics(string title, string stage)
    {
        ProcessingText = "—";
        ReadySummariesText = "—";
        OpenTasksText = "—";
        ActiveProcessingTitle = title;
        ActiveProcessingStage = stage;
        ActiveProcessingProgress = 0;
    }

    private async Task RefreshMeetingMetricsAsync(CancellationToken cancellationToken)
    {
        var meetingIds = RecentMeetings
            .Select(meeting => Guid.TryParse(meeting.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
        try
        {
            var aggregate = await _services.Backend.GetMeetingMetricsAsync(meetingIds, cancellationToken);
            var byMeeting = aggregate.ToDictionary(item => item.MeetingId, StringComparer.OrdinalIgnoreCase);
            var aggregateMetrics = RecentMeetings.Select(meeting =>
            {
                if (!byMeeting.TryGetValue(meeting.Id, out var item))
                    return new MeetingMetrics(meeting.DisplayTitle, false, false, 0, meeting.Status, 0);
                var current = item.Jobs.OrderByDescending(job => job.Attempt).FirstOrDefault(job => !IsTerminal(job.Status))
                    ?? item.Jobs.OrderByDescending(job => job.Attempt).FirstOrDefault();
                return new MeetingMetrics(
                    meeting.DisplayTitle,
                    item.Jobs.Any(job => !IsTerminal(job.Status)),
                    SummaryPresentation.IsDisplayable(item.Summary),
                    item.OpenTasks.Count,
                    current?.Stage ?? meeting.Status,
                    Math.Clamp(current?.Progress ?? 0, 0, 100));
            }).ToArray();
            ApplyMeetingMetrics(aggregateMetrics);
            return;
        }
        catch (DesktopApiException exception) when (exception.ErrorCode is "MEETING_METRICS_UNSUPPORTED")
        {
            // Rolling compatibility: old API versions keep the existing
            // per-meeting calls until the aggregate endpoint is deployed.
        }
        var fallbackMetrics = await Task.WhenAll(RecentMeetings.Select(meeting => LoadMeetingMetricsAsync(meeting, cancellationToken)));
        ApplyMeetingMetrics(fallbackMetrics);
    }

    private void ApplyMeetingMetrics(IReadOnlyList<MeetingMetrics> metrics)
    {
        var processing = metrics.Where(item => item.IsProcessing).ToArray();
        ProcessingText = processing.Length.ToString(CultureInfo.InvariantCulture);
        ReadySummariesText = metrics.Count(item => item.HasSummary).ToString(CultureInfo.InvariantCulture);
        OpenTasksText = metrics.Sum(item => item.OpenTasks).ToString(CultureInfo.InvariantCulture);
        var active = processing.OrderByDescending(item => item.Progress).FirstOrDefault();
        ActiveProcessingTitle = active?.Title ?? "Нет активной обработки";
        ActiveProcessingStage = active is null ? "Все последние совещания обработаны" : $"{DisplayStage(active.Stage)} · {active.Progress}%";
        ActiveProcessingProgress = active?.Progress ?? 0;
    }

    private async Task<MeetingMetrics> LoadMeetingMetricsAsync(DesktopMeeting meeting, CancellationToken cancellationToken)
    {
            if (!Guid.TryParse(meeting.Id, out var meetingId)) return new MeetingMetrics(meeting.DisplayTitle, false, false, 0, "Этап не указан", 0);
        try
        {
            var jobsTask = _services.Backend.GetJobsAsync(meetingId, cancellationToken);
            var summaryTask = _services.Backend.GetSummaryAsync(meetingId, cancellationToken);
            var tasksTask = _services.Backend.GetTasksAsync(meetingId, cancellationToken);
            await Task.WhenAll(jobsTask, summaryTask, tasksTask);
            var jobs = await jobsTask;
            var summary = await summaryTask;
            var tasks = await tasksTask;
            var current = jobs.OrderByDescending(job => job.Attempt).FirstOrDefault(job => !IsTerminal(job.Status)) ?? jobs.OrderByDescending(job => job.Attempt).FirstOrDefault();
            return new MeetingMetrics(
                meeting.DisplayTitle,
                jobs.Any(job => !IsTerminal(job.Status)),
                SummaryPresentation.IsDisplayable(summary),
                tasks.Count(task => !task.Status.Equals("DONE", StringComparison.OrdinalIgnoreCase) && !task.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)),
                current?.Stage ?? meeting.Status,
                Math.Clamp(current?.Progress ?? 0, 0, 100));
        }
        catch
        {
            var isProcessing = !IsTerminal(meeting.Status);
            return new MeetingMetrics(meeting.Title, isProcessing, false, 0, meeting.Status, 0);
        }
    }

    private static string FormatStorage(long free, long total) => free <= 0 || total <= 0
        ? "Нет данных"
        : $"{FormatBytes(free)} свободно из {FormatBytes(total)}";

    private static string FormatMicrophoneSignal(string? state, double? rmsDb)
    {
        var label = state?.ToUpperInvariant() switch
        {
            "READY" => "Сигнал микрофона обнаружен",
            "READY_NO_SIGNAL" => "Поток открыт, сигнала нет",
            "CLIPPING" => "Сигнал перегружен (clipping)",
            "FORMAT_MISMATCH" => "Несовместимый формат аудиобуфера",
            "NO_PACKETS" => "Ожидаются аудиокадры",
            "UNAVAILABLE" => "Сигнал недоступен",
            _ => "Сигнал проверяется перед стартом записи"
        };
        return rmsDb is double value ? $"{label} · RMS {value:0.0} dB" : label;
    }

    private static IReadOnlyList<double> AppendWaveformSample(IReadOnlyList<double> current, double? peak)
    {
        var next = current is { Count: > 0 } ? current.ToList() : [];
        next.Add(Math.Clamp(peak ?? 0d, 0d, 1d));
        if (next.Count > 48) next.RemoveRange(0, next.Count - 48);
        return next;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024L * 1024L) return $"{bytes:N0} Б";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024d / 1024d:N1} МБ";
        return $"{bytes / 1024d / 1024d / 1024d:N1} ГБ";
    }

    private static bool IsTerminal(string status) => status.Equals("READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("PARTIAL_READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("ADMIN_REVIEW", StringComparison.OrdinalIgnoreCase)
        || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);

    private static string DisplayStage(string? stage) => stage switch
    {
        "TRANSCRIBING" => "Транскрибация",
        "ALIGNING" => "Выравнивание",
        "DIARIZING" => "Диаризация",
        "SUMMARIZING" => "Саммари",
        "ADMIN_REVIEW" => "Требуется проверка",
        "READY" => "Готово",
        _ => string.IsNullOrWhiteSpace(stage) ? "Обработка" : stage
    };

    private static string FormatVoiceStatus(WhisperX.Atom.Desktop.DesktopVoiceSnapshot? snapshot, string? microphoneSignalState)
    {
        if (snapshot is null) return "Мифодий недоступен";
        if (snapshot.IsSpeaking || snapshot.State.Equals("RESPONDING", StringComparison.OrdinalIgnoreCase)) return "Мифодий озвучивает ответ";
        if (microphoneSignalState is "NO_PACKETS" or "READY_NO_SIGNAL") return "Мифодий ждёт аудиосигнал";
        return snapshot.State.ToUpperInvariant() switch
        {
            "LISTENING" => "Мифодий слушает · готов к команде",
            "CAPTURING" or "RECOGNIZING" or "WAKEDETECTED" => "Мифодий слушает вопрос",
            "STARTING" => "Мифодий запускается",
            "DEGRADED" or "ERROR" => "Мифодий требует проверки",
            _ => "Мифодий готов"
        };
    }

    private sealed record MeetingMetrics(string Title, bool IsProcessing, bool HasSummary, int OpenTasks, string Stage, int Progress);

    private static string SafeError(Exception ex) => UiErrorFormatter.Format(ex, "Не удалось обновить состояние рабочего стола.");
}

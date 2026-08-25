using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Recorder;
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
    private string _voiceStatusDetail = "Состояние Voice Host проверяется автоматически";
    private string _voicePlaybackStatus = "Озвучка: проверяется";
    private string _recordingStatus = "Проверяется состояние записи…";
    private string _heroTitle = "Готов к новой записи";
    private string _effectiveMicrophoneText = "Микрофон ещё не подтверждён";
    private string _microphoneSignalText = "Сигнал проверяется перед стартом записи";
    private string _microphoneSignalState = "UNKNOWN";
    private double _microphoneLevel;
    private string _microphoneDbLabel = "Нет измерения";
    private string _microphoneRmsDbText = "RMS —";
    private string _microphonePeakDbText = "Peak —";
    private IReadOnlyList<double> _microphoneWaveform = Array.Empty<double>();
    private bool _microphoneTelemetryStale = true;
    private int _voiceProcessingGainDb;
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
    private readonly Dictionary<string, MeetingMetrics> _meetingMetrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalSessionSummary> _localSessionsByMeeting = new(StringComparer.OrdinalIgnoreCase);

    public HomeViewModel(FrontendServices services)
    {
        _services = services;
        VoiceProcessingGainDb = Math.Clamp(services.Settings.Load().VoiceProcessingGainDb, 0, 18);
    }

    public ObservableCollection<DesktopMeeting> RecentMeetings { get; } = [];
    public ObservableCollection<HomeMeetingRowViewModel> RecentMeetingRows { get; } = [];
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool ApiAvailable { get => _apiAvailable; private set => SetProperty(ref _apiAvailable, value); }
    public bool AgentAvailable { get => _agentAvailable; private set => SetProperty(ref _agentAvailable, value); }
    public string ApiStatus { get => _apiStatus; private set => SetProperty(ref _apiStatus, value); }
    public string AgentStatus { get => _agentStatus; private set => SetProperty(ref _agentStatus, value); }
    public string RecorderSummary { get => _recorderSummary; private set => SetProperty(ref _recorderSummary, value); }
    public string ServerSummary { get => _serverSummary; private set => SetProperty(ref _serverSummary, value); }
    public string WhisperXSummary { get => _whisperXSummary; private set => SetProperty(ref _whisperXSummary, value); }
    public string VoiceStatus { get => _voiceStatus; private set => SetProperty(ref _voiceStatus, value); }
    public string VoiceStatusDetail { get => _voiceStatusDetail; private set => SetProperty(ref _voiceStatusDetail, value); }
    public string VoicePlaybackStatus { get => _voicePlaybackStatus; private set => SetProperty(ref _voicePlaybackStatus, value); }
    public string HeroTitle { get => _heroTitle; private set => SetProperty(ref _heroTitle, value); }
    public string RecordingActionText => _recordingState.ToUpperInvariant() switch
    {
        "RECORDING" or "PAUSED" or "FINALIZING" => "Открыть запись",
        "UNAVAILABLE" or "ERROR" => "Проверить запись",
        _ => "Начать запись"
    };
    public string RecordingActionToolTip => _recordingState.ToUpperInvariant() switch
    {
        "RECORDING" or "PAUSED" or "FINALIZING" => "Открыть пульт текущей записи",
        "UNAVAILABLE" or "ERROR" => "Открыть пульт и проверить Recorder Agent",
        _ => "Открыть пульт и начать локальную запись"
    };
    public string RecordingDurationText => _recordingState.ToUpperInvariant() is "RECORDING" or "PAUSED"
        ? $"Длительность {MediaTimeText}"
        : string.Empty;
    public string RecordingStatus { get => _recordingStatus; private set => SetProperty(ref _recordingStatus, value); }
    public string EffectiveMicrophoneText { get => _effectiveMicrophoneText; private set => SetProperty(ref _effectiveMicrophoneText, value); }
    public string MicrophoneSignalText { get => _microphoneSignalText; private set => SetProperty(ref _microphoneSignalText, value); }
    public string MicrophoneSignalState { get => _microphoneSignalState; private set => SetProperty(ref _microphoneSignalState, value); }
    public double MicrophoneLevel { get => _microphoneLevel; private set => SetProperty(ref _microphoneLevel, value); }
    public string MicrophoneDbLabel { get => _microphoneDbLabel; private set => SetProperty(ref _microphoneDbLabel, value); }
    public string MicrophoneRmsDbText { get => _microphoneRmsDbText; private set => SetProperty(ref _microphoneRmsDbText, value); }
    public string MicrophonePeakDbText { get => _microphonePeakDbText; private set => SetProperty(ref _microphonePeakDbText, value); }
    public IReadOnlyList<double> MicrophoneWaveform { get => _microphoneWaveform; private set => SetProperty(ref _microphoneWaveform, value); }
    public bool MicrophoneTelemetryStale { get => _microphoneTelemetryStale; private set => SetProperty(ref _microphoneTelemetryStale, value); }
    public int VoiceProcessingGainDb
    {
        get => _voiceProcessingGainDb;
        private set
        {
            if (!SetProperty(ref _voiceProcessingGainDb, Math.Clamp(value, 0, 18))) return;
            OnPropertyChanged(nameof(VoiceProcessingGainText));
        }
    }
    public string VoiceProcessingGainText => VoiceProcessingGainDb == 0 ? "Без усиления" : $"+{VoiceProcessingGainDb} дБ";
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

    public async Task<bool> SetVoiceProcessingGainAsync(int gainDb, CancellationToken cancellationToken = default)
    {
        var normalized = Math.Clamp(gainDb, 0, 18);
        var current = _services.Settings.Load();
        _services.Settings.Save(current with { VoiceProcessingGainDb = normalized });
        VoiceProcessingGainDb = normalized;
        OnPropertyChanged(nameof(VoiceProcessingGainText));
        var ok = await _services.VoiceHost.SetVoiceProcessingGainAsync(normalized, cancellationToken);
        if (!ok) ErrorText = "Усиление голоса недоступно в текущем Voice Host. Запись и архив не изменены.";
        return ok;
    }

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
            UpdateRecordingPresentation();
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
                MicrophoneRmsDbText = health.MicrophoneRmsDb is double rmsValue ? $"RMS {rmsValue:0.0} dBFS" : "RMS —";
                MicrophonePeakDbText = health.MicrophonePeak is double peakValue && peakValue > 0 ? $"Peak {20d * Math.Log10(Math.Clamp(peakValue, 0.000001, 1d)):0.0} dBFS" : "Peak —";
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
                MicrophoneRmsDbText = "RMS —";
                MicrophonePeakDbText = "Peak —";
                MicrophoneWaveform = Array.Empty<double>();
            }

            try
            {
                var voice = await new WhisperX.Atom.Desktop.VoiceHostClient().GetStatusAsync(cancellationToken);
                var localVoiceSettings = _services.Settings.Load();
                VoiceStatus = FormatVoiceStatus(voice, MicrophoneSignalState, localVoiceSettings.VoiceQuietMode);
                VoiceStatusDetail = FormatVoiceStatusDetail(voice, localVoiceSettings.VoiceQuietMode, _services.Backend.AuthState);
                VoicePlaybackStatus = FormatPlaybackStatus(voice, localVoiceSettings.VoiceQuietMode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                var localVoiceSettings = _services.Settings.Load();
                VoiceStatus = localVoiceSettings.VoiceQuietMode ? "Тихий режим включён" : "Voice Host недоступен";
                VoiceStatusDetail = localVoiceSettings.VoiceQuietMode
                    ? "Озвучка ответов отключена настройкой «Тихий режим»."
                    : "Voice Host не отвечает; локальная запись и её команды не зависят от сервера.";
                VoicePlaybackStatus = localVoiceSettings.VoiceQuietMode
                    ? "Озвучка отключена"
                    : "Озвучка недоступна: Voice Host не отвечает";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AgentAvailable = false;
            _recordingState = "Unavailable";
            _mediaTimeMs = null;
            UpdateRecordingPresentation();
            OnPropertyChanged(nameof(RecordingBadgeText));
            OnPropertyChanged(nameof(MediaTimeText));
            AgentStatus = "Recorder Agent недоступен";
            RecorderSummary = "Recorder недоступен";
            VoiceStatus = "Voice Host недоступен";
            VoiceStatusDetail = "Не удалось получить локальное состояние помощника. Проверьте Desktop и Voice Host.";
            VoicePlaybackStatus = "Озвучка недоступна";
            RecordingStatus = "Запись недоступна";
            StorageText = "Ожидание проверки";
            PendingUploadsText = "—";
            EffectiveMicrophoneText = "Микрофон недоступен";
            MicrophoneSignalState = "UNAVAILABLE";
            MicrophoneSignalText = "Сигнал недоступен — проверьте Recorder Agent";
            MicrophoneTelemetryStale = true;
            MicrophoneLevel = 0;
            MicrophoneDbLabel = "Нет измерения";
            MicrophoneRmsDbText = "RMS —";
            MicrophonePeakDbText = "Peak —";
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
                RecentMeetingRows.Clear();
                ResetMeetingMetrics("История недоступна", "Подключите API, чтобы увидеть конвейер");
            }
            else if (!_services.Backend.HasSession)
            {
                MeetingsMessage = "Войдите в API, чтобы загрузить совещания";
                RecentMeetings.Clear();
                RecentMeetingRows.Clear();
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
            RecentMeetingRows.Clear();
            ResetMeetingMetrics("История недоступна", "Подключите API, чтобы увидеть конвейер");
            OnPropertyChanged(nameof(HasMeetings));
        }
    }

    private void ResetMeetingMetrics(string title, string stage)
    {
        _meetingMetrics.Clear();
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
            _localSessionsByMeeting.Clear();
            try
            {
                var local = await _services.Recorder.ListLocalSessionsAsync(200, cancellationToken);
                foreach (var session in local.LocalSessions ?? [])
                    if (session.MeetingId is Guid meetingId)
                        _localSessionsByMeeting[meetingId.ToString()] = session;
            }
            catch { }
            var aggregate = await _services.Backend.GetMeetingMetricsAsync(meetingIds, cancellationToken);
            var byMeeting = aggregate.ToDictionary(item => item.MeetingId, StringComparer.OrdinalIgnoreCase);
            var aggregateMetrics = RecentMeetings.Select(meeting =>
            {
                if (!byMeeting.TryGetValue(meeting.Id, out var item))
                    return new MeetingMetrics(meeting.Id, meeting.DisplayTitle, false, false, 0, meeting.Status, 0, [], null, null);
                var current = item.Jobs.OrderByDescending(job => job.Attempt).FirstOrDefault(job => !IsTerminal(job.Status))
                    ?? item.Jobs.OrderByDescending(job => job.Attempt).FirstOrDefault();
                return new MeetingMetrics(
                    meeting.Id,
                    meeting.DisplayTitle,
                    item.Jobs.Any(job => !IsTerminal(job.Status)),
                    SummaryPresentation.IsDisplayable(item.Summary),
                    item.OpenTasks.Count,
                    current?.Stage ?? meeting.Status,
                    Math.Clamp(current?.Progress ?? 0, 0, 100),
                    item.Pipeline,
                    item.Jobs.FirstOrDefault(job => job.Retryable),
                    item.Summary?.ErrorCode);
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
        ActiveProcessingStage = active is null ? "Все последние совещания обработаны" : active.DisplayStage;
        ActiveProcessingProgress = 0;
        _meetingMetrics.Clear();
        foreach (var item in metrics) _meetingMetrics[item.MeetingId] = item;
        RecentMeetingRows.Clear();
        foreach (var meeting in RecentMeetings)
        {
            _meetingMetrics.TryGetValue(meeting.Id, out var item);
            _localSessionsByMeeting.TryGetValue(meeting.Id, out var localSession);
            RecentMeetingRows.Add(HomeMeetingRowViewModel.Create(meeting, item, localSession));
        }
        OnPropertyChanged(nameof(RecentMeetingRows));
    }

    private async Task<MeetingMetrics> LoadMeetingMetricsAsync(DesktopMeeting meeting, CancellationToken cancellationToken)
    {
            if (!Guid.TryParse(meeting.Id, out var meetingId)) return new MeetingMetrics(meeting.Id, meeting.DisplayTitle, false, false, 0, "Этап не указан", 0, [], null, null);
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
                meeting.Id,
                meeting.DisplayTitle,
                jobs.Any(job => !IsTerminal(job.Status)),
                SummaryPresentation.IsDisplayable(summary),
                tasks.Count(task => !task.Status.Equals("DONE", StringComparison.OrdinalIgnoreCase) && !task.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)),
                current?.Stage ?? meeting.Status,
                Math.Clamp(current?.Progress ?? 0, 0, 100),
                [],
                jobs.FirstOrDefault(job => job.Retryable),
                summary?.ErrorCode);
        }
        catch
        {
            var isProcessing = !IsTerminal(meeting.Status);
            return new MeetingMetrics(meeting.Id, meeting.Title, isProcessing, false, 0, meeting.Status, 0, [], null, "MEETING_METRICS_UNAVAILABLE");
        }
    }

    private static string FormatStorage(long free, long total) => free <= 0 || total <= 0
        ? "Нет данных"
        : $"{FormatBytes(free)} свободно из {FormatBytes(total)}";

    private void UpdateRecordingPresentation()
    {
        HeroTitle = _recordingState.ToUpperInvariant() switch
        {
            "RECORDING" => "Запись идёт",
            "PAUSED" => "Запись на паузе",
            "FINALIZING" => "Сохраняем запись",
            "ERROR" => "Запись требует внимания",
            "UNAVAILABLE" => "Запись недоступна",
            _ => "Готов к новой записи"
        };
        OnPropertyChanged(nameof(RecordingActionText));
        OnPropertyChanged(nameof(RecordingActionToolTip));
        OnPropertyChanged(nameof(RecordingDurationText));
    }

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
        || status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("CONFIRMED", StringComparison.OrdinalIgnoreCase)
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

    private static string FormatVoiceStatus(WhisperX.Atom.Desktop.DesktopVoiceSnapshot? snapshot, string? microphoneSignalState, bool configuredQuietMode)
    {
        if (snapshot is null) return configuredQuietMode ? "Тихий режим включён" : "Voice Host недоступен";
        if (snapshot.QuietMode) return "Тихий режим включён";
        if (snapshot.IsSpeaking || snapshot.State.Equals("RESPONDING", StringComparison.OrdinalIgnoreCase)) return "Мифодий озвучивает ответ";
        if (!snapshot.TtsReady && !snapshot.TtsFallbackUsed) return "Озвучка недоступна";
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

    private static string FormatVoiceStatusDetail(WhisperX.Atom.Desktop.DesktopVoiceSnapshot? snapshot, bool configuredQuietMode, DesktopAuthState authState)
    {
        if (configuredQuietMode || snapshot?.QuietMode == true)
            return "Тихий режим включён. Чтобы слышать ответы, выключите его в Настройки → Мифодий.";
        if (snapshot is null)
            return "Voice Host не запущен или недоступен. Откройте Desktop заново и проверьте голос.";
        if (!snapshot.TtsReady && !snapshot.TtsFallbackUsed)
            return $"Локальный TTS не готов ({snapshot.TtsFallbackReason ?? snapshot.LastErrorCode ?? "причина не указана"}). Запись остаётся доступной.";
        if (authState == DesktopAuthState.LoginRequired)
            return "Сессия сервера истекла. Локальная озвучка работает отдельно, Assistant и Qwen требуют повторного входа.";
        if (snapshot.TtsFallbackUsed)
            return "Silero недоступен, используется русский Windows fallback.";
        return "Локальная озвучка готова; сервер для неё не требуется.";
    }

    private static string FormatPlaybackStatus(WhisperX.Atom.Desktop.DesktopVoiceSnapshot? snapshot, bool configuredQuietMode)
    {
        if (configuredQuietMode || snapshot?.QuietMode == true) return "Озвучка отключена";
        if (snapshot is null) return "Озвучка не проверена";
        if (snapshot.TtsFallbackUsed) return "Озвучка готова · Windows fallback";
        return snapshot.TtsReady ? $"Озвучка готова · {snapshot.TtsEngine}" : "Озвучка недоступна";
    }

    public sealed record MeetingMetrics(string MeetingId, string Title, bool IsProcessing, bool HasSummary, int OpenTasks, string Stage, int Progress, IReadOnlyList<DesktopPipelineRun> Pipeline, DesktopJob? RetryableJob, string? ErrorCode)
    {
        public string DisplayStage => Stage switch
        {
            "TRANSCRIBING" or "ALIGNING" or "DIARIZING" => "Получение стенограммы",
            "SUMMARIZING" => "Саммари",
            "ADMIN_REVIEW" => "Требует проверки",
            _ => string.IsNullOrWhiteSpace(Stage) ? "Этап не указан" : DisplayStageValue(Stage)
        };

        private static string DisplayStageValue(string value) => value switch
        {
            "READY" or "PARTIAL_READY" => "Стенограмма готова",
            _ => value
        };
    }

    private static string SafeError(Exception ex) => UiErrorFormatter.Format(ex, "Не удалось обновить состояние рабочего стола.");
}

public sealed class HomeMeetingRowViewModel
{
    private static readonly IReadOnlyList<string> PipelineSteps =
        ["Доставляется", "Доставлено", "Получение стенограммы", "Стенограмма готова", "Саммари", "Саммари готово"];

    private HomeMeetingRowViewModel(DesktopMeeting meeting, MeetingRowState state)
    {
        Meeting = meeting;
        CreatedAtText = meeting.CreatedAtText;
        DisplayTitle = meeting.DisplayTitle;
        Description = meeting.Description ?? string.Empty;
        Steps = PipelineSteps;
        CurrentIndex = state.CurrentIndex;
        CurrentStageText = state.CurrentStageText;
        ErrorText = state.ErrorText;
        LocalSessionId = state.LocalSessionId;
        RetryJobId = state.RetryJobId;
        CanStopDelivery = state.CanStopDelivery;
        CanRetry = state.CanRetry;
        CanCancel = state.CanCancel;
        CanDelete = state.CanDelete;
    }

    public DesktopMeeting Meeting { get; }
    public string Id => Meeting.Id;
    public string CreatedAtText { get; }
    public string DisplayTitle { get; }
    public string Description { get; }
    public string StatusText => Meeting.StatusText;
    public IReadOnlyList<string> Steps { get; }
    public int CurrentIndex { get; }
    public string CurrentStageText { get; }
    public string? ErrorText { get; }
    public string? LocalSessionId { get; }
    public string? RetryJobId { get; }
    public bool CanStopDelivery { get; }
    public bool CanRetry { get; }
    public bool CanCancel { get; }
    public bool CanDelete { get; }

    public static HomeMeetingRowViewModel Create(DesktopMeeting meeting, HomeViewModel.MeetingMetrics? metrics, LocalSessionSummary? localSession)
    {
        string? error = null;
        var stage = meeting.Status;
        var currentIndex = 0;
        string? retryJobId = null;
        var hasSummary = false;
        IReadOnlyList<DesktopPipelineRun> pipeline = [];
        if (metrics is not null)
        {
            stage = metrics.Stage;
            hasSummary = metrics.HasSummary;
            retryJobId = metrics.RetryableJob?.Id;
            error = metrics.ErrorCode;
            pipeline = metrics.Pipeline ?? [];
        }

        var snapshot = pipeline.Select(run => run.Snapshot).FirstOrDefault(item => item is not null);
        var deliveryDone = IsDone(snapshot?.Delivery.Status)
            || pipeline.Any(run => IsDone(run.MediaStatus))
            || meeting.Status.Equals("READY", StringComparison.OrdinalIgnoreCase)
            || meeting.Status.Equals("PARTIAL_READY", StringComparison.OrdinalIgnoreCase);
        var mediaDone = IsDone(snapshot?.Media.Status) || pipeline.Any(run => IsDone(run.MediaStatus));
        var transcriptDone = IsDone(snapshot?.TranscriptV2.Status)
            || IsDone(snapshot?.TranscriptV1.Status)
            || pipeline.Any(run => IsDone(run.TranscriptV2Status) || IsDone(run.TranscriptV1Status))
            || meeting.Status.Equals("READY", StringComparison.OrdinalIgnoreCase)
            || meeting.Status.Equals("PARTIAL_READY", StringComparison.OrdinalIgnoreCase);
        var summaryRunning = IsRunning(snapshot?.Summary.Status)
            || pipeline.Any(run => IsRunning(run.SummaryJobStatus));
        if (localSession is not null && localSession.DeliveryState is not ("CONFIRMED" or "COMPLETED")) deliveryDone = false;

        // Older API versions may not expose a pipeline snapshot. Keep the
        // compact stepper truthful by using the job stage as a compatibility
        // signal instead of falling back to the first delivery step.
        switch (stage.ToUpperInvariant())
        {
            case "MEDIA_READY":
            case "ASR_QUEUED":
            case "TRANSCRIBING":
            case "ALIGNING":
            case "DIARIZING":
            case "SUMMARIZING":
            case "READY":
            case "PARTIAL_READY":
                deliveryDone = true;
                mediaDone = true;
                break;
        }
        if (stage.Equals("READY", StringComparison.OrdinalIgnoreCase) || stage.Equals("PARTIAL_READY", StringComparison.OrdinalIgnoreCase))
            transcriptDone = true;

        if (!deliveryDone) currentIndex = 0;
        else if (!mediaDone) currentIndex = 1;
        else if (!transcriptDone) currentIndex = 2;
        else if (hasSummary) currentIndex = 5;
        else if (summaryRunning || stage.Equals("SUMMARIZING", StringComparison.OrdinalIgnoreCase)) currentIndex = 4;
        else currentIndex = 3;

        if (meeting.Status is "FAILED" or "ADMIN_REVIEW" && string.IsNullOrWhiteSpace(error)) error = meeting.Status;
        if (!string.IsNullOrWhiteSpace(error)) error = FormatMeetingError(error);
        var activeRecording = localSession?.State is "RECORDING" or "PAUSED";
        var canStop = !activeRecording && localSession is not null
            && localSession.DeliveryState is not ("CONFIRMED" or "COMPLETED" or "PAUSED")
            && localSession.State is not ("FINALIZED" or "CANCELLED");
        var canRetry = !activeRecording && (localSession is not null
            && (localSession.DeliveryState is "PAUSED" or "DELIVERY_ERROR" or "DELIVERY_FAILED" or "PENDING_SERVER" or "WAITING_SERVER" or "WAITING_SERVER_ASSEMBLY")
            || !string.IsNullOrWhiteSpace(retryJobId));
        var canCancel = !activeRecording && meeting.Status is not ("READY" or "PARTIAL_READY" or "FAILED" or "CANCELLED" or "ADMIN_REVIEW");
        var canDelete = !activeRecording && Guid.TryParse(meeting.Id, out _);
        return new HomeMeetingRowViewModel(meeting, new MeetingRowState(
            currentIndex,
            PipelineSteps[currentIndex],
            error,
            localSession?.SessionId,
            retryJobId,
            canStop,
            canRetry,
            canCancel,
            CanDelete: canDelete));
    }

    private static bool IsDone(string? value) => value is "READY" or "COMPLETED" or "CONFIRMED" or "PARTIAL_READY";
    private static bool IsRunning(string? value) => !string.IsNullOrWhiteSpace(value) && value is not ("READY" or "COMPLETED" or "FAILED" or "CANCELLED" or "PARTIAL_READY");
    private static string FormatMeetingError(string value) => value switch
    {
        "ADMIN_REVIEW" => "Требует проверки",
        "FAILED" => "Ошибка обработки",
        _ => $"Требует проверки: {value}"
    };
    private sealed record MeetingRowState(int CurrentIndex, string CurrentStageText, string? ErrorText, string? LocalSessionId, string? RetryJobId, bool CanStopDelivery, bool CanRetry, bool CanCancel, bool CanDelete);
}

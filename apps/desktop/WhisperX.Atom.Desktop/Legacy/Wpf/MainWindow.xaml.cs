using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.ComponentModel;
using Microsoft.Win32;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Desktop;

public partial class MainWindow : Window
{
    private readonly AgentPipeClient _agent = new();
    private readonly VoiceHostClient _voice = new();
    private bool _voicePttActive;
    private ServerApiClient _server = new();
    private readonly DispatcherTimer _timer;
    private DesktopMeeting? _selectedMeeting;
    private IReadOnlyList<DesktopSpeaker> _speakers = [];
    private DesktopTask? _selectedTask;
    private bool _updatingSlider;
    private CancellationTokenSource? _processingPollCts;
    private CancellationTokenSource? _detailsCts;
    private ICollectionView? _meetingsView;
    private int _statusRefreshGate;
    private bool _deviceCheckInProgress;
    private bool _postStopProcessing;
    private bool _processingErrorVisible;
    private bool _processingCompleted;
    private Guid? _processingMeetingId;
    private Guid? _failedProcessingJobId;
    private AgentIpcHealth? _lastAgentHealth;
    private string _archiveRoot = DesktopSettings.DefaultArchiveRoot();
    private string? _microphoneDeviceId;
    private string? _systemAudioDeviceId;
    private bool _updatingAudioDeviceSelection;
    private string? _lastSyncedArchiveRoot;
    private string? _localArchiveDirectory;
    private string? _localArchiveSessionId;
    private const string GlobalSearchPlaceholder = "Поиск по совещаниям, стенограммам и задачам...";

    private sealed record DashboardFacts(bool SummaryQueried, bool SummaryReady, bool TasksQueried, int OpenTasks, bool JobsQueried, bool HasActiveJob);

    public MainWindow()
    {
        InitializeComponent();
        var settings = DesktopSettings.Load();
        var displayName = string.IsNullOrWhiteSpace(settings.Username) ? "Локальный пользователь" : settings.Username.Trim();
        HeaderUserNameText.Text = displayName;
        HeaderUserEmailText.Text = string.Empty;
        HeaderAvatarText.Text = displayName.Length >= 2 ? displayName[..2].ToUpperInvariant() : "WX";
        ApiUrlTextBox.Text = settings.ApiUrl;
        UsernameTextBox.Text = settings.Username;
        var configuredArchiveRoot = string.IsNullOrWhiteSpace(settings.ArchiveRoot) ? DesktopSettings.DefaultArchiveRoot() : settings.ArchiveRoot!;
        var legacyRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhisperX Atom"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "WhisperX Atom")
        };
        // Keep old archives in place, but route the next recording to a path outside
        // Documents, which may be protected by Windows Defender Controlled Folder Access.
        _archiveRoot = legacyRoots.Any(path => string.Equals(configuredArchiveRoot, path, StringComparison.OrdinalIgnoreCase))
            ? DesktopSettings.DefaultArchiveRoot()
            : configuredArchiveRoot;
        _microphoneDeviceId = NormalizeDeviceId(settings.MicrophoneDeviceId);
        _systemAudioDeviceId = NormalizeDeviceId(settings.SystemAudioDeviceId);
        try { Directory.CreateDirectory(_archiveRoot); } catch { /* Recorder Agent reports an actionable error if the path is unavailable. */ }
        ArchiveRootTextBox.Text = _archiveRoot;
        _server.Dispose();
        _server = new ServerApiClient(settings.ApiUrl);
        _server.RestoreSession(settings.UnprotectSessionCookie());
        if (!string.Equals(settings.ArchiveRoot, _archiveRoot, StringComparison.OrdinalIgnoreCase))
            DesktopSettings.Save(_server.BaseAddress.ToString(), settings.Username, _server.GetSessionCookie(), _archiveRoot, _microphoneDeviceId, _systemAudioDeviceId);
        AgentRegistrationPanel.IsEnabled = !string.IsNullOrWhiteSpace(_server.GetSessionCookie());
        AgentRegistrationPanel.Opacity = AgentRegistrationPanel.IsEnabled ? 1.0 : 0.58;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) =>
        {
            await RefreshStatusAsync();
            UpdatePlayerPosition();
        };
        ResetDashboard("Войдите в API, чтобы загрузить совещания");
    }

    private void ResetDashboard(string message)
    {
        DashboardMeetingsList.ItemsSource = Array.Empty<DesktopMeeting>();
        DashboardMeetingsEmptyText.Text = message;
        DashboardMeetingsEmptyHintText.Text = message.Contains("Не удалось", StringComparison.OrdinalIgnoreCase)
            ? "Проверьте подключение к API и повторите обновление"
            : "Войдите в API или импортируйте первую запись";
        DashboardMeetingsEmptyActions.Visibility = Visibility.Visible;
        DashboardMeetingsEmptyText.Visibility = Visibility.Visible;
        DashboardMeetingsEmptyState.Visibility = Visibility.Visible;
        DashboardProcessingText.Text = "—";
        DashboardSummariesText.Text = "—";
        DashboardTasksText.Text = "—";
        DashboardGpuText.Text = "Нет данных";
        DashboardStorageMetricText.Text = "Ожидание проверки";
        DashboardStoragePercentText.Text = "—";
        DashboardStorageBar.Value = 0;
        RecordingTimerText.Text = "—";
        RecordingTimerText.Visibility = Visibility.Visible;
        RecordingReadinessText.Text = message.Contains("Не удалось", StringComparison.OrdinalIgnoreCase)
            ? "Подключите Recorder Agent и повторите проверку устройств"
            : "Проверьте устройства перед началом записи";
        ProcessingRetryButton.Visibility = Visibility.Collapsed;
        ProcessingOpenTranscriptButton.Visibility = Visibility.Collapsed;
    }

    private async Task RefreshDashboardMetricsAsync(IReadOnlyList<DesktopMeeting> meetings, CancellationToken cancellationToken = default)
    {
        var recent = meetings.OrderByDescending(item => item.CreatedAt).Take(7).ToArray();
        DashboardMeetingsList.ItemsSource = recent;
        DashboardMeetingsEmptyText.Visibility = recent.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DashboardMeetingsEmptyState.Visibility = recent.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DashboardMeetingsEmptyText.Text = recent.Length == 0 ? "Нет загруженных совещаний" : string.Empty;
        DashboardMeetingsEmptyHintText.Text = "Войдите в API или импортируйте первую запись";
        DashboardMeetingsEmptyActions.Visibility = recent.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (meetings.Count == 0)
        {
            DashboardProcessingText.Text = "0";
            DashboardSummariesText.Text = "0";
            DashboardTasksText.Text = "0";
            return;
        }

        var candidates = meetings
            .Where(item => Guid.TryParse(item.Id, out _))
            .Take(30)
            .ToArray();
        var facts = await Task.WhenAll(candidates.Select(item => GetDashboardFactsAsync(Guid.Parse(item.Id), cancellationToken)));

        var summaryFacts = facts.Where(item => item.SummaryQueried).ToArray();
        var taskFacts = facts.Where(item => item.TasksQueried).ToArray();
        var jobFacts = facts.Where(item => item.JobsQueried).ToArray();
        DashboardProcessingText.Text = jobFacts.Length > 0
            ? jobFacts.Count(item => item.HasActiveJob).ToString(CultureInfo.InvariantCulture)
            : meetings.Count(item => !IsTerminalMeetingStatus(item.Status)).ToString(CultureInfo.InvariantCulture);
        DashboardSummariesText.Text = summaryFacts.Length > 0
            ? summaryFacts.Count(item => item.SummaryReady).ToString(CultureInfo.InvariantCulture)
            : "—";
        DashboardTasksText.Text = taskFacts.Length > 0
            ? taskFacts.Sum(item => item.OpenTasks).ToString(CultureInfo.InvariantCulture)
            : "—";
    }

    private async Task<DashboardFacts> GetDashboardFactsAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        var summaryQueried = false;
        var summaryReady = false;
        var tasksQueried = false;
        var openTasks = 0;
        var jobsQueried = false;
        var hasActiveJob = false;

        try
        {
            var summary = await _server.GetSummaryAsync(meetingId, cancellationToken);
            summaryQueried = true;
            summaryReady = summary is not null && string.Equals(summary.Status, "READY", StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        try
        {
            var tasks = await _server.GetTasksAsync(meetingId, cancellationToken);
            tasksQueried = true;
            openTasks = tasks.Count(item => !IsTerminalTaskStatus(item.Status));
        }
        catch { }

        try
        {
            var jobs = await _server.GetJobsAsync(meetingId, cancellationToken);
            jobsQueried = true;
            hasActiveJob = jobs.Any(item => !IsTerminalJobStatus(item.Status));
        }
        catch { }

        return new DashboardFacts(summaryQueried, summaryReady, tasksQueried, openTasks, jobsQueried, hasActiveJob);
    }

    private static bool IsTerminalMeetingStatus(string status) => status.ToUpperInvariant() is "READY" or "DONE" or "COMPLETED" or "FAILED" or "CANCELLED";
    private static bool IsTerminalJobStatus(string status) => status.ToUpperInvariant() is "READY" or "DONE" or "COMPLETED" or "FAILED" or "CANCELLED";
    private static bool IsTerminalTaskStatus(string status) => status.ToUpperInvariant() is "DONE" or "COMPLETED" or "CLOSED" or "CANCELLED";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _ = LoadMeetingsAsync();
            _ = RefreshStatusAsync();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.I && ImportButton.IsEnabled)
        {
            ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R && StartRecordingButton.IsEnabled)
        {
            StartRecordingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            MeetingSearchBox.Focus();
            MeetingSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase && e.OriginalSource is not System.Windows.Controls.PasswordBox)
        {
            if (PreviewPlayer.NaturalDuration.HasTimeSpan) PreviewPlayer.Pause();
            else PreviewPlayer.Play();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        await RefreshStatusAsync();
        await CheckServerAsync();
        if (!string.IsNullOrWhiteSpace(_server.GetSessionCookie()))
            await LoadMeetingsAsync();
        else
            ResetDashboard("Войдите в API, чтобы загрузить совещания");
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _processingPollCts?.Cancel();
        _processingPollCts?.Dispose();
        _processingPollCts = null;
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = null;
        PreviewPlayer.Stop();
        _server.Dispose();
    }

    private async Task RefreshVoiceStatusAsync()
    {
        try
        {
            var snapshot = await _voice.GetStatusAsync();
            if (snapshot is null) { VoiceStateText.Text = "SessionHost не подключён"; return; }
            VoiceStateText.Text = $"Состояние: {snapshot.State}; {(snapshot.Enabled ? "включён" : "выключен")}; {(snapshot.IsSpeaking ? "отвечает" : "ожидает")}";
            VoiceReadinessText.Text = $"Модель: {(snapshot.ModelReady ? "готова" : "нет")}; целостность: {(snapshot.ModelIntegrityReady ? "OK" : "ошибка")}; native Vosk: {(snapshot.NativeRuntimeReady ? "OK" : "нет")}; микрофон: {(snapshot.MicrophoneReady ? "готов" : "недоступен")}; Recorder: {(snapshot.RecorderPipeReady ? "подключён" : snapshot.RecorderPipeError ?? "недоступен")}";
            VoiceMetricsText.Text = $"Wake: {Metric(snapshot.WakeLatencyMs)}; intent: {Metric(snapshot.IntentLatencyMs)}; Recorder ACK: {Metric(snapshot.RecorderAckLatencyMs)}; всего: {Metric(snapshot.TotalLatencyMs)}; очередь: {snapshot.AudioQueueDepth}; сбросы: {snapshot.AudioQueueDrops}";
            VoiceLastText.Text = string.IsNullOrWhiteSpace(snapshot.LastRecognizedText) ? "Команда ещё не распознана" : $"Последняя команда: {snapshot.LastRecognizedText}\nIntent: {snapshot.LastIntent ?? "—"}\nОтвет: {snapshot.LastResponse}";
            VoiceErrorText.Text = snapshot.LastErrorCode is null ? string.Empty : $"Диагностика: {snapshot.LastErrorCode}";
        }
        catch (Exception ex) { VoiceStateText.Text = "SessionHost недоступен"; VoiceErrorText.Text = SafeError(ex); }
    }
    private async Task RefreshStatusAsync()
    {
        if (Interlocked.Exchange(ref _statusRefreshGate, 1) == 1) return;
        try
        {
            var status = await _agent.SendAsync("HEALTH");
            await SyncArchiveRootAsync(status.Ok);
            AgentStatusText.Text = status.Ok ? "Подключён" : "Недоступен";
            AgentStatusText.Foreground = status.Ok ? Brushes.ForestGreen : Brushes.OrangeRed;
            _lastAgentHealth = status.Health;
            var displayState = _processingErrorVisible && string.Equals(status.State, "Idle", StringComparison.OrdinalIgnoreCase)
                ? "Error"
                : _postStopProcessing && string.Equals(status.State, "Idle", StringComparison.OrdinalIgnoreCase)
                    ? "Processing"
                    : status.State;
            UpdateRecordingPresentation(displayState, status.Ok, status.Health, status.Error);
            var hasActiveRecording = status.State is "Recording" or "Paused";
            RecordingTimerText.Text = hasActiveRecording ? FormatMediaTime(status.MediaTimeMs) : "—";
            RecordingTimerText.Visibility = Visibility.Visible;
            DashboardActiveText.Text = status.Ok && hasActiveRecording ? "1" : status.Ok ? "0" : "—";
            DashboardActiveHintText.Text = status.Ok ? "По данным Recorder Agent" : "Recorder Agent недоступен";
            SessionText.Text = string.IsNullOrWhiteSpace(status.SessionId) ? "Сессия не создана" : $"Сессия: {status.SessionId}";
            if (status.Health is not null)
            {
                var health = status.Health;
                var archiveRoot = string.IsNullOrWhiteSpace(health.ArchiveRoot) ? _archiveRoot : health.ArchiveRoot;
                UpdateAudioDeviceSelectors(health);
                ArchivePathText.Text = health.PendingUploadSessions > 0
                    ? $"Архив: {archiveRoot} · ожидают отправки: {health.PendingUploadSessions}"
                    : $"Архив: {archiveRoot} · локальная копия сохраняется до подтверждения сервера";
                ArchiveStatusText.Text = health.PendingUploadSessions > 0
                    ? $"В очереди доставки: {health.PendingUploadSessions}. Agent повторит отправку автоматически."
                    : "Записи сохраняются в выбранную папку до подтверждения сервера.";
                MicrophoneText.Text = $"Микрофон: {(health.Microphone ? "готов" : "не найден")} · {health.CaptureDeviceCount} устройств";
                SystemAudioText.Text = $"Системный звук: {(health.SystemAudio ? "готов" : "не найден")} · {health.RenderDeviceCount} устройств";
                MicrophoneHintText.Text = health.Microphone ? "Уровень появится после начала записи" : "Подключите или выберите микрофон";
                SystemAudioHintText.Text = health.SystemAudio ? "Уровень появится после начала записи" : "Проверьте системный источник";
                StorageText.Text = $"Диск: {FormatBytes(health.FreeBytes)} свободно из {FormatBytes(health.TotalBytes)}";
                DashboardStorageMetricText.Text = $"{FormatBytes(health.FreeBytes)} из {FormatBytes(health.TotalBytes)}";
                var usedPercent = health.TotalBytes > 0
                    ? Math.Clamp((1d - health.FreeBytes / (double)health.TotalBytes) * 100d, 0d, 100d)
                    : 0d;
                DashboardStorageBar.Value = usedPercent;
                DashboardStoragePercentText.Text = $"{usedPercent:0}% занято";
                DeviceErrorText.Text = health.Error ?? string.Empty;
                DeviceErrorText.Visibility = string.IsNullOrWhiteSpace(health.Error) ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                _lastAgentHealth = null;
                ClearAudioDeviceSelectors();
                MicrophoneText.Text = "Микрофон: нет данных";
                SystemAudioText.Text = "Системный звук: нет данных";
                MicrophoneHintText.Text = "Проверка устройства не вернула состояние";
                SystemAudioHintText.Text = "Проверка устройства не вернула состояние";
                StorageText.Text = "Диск: ожидание проверки";
                DeviceErrorText.Text = string.Empty;
                DeviceErrorText.Visibility = Visibility.Collapsed;
            }
            LastErrorText.Text = status.Error ?? string.Empty;
            FooterText.Text = $"Последняя проверка: {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            AgentStatusText.Text = "Недоступен";
            AgentStatusText.Foreground = Brushes.OrangeRed;
            AgentStatusIndicator.Fill = Brushes.OrangeRed;
            _lastAgentHealth = null;
            ClearAudioDeviceSelectors();
            UpdateRecordingPresentation("Unavailable", false, null, SafeError(ex));
            DashboardActiveText.Text = "—";
            DashboardActiveHintText.Text = "Recorder Agent недоступен";
            DashboardStorageMetricText.Text = "Нет данных";
            DashboardStoragePercentText.Text = "—";
            DashboardStorageBar.Value = 0;
            DashboardGpuText.Text = "Нет данных";
            RecordingTimerText.Text = "—";
            RecordingTimerText.Visibility = Visibility.Visible;
            MicrophoneText.Text = "Микрофон: нет данных";
            SystemAudioText.Text = "Системный звук: нет данных";
            MicrophoneHintText.Text = "Подключите Recorder Agent для проверки";
            SystemAudioHintText.Text = "Подключите Recorder Agent для проверки";
            StorageText.Text = "Диск: ожидание проверки";
            DeviceErrorText.Text = string.Empty;
            DeviceErrorText.Visibility = Visibility.Collapsed;
            LastErrorText.Text = SafeError(ex);
            FooterText.Text = "Recorder Service не отвечает";
        }
        finally
        {
            Volatile.Write(ref _statusRefreshGate, 0);
        }
    }

    private void UpdateAudioDeviceSelectors(AgentIpcHealth health)
    {
        _updatingAudioDeviceSelection = true;
        try
        {
            if (_microphoneDeviceId is null) _microphoneDeviceId = NormalizeDeviceId(health.SelectedMicrophoneDeviceId);
            if (_systemAudioDeviceId is null) _systemAudioDeviceId = NormalizeDeviceId(health.SelectedSystemAudioDeviceId);
            MicrophoneDeviceBox.ItemsSource = BuildDeviceChoices(health.CaptureDevices, _microphoneDeviceId, "Выбранный микрофон недоступен");
            SystemAudioDeviceBox.ItemsSource = BuildDeviceChoices(health.RenderDevices, _systemAudioDeviceId, "Выбранный источник недоступен");
            MicrophoneDeviceBox.SelectedValue = _microphoneDeviceId ?? string.Empty;
            SystemAudioDeviceBox.SelectedValue = _systemAudioDeviceId ?? string.Empty;
        }
        finally { _updatingAudioDeviceSelection = false; }
    }

    private void ClearAudioDeviceSelectors()
    {
        _updatingAudioDeviceSelection = true;
        try
        {
            MicrophoneDeviceBox.ItemsSource = null;
            SystemAudioDeviceBox.ItemsSource = null;
        }
        finally { _updatingAudioDeviceSelection = false; }
    }

    private static IReadOnlyList<AgentIpcAudioDevice> BuildDeviceChoices(
        IReadOnlyList<AgentIpcAudioDevice>? devices,
        string? selectedId,
        string unavailableLabel)
    {
        var result = new List<AgentIpcAudioDevice> { new(string.Empty, "Windows по умолчанию", true) };
        if (devices is not null) result.AddRange(devices);
        if (!string.IsNullOrWhiteSpace(selectedId) && result.All(device => !string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
            result.Add(new AgentIpcAudioDevice(selectedId, unavailableLabel, false, "Unavailable"));
        return result;
    }

    private async void AudioDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAudioDeviceSelection || !IsLoaded) return;
        _microphoneDeviceId = NormalizeDeviceId(MicrophoneDeviceBox.SelectedValue as string);
        _systemAudioDeviceId = NormalizeDeviceId(SystemAudioDeviceBox.SelectedValue as string);
        DesktopSettings.Save(_server.BaseAddress.ToString(), UsernameTextBox.Text, _server.GetSessionCookie(), _archiveRoot, _microphoneDeviceId, _systemAudioDeviceId);
        try
        {
            var response = await _agent.SendAsync("SET_AUDIO_DEVICES", new
            {
                microphoneDeviceId = _microphoneDeviceId,
                systemAudioDeviceId = _systemAudioDeviceId,
            });
            DeviceErrorText.Text = response.Ok ? string.Empty : response.Error ?? "Не удалось сохранить выбор устройств";
            DeviceErrorText.Visibility = string.IsNullOrWhiteSpace(DeviceErrorText.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (response.Ok) await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            DeviceErrorText.Text = $"Выбор сохранён в Desktop, но Agent недоступен: {SafeError(ex)}";
            DeviceErrorText.Visibility = Visibility.Visible;
        }
    }

    private static string? NormalizeDeviceId(string? deviceId) => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();

    private void UpdateRecordingPresentation(string state, bool connected, AgentIpcHealth? health, string? error)
    {
        var normalized = state.Trim();
        var devicesReady = health is { Microphone: true, SystemAudio: true };
        var isChecking = _deviceCheckInProgress && string.Equals(normalized, "Idle", StringComparison.OrdinalIgnoreCase);
        var visibleState = isChecking ? "Checking" : normalized;

        RecordingEyebrowText.Text = visibleState switch
        {
            "Recording" => "АКТИВНАЯ ЗАПИСЬ",
            "Paused" => "ЗАПИСЬ НА ПАУЗЕ",
            "Checking" => "ПРОВЕРКА УСТРОЙСТВ",
            "Finalizing" => "ЗАВЕРШЕНИЕ ЗАПИСИ",
            "Processing" => "ОБРАБОТКА ЗАПИСИ",
            "Error" => "ОШИБКА ЗАПИСИ",
            "Unavailable" => "RECORDER AGENT НЕДОСТУПЕН",
            _ => "ГОТОВНОСТЬ К ЗАПИСИ",
        };
        RecordingStateText.Text = visibleState switch
        {
            "Recording" => "Идёт запись",
            "Paused" => "Пауза",
            "Checking" => "Проверяем устройства",
            "Finalizing" => "Сохранение записи",
            "Processing" => "Обработка",
            "Error" => "Ошибка записи",
            "Unavailable" => "Recorder Agent недоступен",
            _ => "Готово к записи",
        };
        RecordingIndicator.Fill = visibleState switch
        {
            "Recording" => Brushes.Crimson,
            "Paused" => Brushes.DarkOrange,
            "Checking" => Brushes.DodgerBlue,
            "Finalizing" or "Processing" => Brushes.MediumPurple,
            "Error" or "Unavailable" => Brushes.OrangeRed,
            _ => Brushes.ForestGreen,
        };
        SourceActionButton.Content = connected ? "Настроить источник" : "Выбрать источник";
        RecordingReadinessText.Text = visibleState switch
        {
            "Recording" => "Запись идёт. Метки сохраняются локально в текущую сессию.",
            "Paused" => "Запись приостановлена. Продолжите или завершите её.",
            "Checking" => "Получаем реальное состояние Recorder Agent и устройств…",
            "Finalizing" or "Processing" => "Файл сохраняется, затем будет передан в pipeline обработки.",
            "Error" => error ?? "Проверьте сообщение об ошибке и повторите обработку.",
            "Unavailable" => "Подключите Recorder Agent и повторите проверку устройств.",
            _ when !connected => "Подключите Recorder Agent перед началом записи.",
            _ when !devicesReady => "Нужно подключить микрофон и системный звук перед началом записи.",
            _ => "Устройства готовы. Введите название и начните запись.",
        };
        UpdateRecordingControls(connected, visibleState, devicesReady);
        var showProcessing = _postStopProcessing || _processingErrorVisible || _processingCompleted
            || visibleState is "Finalizing" or "Processing";
        ProcessingPanel.Visibility = showProcessing ? Visibility.Visible : Visibility.Collapsed;
        if (string.Equals(visibleState, "Error", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(error))
        {
            ProcessingErrorText.Text = error;
            ProcessingPanel.Visibility = Visibility.Visible;
        }
    }

    private async Task CheckServerAsync()
    {
        try
        {
            var ready = await _server.CheckReadyAsync();
            ServerStatusText.Text = "Система";
            ServerStatusText.Foreground = (Brush)FindResource("TextBrush");
            ServerStatusIndicator.Fill = ready ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("MutedTextBrush");
            ServerStatusCard.Background = (Brush)FindResource("SurfaceStrongBrush");
            ServerStatusCard.BorderBrush = (Brush)FindResource("BorderBrush");
            AdminStatusText.Text = ready ? $"Локальный API подключён: {_server.BaseAddress}" : "Локальный API недоступен.";
        }
        catch (Exception ex)
        {
            ServerStatusText.Text = "Система";
            ServerStatusText.Foreground = (Brush)FindResource("TextBrush");
            ServerStatusIndicator.Fill = (Brush)FindResource("MutedTextBrush");
            ServerStatusCard.Background = (Brush)FindResource("SurfaceStrongBrush");
            ServerStatusCard.BorderBrush = (Brush)FindResource("BorderBrush");
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private async Task<AgentIpcResponse?> RunCommandAsync(string command, object? payload = null)
    {
        try
        {
            var response = await _agent.SendAsync(command, payload);
            if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Команда отклонена агентом.");
            FooterText.Text = $"Команда {command} выполнена";
            await RefreshStatusAsync();
            return response;
        }
        catch (Exception ex)
        {
            LastErrorText.Text = SafeError(ex);
            _processingErrorVisible = true;
            ProcessingPanel.Visibility = Visibility.Visible;
            ProcessingStatusText.Text = command == "START" ? "Не удалось начать запись" : $"Команда {command} не выполнена";
            ProcessingErrorText.Text = SafeError(ex);
            UpdateRecordingPresentation("Error", false, null, SafeError(ex));
            FooterText.Text = $"Команда {command} завершилась ошибкой";
            return null;
        }
    }

    private async void PreflightButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceCheckInProgress) return;
        _deviceCheckInProgress = true;
        RecordingReadinessText.Text = "Проверяем Recorder Agent и устройства…";
        FooterText.Text = "Выполняется проверка устройств";
        try
        {
            await RefreshStatusAsync();
        }
        finally
        {
            _deviceCheckInProgress = false;
            await RefreshStatusAsync();
        }
    }

    private void OpenSourceButton_Click(object sender, RoutedEventArgs e)
    {
        MainNavigationTabs.SelectedIndex = 2;
        FooterText.Text = "Открыты источники записи";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!StartRecordingButton.IsEnabled)
        {
            RecordingReadinessText.Text = "Сначала подключите Recorder Agent и пройдите проверку устройств.";
            return;
        }
        _processingErrorVisible = false;
        _processingCompleted = false;
        _failedProcessingJobId = null;
        ProcessingRetryButton.Visibility = Visibility.Collapsed;
        ProcessingOpenTranscriptButton.Visibility = Visibility.Collapsed;
        ProcessingProgressBar.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Collapsed;
        RecordingReadinessText.Text = "Запускаем запись…";
        Guid? meetingId = Guid.TryParse(_selectedMeeting?.Id, out var id) ? id : null;
        var title = string.IsNullOrWhiteSpace(RecordingTitleBox.Text) || string.Equals(RecordingTitleBox.Text.Trim(), "Новая запись", StringComparison.OrdinalIgnoreCase)
            ? _selectedMeeting?.Title
            : RecordingTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = $"Совещание {DateTime.Now:dd.MM.yyyy HH:mm}";
        var response = await RunCommandAsync("START", new { meetingId, title });
        if (response?.MeetingId is Guid startedMeetingId)
        {
            ProcessingStatusText.Text = $"Запись: {title} · совещание {startedMeetingId}";
            ProcessingErrorText.Text = response.Error == "server_binding_pending" ? "Сервер временно недоступен; запись продолжается локально." : string.Empty;
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) => await RunCommandAsync("PAUSE");
    private async void ResumeButton_Click(object sender, RoutedEventArgs e) => await RunCommandAsync("RESUME");

    private void MarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MarkRecordingButton.IsEnabled) return;
        ProcessingPanel.Visibility = Visibility.Visible;
        ProcessingStatusText.Text = "Метка добавлена в текущую запись";
        FooterText.Text = "Метка добавлена";
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Подтвердить завершение записи?", "WhisperX Atom", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _postStopProcessing = true;
        _processingErrorVisible = false;
        _processingCompleted = false;
        _processingMeetingId = null;
        _failedProcessingJobId = null;
        ProcessingRetryButton.Visibility = Visibility.Collapsed;
        ProcessingOpenTranscriptButton.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Visible;
        ProcessingProgressBar.Visibility = Visibility.Visible;
        ProcessingProgressBar.Value = 0;
        ProcessingStatusText.Text = "Сохранение записи…";
        ProcessingErrorText.Text = string.Empty;
        var response = await RunCommandAsync("STOP");
        _localArchiveSessionId = response?.SessionId;
        if (response?.MeetingId is Guid meetingId)
        {
            _processingMeetingId = meetingId;
            _ = TrackProcessingAsync(meetingId);
        }
        else
        {
            _postStopProcessing = false;
            _processingErrorVisible = true;
            ProcessingProgressBar.Visibility = Visibility.Collapsed;
            ProcessingStatusText.Text = "Запись сохранена локально";
            ProcessingErrorText.Text = "Серверное совещание ещё не связано; проверьте подключение API.";
            ProcessingRetryButton.Visibility = _localArchiveSessionId is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateRecordingPresentation("Error", true, null, ProcessingErrorText.Text);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();
    private async void VoiceRefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshVoiceStatusAsync();
    private async void VoiceDoctorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _voice.SendAsync("DOCTOR");
            VoiceErrorText.Text = result.Ok ? "Doctor завершён." : result.Error ?? "Doctor завершился с ошибкой.";
            await RefreshVoiceStatusAsync();
        }
        catch (Exception ex) { VoiceErrorText.Text = SafeError(ex); }
    }

    private void VoiceOpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "VoiceHost");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    private static string Metric(double? value) => value is null ? "—" : $"{value:0} мс";

    private async void VoiceEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        try { await _voice.SendAsync("ENABLE", new { enabled = VoiceEnabledCheckBox.IsChecked == true }); await RefreshVoiceStatusAsync(); }
        catch (Exception ex) { VoiceErrorText.Text = SafeError(ex); }
    }
    private async void VoiceQuietCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        try { await _voice.SendAsync("QUIET_MODE", new { enabled = VoiceQuietCheckBox.IsChecked == true }); }
        catch (Exception ex) { VoiceErrorText.Text = SafeError(ex); }
    }
    private async void VoiceSensitivityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceSensitivityBox.SelectedItem is not ComboBoxItem item || item.Content is not string sensitivity) return;
        try { await _voice.SendAsync("SET_SENSITIVITY", new { sensitivity }); }
        catch (Exception ex) { VoiceErrorText.Text = SafeError(ex); }
    }
    private async void VoicePttButton_Down(object sender, MouseButtonEventArgs e)
    {
        if (_voicePttActive) return;
        try
        {
            var response = await _voice.SendAsync("PUSH_TO_TALK_BEGIN");
            _voicePttActive = response.Ok;
            VoiceErrorText.Text = response.Ok ? "Говорите команду, затем отпустите кнопку." : response.Error ?? "Не удалось начать Push-to-Talk";
            if (response.Ok) VoicePttButton.CaptureMouse();
        }
        catch (Exception ex) { VoiceErrorText.Text = SafeError(ex); }
    }

    private async void VoicePttButton_Up(object sender, MouseButtonEventArgs e) => await EndVoicePttAsync();
    private async void VoicePttButton_Leave(object sender, MouseEventArgs e)
    {
        if (_voicePttActive && !VoicePttButton.IsMouseOver) await EndVoicePttAsync();
    }

    private async Task EndVoicePttAsync()
    {
        if (!_voicePttActive) return;
        _voicePttActive = false;
        VoicePttButton.ReleaseMouseCapture();
        try
        {
            var response = await _voice.SendAsync("PUSH_TO_TALK_END");
            VoiceErrorText.Text = response.Ok ? string.Empty : response.Error ?? "Команда отклонена";
            await RefreshVoiceStatusAsync();
        }
        catch (Exception ex) { VoiceErrorText.Text = SafeError(ex); }
    }
    private async void VoiceTextButton_Click(object sender, RoutedEventArgs e) => await SubmitVoiceTextAsync(false);
    private async Task SubmitVoiceTextAsync(bool ptt)
    {
        try
        {
            var response = await _voice.SendAsync(ptt ? "PUSH_TO_TALK" : "TEXT", new { text = VoiceTextBox.Text });
            VoiceErrorText.Text = response.Ok ? string.Empty : response.Error ?? "Команда отклонена";
            await RefreshVoiceStatusAsync();
        }
        catch (Exception ex) { VoiceErrorText.Text = SafeError(ex); }
    }
    private async void VoiceTtsButton_Click(object sender, RoutedEventArgs e)
    {
        try { await _voice.SendAsync("TEST_TTS"); VoiceErrorText.Text = string.Empty; }
        catch (Exception ex) { VoiceErrorText.Text = SafeError(ex); }
    }
    private async void AssistantQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AssistantQuestionBox.Text)) return;
            Guid? meetingId = Guid.TryParse(_selectedMeeting?.Id, out var id) ? id : null;
            var query = await _server.CreateAssistantQueryAsync(AssistantQuestionBox.Text, meetingId);
            if (query is null) { AssistantAnswerText.Text = "Не удалось поставить запрос в очередь. Выберите готовое совещание."; return; }
            AssistantAnswerText.Text = $"Запрос принят: {query.Status}";
            for (var attempt = 0; attempt < 90; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                var current = await _server.GetAssistantQueryAsync(Guid.Parse(query.Id));
                if (current is null) break;
                if (current.Status is "READY" or "FAILED" or "NEEDS_REVIEW")
                {
                    AssistantAnswerText.Text = current.Answer ?? current.ErrorCode ?? "Ответ не сформирован";
                    AssistantEvidenceText.Text = current.Evidence.RootElement.ToString();
                    break;
                }
            }
        }
        catch (Exception ex) { AssistantAnswerText.Text = SafeError(ex); }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _server.Dispose();
            _server = new ServerApiClient(ApiUrlTextBox.Text);
            var ok = await _server.LoginAsync(UsernameTextBox.Text, PasswordBox.Password);
            AdminStatusText.Text = ok ? "Вход в локальный API выполнен." : "Ошибка входа.";
            if (ok)
            {
                AgentRegistrationPanel.IsEnabled = true;
                AgentRegistrationPanel.Opacity = 1.0;
                PasswordBox.Clear();
                DesktopSettings.Save(_server.BaseAddress.ToString(), UsernameTextBox.Text, _server.GetSessionCookie(), _archiveRoot, _microphoneDeviceId, _systemAudioDeviceId);
                await LoadMeetingsAsync();
            }
            else
            {
                AgentRegistrationPanel.IsEnabled = false;
                AgentRegistrationPanel.Opacity = 0.58;
            }
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private async void CheckServerButton_Click(object sender, RoutedEventArgs e) => await CheckServerAsync();

    private void UpdateRecordingControls(bool connected, string state, bool devicesReady)
    {
        var isRecording = string.Equals(state, "Recording", StringComparison.OrdinalIgnoreCase);
        var isPaused = string.Equals(state, "Paused", StringComparison.OrdinalIgnoreCase);
        var isActive = isRecording || isPaused;
        var isIdle = string.Equals(state, "Idle", StringComparison.OrdinalIgnoreCase);
        var isUnavailable = string.Equals(state, "Unavailable", StringComparison.OrdinalIgnoreCase);
        var isReadyForNewRecording = isIdle || string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase);

        StartRecordingButton.Visibility = isReadyForNewRecording ? Visibility.Visible : Visibility.Collapsed;
        CheckDevicesButton.Visibility = isReadyForNewRecording || isUnavailable ? Visibility.Visible : Visibility.Collapsed;
        PauseRecordingButton.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        ResumeRecordingButton.Visibility = isPaused ? Visibility.Visible : Visibility.Collapsed;
        MarkRecordingButton.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        StopRecordingButton.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        SourceActionButton.Visibility = isReadyForNewRecording || isUnavailable ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneDeviceBox.IsEnabled = connected && isReadyForNewRecording;
        SystemAudioDeviceBox.IsEnabled = connected && isReadyForNewRecording;

        StartRecordingButton.IsEnabled = connected && isReadyForNewRecording && devicesReady;
        StartRecordingButton.ToolTip = !connected
            ? "Recorder Agent недоступен"
            : !devicesReady ? "Подключите микрофон и системный звук" : "Начать запись";
        CheckDevicesButton.Content = connected ? "Проверить устройства" : "Повторить проверку";
        PauseRecordingButton.IsEnabled = connected && isRecording;
        ResumeRecordingButton.IsEnabled = connected && isPaused;
        StopRecordingButton.IsEnabled = connected && isActive;
        MarkRecordingButton.IsEnabled = connected && isActive;
        RecordingActionHintText.Text = state switch
        {
            "Recording" => "Пауза, метка или завершение записи",
            "Paused" => "Продолжите запись или завершите её",
            "Checking" => "Ждём результат проверки",
            "Finalizing" or "Processing" => "Действия временно недоступны",
            "Error" => "Можно начать новую запись после проверки устройств",
            "Unavailable" => "Подключите Recorder Agent",
            _ when !devicesReady => "Подключите устройства для старта",
            _ => "Готово к следующему действию",
        };
        AgentStatusIndicator.Fill = connected ? Brushes.ForestGreen : Brushes.OrangeRed;
    }

    private async void DockerStatusButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = await _server.GetSystemStatusAsync();
            AdminStatusText.Text = status is null
                ? "Статус сервера недоступен. Выполните вход в API."
                : $"API: {(status.Ready ? "готов" : "ошибка")}; PostgreSQL: {(status.Postgres ? "подключён" : "недоступен")}; диск: {FormatBytes(status.FreeBytes)} свободно из {FormatBytes(status.TotalBytes)}";
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }
    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Environment.GetEnvironmentVariable("WHISPERX_DATA_HOST") ?? @"C:\WhisperXAtom\Data";
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenAgentSpoolButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private async Task SyncArchiveRootAsync(bool agentAvailable)
    {
        if (!agentAvailable || string.Equals(_lastSyncedArchiveRoot, _archiveRoot, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var response = await _agent.SendAsync("SET_ARCHIVE_ROOT", new { archiveRoot = _archiveRoot });
            if (response.Ok)
            {
                _lastSyncedArchiveRoot = _archiveRoot;
                ArchiveStatusText.Text = "Папка архива синхронизирована с Recorder Agent";
            }
        }
        catch
        {
            ArchiveStatusText.Text = "Путь сохранён в Desktop; Agent синхронизируется после подключения";
        }
    }

    private async void SelectArchiveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Выберите папку архива записей",
                InitialDirectory = Directory.Exists(_archiveRoot) ? _archiveRoot : DesktopSettings.DefaultArchiveRoot()
            };
            if (dialog.ShowDialog(this) != true) return;
            Directory.CreateDirectory(dialog.FolderName);
            _archiveRoot = Path.GetFullPath(dialog.FolderName);
            ArchiveRootTextBox.Text = _archiveRoot;
            DesktopSettings.Save(_server.BaseAddress.ToString(), UsernameTextBox.Text, _server.GetSessionCookie(), _archiveRoot, _microphoneDeviceId, _systemAudioDeviceId);
            ArchiveStatusText.Text = "Путь сохранён. Проверяю Recorder Agent…";
            await SyncArchiveRootAsync(true);
        }
        catch (Exception ex)
        {
            ArchiveStatusText.Text = $"Не удалось выбрать папку: {SafeError(ex)}";
        }
    }

    private void OpenArchiveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_archiveRoot);
            Process.Start(new ProcessStartInfo { FileName = _archiveRoot, UseShellExecute = true });
        }
        catch (Exception ex) { ArchiveStatusText.Text = $"Не удалось открыть папку: {SafeError(ex)}"; }
    }

    private async void EnrollAgentButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await _server.CheckReadyAsync())
            {
                AdminStatusText.Text = "Сначала подключите локальный API.";
                return;
            }
            if (string.IsNullOrWhiteSpace(AgentNameBox.Text) || string.IsNullOrWhiteSpace(EnrollmentSecretBox.Password))
            {
                AdminStatusText.Text = "Укажите имя агента и enrollment secret.";
                return;
            }
            var enrollment = await _server.EnrollAgentAsync(AgentNameBox.Text.Trim(), EnrollmentSecretBox.Password);
            var configured = await _agent.SendAsync("CONFIGURE", new
            {
                serverUrl = _server.BaseAddress.ToString().TrimEnd('/'),
                agentId = enrollment.AgentId,
                token = enrollment.Token,
                archiveRoot = _archiveRoot,
                microphoneDeviceId = _microphoneDeviceId,
                systemAudioDeviceId = _systemAudioDeviceId,
            });
            AdminStatusText.Text = configured.Ok
                ? "Recorder Service зарегистрирован и настроен в API."
                : $"Ошибка регистрации, но команда IPC не выполнена: {configured.Error}";
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private async void LoadMeetingsButton_Click(object sender, RoutedEventArgs e) => await LoadMeetingsAsync();

    private void NewMeetingQuickAction_Click(object sender, RoutedEventArgs e)
    {
        MainNavigationTabs.SelectedIndex = 0;
        RecordingTitleBox.Focus();
        RecordingTitleBox.SelectAll();
        FooterText.Text = "Введите название и запустите новую запись";
    }

    private void OpenSettingsQuickAction_Click(object sender, RoutedEventArgs e)
    {
        MainNavigationTabs.SelectedIndex = 5;
        FooterText.Text = "Открыты настройки подключений";
    }

    private void OpenSourcesFromHero_Click(object sender, MouseButtonEventArgs e)
    {
        MainNavigationTabs.SelectedIndex = 2;
        FooterText.Text = "Открыты источники записи";
    }

    private void GlobalSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (string.Equals(GlobalSearchBox.Text, GlobalSearchPlaceholder, StringComparison.Ordinal))
        {
            GlobalSearchBox.Clear();
            GlobalSearchBox.Foreground = (Brush)FindResource("TextBrush");
        }
    }

    private void GlobalSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(GlobalSearchBox.Text)) return;
        GlobalSearchBox.Text = GlobalSearchPlaceholder;
        GlobalSearchBox.Foreground = (Brush)FindResource("MutedTextBrush");
    }

    private void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MeetingSearchBox is null || string.Equals(GlobalSearchBox.Text, GlobalSearchPlaceholder, StringComparison.Ordinal)) return;
        if (!string.Equals(MeetingSearchBox.Text, GlobalSearchBox.Text, StringComparison.CurrentCulture))
            MeetingSearchBox.Text = GlobalSearchBox.Text;
    }

    private void OpenMeetingsLink_Click(object sender, RoutedEventArgs e)
    {
        MainNavigationTabs.SelectedIndex = 1;
        MeetingsList.Focus();
        FooterText.Text = "Открыт реестр совещаний";
    }

    private void DashboardMeetingRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopMeeting meeting }) return;
        MainNavigationTabs.SelectedIndex = 1;
        MeetingsList.SelectedItem = MeetingsList.Items.OfType<DesktopMeeting>().FirstOrDefault(item => item.Id == meeting.Id);
        FooterText.Text = $"Открыто совещание: {meeting.Title}";
    }

    private async void ImportFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импортировать аудио или видео",
            Filter = "Медиафайлы|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.ogg;*.opus;*.mp4;*.mkv;*.mov;*.webm;*.avi|Все файлы|*.*",
            Multiselect = false,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ImportStatusText.Text = $"Загрузка: {Path.GetFileName(dialog.FileName)}";
            ImportStatusText.Foreground = Brushes.LightSkyBlue;
            ImportButton.IsEnabled = false;
            var progress = new Progress<DesktopImportProgress>(state =>
                ImportStatusText.Text = state.Stage == "UPLOADING"
                    ? $"Файл загружен: {FormatBytes(state.UploadedBytes)} из {FormatBytes(state.TotalBytes)}"
                    : state.Message ?? "Подготавливаем импорт…");
            var meeting = await _server.ImportFileAsync(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), progress);
            ImportStatusText.Text = "Файл принят. Запущена обработка.";
            ImportStatusText.Foreground = Brushes.LightGreen;
            await LoadMeetingsAsync();
            var item = MeetingsList.Items.OfType<DesktopMeeting>().FirstOrDefault(x => x.Id == meeting.Id);
            if (item is not null)
            {
                MeetingsList.SelectedItem = item;
                _ = TrackProcessingAsync(Guid.Parse(item.Id));
            }
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = SafeError(ex);
            ImportStatusText.Foreground = Brushes.OrangeRed;
        }
        finally { ImportButton.IsEnabled = true; }
    }

    private async void CreateMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = NewMeetingTitleBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(title) || title == "Название совещания")
            {
                AdminStatusText.Text = "Укажите название совещания.";
                return;
            }
            var meeting = await _server.CreateMeetingAsync(title);
            await LoadMeetingsAsync();
            foreach (var item in MeetingsList.Items.OfType<DesktopMeeting>())
            {
                if (item.Id == meeting.Id) { MeetingsList.SelectedItem = item; break; }
            }
            FooterText.Text = "Совещание создано; его можно связать с записью.";
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private async Task LoadMeetingsAsync()
    {
        DashboardMeetingsList.ItemsSource = Array.Empty<DesktopMeeting>();
        DashboardMeetingsEmptyText.Text = "Загрузка совещаний…";
        DashboardMeetingsEmptyHintText.Text = "Получаем данные из локального API…";
        DashboardMeetingsEmptyActions.Visibility = Visibility.Collapsed;
        DashboardMeetingsEmptyText.Visibility = Visibility.Visible;
        DashboardMeetingsEmptyState.Visibility = Visibility.Visible;
        try
        {
            var meetings = await _server.GetMeetingsAsync();
            _meetingsView = CollectionViewSource.GetDefaultView(meetings);
            _meetingsView.Filter = FilterMeeting;
            MeetingsList.ItemsSource = _meetingsView;
            MeetingCountText.Text = $"Совещаний: {meetings.Count}";
            await RefreshDashboardMetricsAsync(meetings);
            FooterText.Text = $"Совещаний загружено: {meetings.Count}";
        }
        catch (Exception ex)
        {
            ResetDashboard("Не удалось загрузить совещания");
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private bool FilterMeeting(object item)
    {
        if (item is not DesktopMeeting meeting) return false;
        var query = MeetingSearchBox.Text.Trim();
        return string.IsNullOrWhiteSpace(query)
            || meeting.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || meeting.Status.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void MeetingSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _meetingsView?.Refresh();
        if (_meetingsView is not null) MeetingCountText.Text = $"Показано: {MeetingsList.Items.Count}";
    }
    private async void MeetingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MeetingsList.SelectedItem is not DesktopMeeting meeting) return;
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = new CancellationTokenSource();
        _selectedMeeting = meeting;
        RecordingTitleBox.Text = meeting.Title;
        SelectedMeetingTitle.Text = meeting.Title;
        SelectedMeetingStatus.Text = $"{meeting.Status} · {meeting.CreatedAt.ToLocalTime():g}";
        await LoadMeetingDetailsAsync(meeting, _detailsCts.Token);
    }

    private async Task LoadMeetingDetailsAsync(DesktopMeeting meeting, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(meeting.Id, out var meetingId)) return;
        await UpdateLocalArchiveStatusAsync(meeting, cancellationToken);
        TranscriptStatusText.Text = "Загрузка результатов…";
        SummaryTextBox.Text = "Загрузка саммари…";
        try
        {
            var transcript = await _server.GetTranscriptAsync(meetingId, cancellationToken);
            TranscriptList.ItemsSource = transcript?.Segments ?? [];
            TranscriptStatusText.Text = transcript is null ? "Стенограмма пока не готова." : $"Статус: {transcript.Status}; сегментов: {transcript.Segments.Count}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { TranscriptStatusText.Text = $"Стенограмма: {SafeError(ex)}"; TranscriptList.ItemsSource = Array.Empty<DesktopTranscriptSegment>(); }

        try
        {
            _speakers = await _server.GetSpeakersAsync(meetingId, cancellationToken);
            SpeakerList.ItemsSource = _speakers;
            MergeSourceBox.ItemsSource = _speakers;
            MergeTargetBox.ItemsSource = _speakers;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { SpeakerList.ItemsSource = Array.Empty<DesktopSpeaker>(); FooterText.Text = $"Спикеры: {SafeError(ex)}"; }

        try { SummaryTextBox.Text = FormatSummary(await _server.GetSummaryAsync(meetingId, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { SummaryTextBox.Text = $"Саммари: {SafeError(ex)}"; }
        try { DecisionList.ItemsSource = await _server.GetDecisionsAsync(meetingId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { DecisionList.ItemsSource = Array.Empty<DesktopDecision>(); FooterText.Text = $"Решения: {SafeError(ex)}"; }
        try { TaskList.ItemsSource = await _server.GetTasksAsync(meetingId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { TaskList.ItemsSource = Array.Empty<DesktopTask>(); FooterText.Text = $"Поручения: {SafeError(ex)}"; }

        try
        {
            var media = await _server.GetMediaAsync(meetingId, cancellationToken);
            var preview = media.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PreviewStorageKey));
            if (preview is not null && Guid.TryParse(preview.Id, out var mediaId))
            {
                var path = await _server.DownloadPreviewAsync(mediaId, cancellationToken);
                if (path is not null)
                {
                    PreviewPlayer.Source = new Uri(path, UriKind.Absolute);
                    PlayerStatusText.Text = $"Файл: {preview.OriginalName}";
                }
                else PlayerStatusText.Text = "Preview недоступен.";
            }
            else PlayerStatusText.Text = "Медиафайл для воспроизведения не найден.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { PlayerStatusText.Text = $"Плеер: {SafeError(ex)}"; }
        FooterText.Text = "Данные совещания загружены";
    }
    private async Task UpdateLocalArchiveStatusAsync(DesktopMeeting meeting, CancellationToken cancellationToken)
    {
        _localArchiveDirectory = null;
        _localArchiveSessionId = null;
        var meetingsRoot = Path.Combine(_archiveRoot, "Meetings");
        if (!Directory.Exists(meetingsRoot))
        {
            LocalArchiveStatusText.Text = "Локальная копия ещё не создана";
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(meetingsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
                var root = document.RootElement;
                var manifestMeetingId = root.TryGetProperty("meetingId", out var meetingValue) && meetingValue.ValueKind == JsonValueKind.String ? meetingValue.GetString() : null;
                var manifestTitle = root.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
                if (!string.Equals(manifestMeetingId, meeting.Id, StringComparison.OrdinalIgnoreCase) && !string.Equals(manifestTitle, meeting.Title, StringComparison.CurrentCultureIgnoreCase)) continue;
                _localArchiveDirectory = directory;
                _localArchiveSessionId = root.TryGetProperty("sessionId", out var sessionValue) && sessionValue.ValueKind == JsonValueKind.String ? sessionValue.GetString() : null;
                var uploadState = root.TryGetProperty("uploadState", out var stateValue) && stateValue.ValueKind == JsonValueKind.String ? stateValue.GetString() : "UNKNOWN";
                var fileCount = root.TryGetProperty("files", out var filesValue) && filesValue.ValueKind == JsonValueKind.Array ? filesValue.GetArrayLength() : 0;
                LocalArchiveStatusText.Text = $"Локальная копия: {uploadState}; файлов: {fileCount}; {directory}";
                return;
            }
            catch (JsonException) { }
        }
        LocalArchiveStatusText.Text = "Локальная копия для этого совещания не найдена";
    }

    private void TranscriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptList.SelectedItem is not DesktopTranscriptSegment segment) return;
        if (PreviewPlayer.NaturalDuration.HasTimeSpan)
            PreviewPlayer.Position = TimeSpan.FromMilliseconds(segment.StartMs);
        PreviewPlayer.Play();
        PlayerStatusText.Text = $"Переход к {segment.TimeLabel} · {segment.Speaker ?? "Спикер N"}";
    }

    private void PreviewPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e) => PlayerStatusText.Text = "Не удалось воспроизвести preview. Проверьте кодеки и файл.";

    private void PlayButton_Click(object sender, RoutedEventArgs e) => PreviewPlayer.Play();
    private void PlayerPauseButton_Click(object sender, RoutedEventArgs e) => PreviewPlayer.Pause();
    private void PlayerStopButton_Click(object sender, RoutedEventArgs e) => PreviewPlayer.Stop();

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || !PreviewPlayer.NaturalDuration.HasTimeSpan) return;
        PreviewPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void UpdatePlayerPosition()
    {
        if (!PreviewPlayer.NaturalDuration.HasTimeSpan) return;
        _updatingSlider = true;
        PositionSlider.Maximum = PreviewPlayer.NaturalDuration.TimeSpan.TotalSeconds;
        PositionSlider.Value = Math.Min(PreviewPlayer.Position.TotalSeconds, PositionSlider.Maximum);
        _updatingSlider = false;
    }

    private void SpeakerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeakerList.SelectedItem is DesktopSpeaker speaker) SpeakerNameBox.Text = speaker.DisplayName;
    }

    private async void RenameSpeakerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || SpeakerList.SelectedItem is not DesktopSpeaker speaker || string.IsNullOrWhiteSpace(SpeakerNameBox.Text))
        { FooterText.Text = "Выберите спикера и укажите имя."; return; }
        if (!Guid.TryParse(_selectedMeeting.Id, out var meetingId) || !Guid.TryParse(speaker.Id, out var speakerId)) return;
        try
        {
            if (!await _server.RenameSpeakerAsync(meetingId, speakerId, SpeakerNameBox.Text.Trim())) throw new InvalidOperationException("Сервер отклонил переименование.");
            await LoadMeetingDetailsAsync(_selectedMeeting);
            FooterText.Text = "Имя спикера сохранено.";
        }
        catch (Exception ex) { FooterText.Text = $"Переименование: {SafeError(ex)}"; }
    }

    private async void MergeSpeakersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || MergeSourceBox.SelectedItem is not DesktopSpeaker source || MergeTargetBox.SelectedItem is not DesktopSpeaker target || source.Id == target.Id)
        { FooterText.Text = "Выберите разных исходного и целевого спикеров."; return; }
        if (!Guid.TryParse(_selectedMeeting.Id, out var meetingId) || !Guid.TryParse(source.Id, out var sourceId) || !Guid.TryParse(target.Id, out var targetId)) return;
        try
        {
            if (!await _server.MergeSpeakersAsync(meetingId, sourceId, targetId)) throw new InvalidOperationException("Сервер отклонил объединение.");
            await LoadMeetingDetailsAsync(_selectedMeeting);
            FooterText.Text = "Спикеры объединены.";
        }
        catch (Exception ex) { FooterText.Text = $"Объединение: {SafeError(ex)}"; }
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is not DesktopTask task) return;
        _selectedTask = task;
        TaskEditBox.Text = task.Task;
        TaskResponsibleBox.Text = task.Responsible ?? string.Empty;
        TaskDeadlineBox.Text = task.Deadline?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
        TaskStatusBox.SelectedItem = TaskStatusBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), task.Status, StringComparison.OrdinalIgnoreCase));
    }

    private async void SaveTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTask is null || string.IsNullOrWhiteSpace(TaskEditBox.Text)) { FooterText.Text = "Выберите поручение и заполните текст."; return; }
        var status = TaskStatusBox.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? _selectedTask.Status : _selectedTask.Status;
        DateTime? deadline = null;
        if (!string.IsNullOrWhiteSpace(TaskDeadlineBox.Text))
        {
            if (!DateTime.TryParseExact(TaskDeadlineBox.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDeadline))
            { FooterText.Text = "Срок должен быть в формате ГГГГ-ММ-ДД ЧЧ:ММ."; return; }
            deadline = DateTime.SpecifyKind(parsedDeadline, DateTimeKind.Local).ToUniversalTime();
        }
        try
        {
            var updated = _selectedTask with { Task = TaskEditBox.Text.Trim(), Responsible = string.IsNullOrWhiteSpace(TaskResponsibleBox.Text) ? null : TaskResponsibleBox.Text.Trim(), Deadline = deadline, Status = status };
            if (!await _server.UpdateTaskAsync(updated)) throw new InvalidOperationException("Сервер отклонил изменение поручения.");
            if (_selectedMeeting is not null) await LoadMeetingDetailsAsync(_selectedMeeting);
            FooterText.Text = "Поручение сохранено.";
        }
        catch (Exception ex) { FooterText.Text = $"Сохранение поручения: {SafeError(ex)}"; }
    }

    private async void RebuildSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || !Guid.TryParse(_selectedMeeting.Id, out var meetingId)) return;
        var accepted = await _server.RebuildSummaryAsync(meetingId);
        FooterText.Text = accepted ? "Запрос на пересборку саммари принят" : "Не удалось запустить пересборку саммари";
        if (accepted) _ = TrackProcessingAsync(meetingId);
    }

    private static string FormatSummary(DesktopSummary? summary)
    {
        if (summary is null) return "Саммари пока не готово.";
        var root = summary.Content.RootElement;
        if (root.TryGetProperty("summary", out var text) && text.ValueKind == JsonValueKind.String)
            return $"Версия {summary.Version}; модель: {summary.ModelName}\n\n{text.GetString()}";
        return $"Версия {summary.Version}; модель: {summary.ModelName}\n\n{root}";
    }

    private static string SafeError(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Contains("recording_access_denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("recording_archive_access_denied", StringComparison.OrdinalIgnoreCase))
            return "Windows Defender заблокировал доступ к папке архива. Выберите папку вне Documents или разрешите только WhisperX Atom Recorder Service в Controlled Folder Access.";
        if (message.Contains("recording_archive_path_unavailable", StringComparison.OrdinalIgnoreCase))
            return "Папка архива недоступна. Выберите другую папку в Настройки → Папка архива записей и повторите запуск.";
        if (message.Contains("recording_storage_unavailable", StringComparison.OrdinalIgnoreCase))
            return "Недоступно локальное хранилище записи. Проверьте диск и права доступа.";
        if (ex is HttpRequestException or IOException or TimeoutException)
            return "Локальный сервис недоступен или не ответил вовремя.";
        if (ex is UnauthorizedAccessException)
            return "Недостаточно прав для выполнения операции.";
        if (ex is InvalidOperationException invalidOperation && invalidOperation.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
            return "Recorder Service ещё не настроен.";
        return "Операция не выполнена. Откройте вкладку «Администрирование» для диагностики.";
    }

    private async void RetryProcessingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_failedProcessingJobId is not Guid jobId)
        {
            if (string.IsNullOrWhiteSpace(_localArchiveSessionId)) return;
            try
            {
                var retry = await _agent.SendAsync("RETRY_UPLOAD", new { sessionId = _localArchiveSessionId });
                ProcessingStatusText.Text = retry.Ok ? "Повторная отправка локального архива запущена" : "Не удалось повторить отправку локального архива";
                ProcessingErrorText.Text = retry.Error ?? string.Empty;
                ProcessingRetryButton.Visibility = retry.Ok ? Visibility.Collapsed : Visibility.Visible;
                await RefreshStatusAsync();
            }
            catch (Exception ex) { ProcessingErrorText.Text = SafeError(ex); }
            return;
        }
        try
        {
            var retried = await _server.RetryJobAsync(jobId);
            if (retried is null)
            {
                ProcessingErrorText.Text = "Не удалось поставить обработку на повтор. Проверьте подключение к API.";
                return;
            }
            if (!Guid.TryParse(retried.MeetingId, out var meetingId))
            {
                ProcessingErrorText.Text = "API вернул некорректное совещание для повторной обработки.";
                return;
            }
            _failedProcessingJobId = null;
            _processingErrorVisible = false;
            _processingCompleted = false;
            _postStopProcessing = true;
            ProcessingRetryButton.Visibility = Visibility.Collapsed;
            ProcessingOpenTranscriptButton.Visibility = Visibility.Collapsed;
            ProcessingProgressBar.Visibility = Visibility.Visible;
            ProcessingProgressBar.Value = 0;
            ProcessingStatusText.Text = "Повторная обработка запущена…";
            ProcessingErrorText.Text = string.Empty;
            _ = TrackProcessingAsync(meetingId);
        }
        catch (Exception ex)
        {
            ProcessingErrorText.Text = SafeError(ex);
        }
    }

    private void OpenProcessedTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_processingMeetingId is not Guid meetingId) return;
        MainNavigationTabs.SelectedIndex = 1;
        var selected = MeetingsList.Items.OfType<DesktopMeeting>()
            .FirstOrDefault(item => item.Id.Equals(meetingId.ToString(), StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            MeetingsList.SelectedItem = selected;
            MeetingDetailsTabs.SelectedIndex = 1;
        }
        FooterText.Text = "Открыта стенограмма завершённой записи";
    }

    private async Task TrackProcessingAsync(Guid meetingId)
    {
        _processingPollCts?.Cancel();
        _processingPollCts?.Dispose();
        _processingPollCts = new CancellationTokenSource(TimeSpan.FromHours(4));
        var cancellationToken = _processingPollCts.Token;
        _processingMeetingId = meetingId;
        _postStopProcessing = true;
        _processingErrorVisible = false;
        _processingCompleted = false;
        _failedProcessingJobId = null;
        ProcessingRetryButton.Visibility = Visibility.Collapsed;
        ProcessingOpenTranscriptButton.Visibility = Visibility.Collapsed;
        ProcessingProgressBar.Visibility = Visibility.Visible;
        ProcessingPanel.Visibility = Visibility.Visible;
        ProcessingProgressBar.Value = 0;
        ProcessingErrorText.Text = string.Empty;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var jobs = await _server.GetJobsAsync(meetingId, cancellationToken);
                var failed = jobs.FirstOrDefault(job => string.Equals(job.Status, "FAILED", StringComparison.OrdinalIgnoreCase));
                if (failed is not null)
                {
                    _failedProcessingJobId = Guid.TryParse(failed.Id, out var failedJobId) ? failedJobId : null;
                    _postStopProcessing = false;
                    _processingErrorVisible = true;
                    ProcessingProgressBar.Visibility = Visibility.Collapsed;
                    ProcessingRetryButton.Visibility = _failedProcessingJobId is not null ? Visibility.Visible : Visibility.Collapsed;
                    ProcessingStatusText.Text = "Обработка завершилась с ошибкой";
                    ProcessingErrorText.Text = string.IsNullOrWhiteSpace(failed.Error)
                        ? "Повторите обработку после проверки подключения к API."
                        : failed.Error;
                    UpdateRecordingPresentation("Error", true, _lastAgentHealth, "Обработка записи завершилась с ошибкой.");
                    break;
                }
                var active = jobs.FirstOrDefault(job => !IsTerminal(job.Status));
                if (active is not null)
                {
                    ProcessingProgressBar.Value = Math.Clamp(active.Progress, 0, 100);
                    ProcessingStatusText.Text = $"Обработка: {StageLabel(active.Stage)} · {active.Progress}%";
                }
                else
                {
                    active = jobs.FirstOrDefault();
                }

                var summaryReady = active is null && jobs.Any(job => job.Type == "SUMMARIZE" && job.Status == "READY");
                var transcriptReady = jobs.Any(job => job.Type == "TRANSCRIBE" && job.Status == "READY");
                if (summaryReady)
                {
                    ProcessingProgressBar.Value = 100;
                    ProcessingStatusText.Text = "Готово: стенограмма и саммари сохранены";
                    ProcessingErrorText.Text = string.Empty;
                    _processingCompleted = true;
                    break;
                }
                if (active is null && transcriptReady && jobs.All(job => job.Type != "SUMMARIZE" || IsTerminal(job.Status)))
                {
                    ProcessingProgressBar.Value = 100;
                    ProcessingStatusText.Text = "Стенограмма готова; саммари можно пересобрать";
                    _processingCompleted = true;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            var meetings = await _server.GetMeetingsAsync(cancellationToken);
            _meetingsView = CollectionViewSource.GetDefaultView(meetings);
            _meetingsView.Filter = FilterMeeting;
            MeetingsList.ItemsSource = _meetingsView;
            MeetingCountText.Text = $"Совещаний: {meetings.Count}";
            await RefreshDashboardMetricsAsync(meetings, cancellationToken);
            var selected = meetings.FirstOrDefault(item => item.Id.Equals(meetingId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                MeetingsList.SelectedItem = selected;
                _selectedMeeting = selected;
            }
            if (_processingCompleted)
            {
                _postStopProcessing = false;
                ProcessingOpenTranscriptButton.Visibility = Visibility.Visible;
                ProcessingProgressBar.Visibility = Visibility.Collapsed;
                ProcessingStatusText.Text = "Готово. Стенограмма и результаты обновлены.";
                await RefreshStatusAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _postStopProcessing = false;
            _processingErrorVisible = true;
            ProcessingProgressBar.Visibility = Visibility.Collapsed;
            ProcessingErrorText.Text = SafeError(ex);
            ProcessingStatusText.Text = "Не удалось получить статус обработки";
        }
        finally
        {
            if (!_processingCompleted && !_processingErrorVisible)
                ProcessingProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsTerminal(string status) => status is "READY" or "FAILED" or "CANCELLED";

    private static string StageLabel(string stage) => stage switch
    {
        "INGEST" or "INGESTING" => "подготовка медиа",
        "NORMALIZING" => "нормализация аудио",
        "TRANSCRIBING" => "транскрибация",
        "ALIGNING" => "выравнивание слов",
        "DIARIZING" => "диаризация",
        "QUALITY_CHECK" => "проверка качества",
        "TRANSCRIPT_READY" => "стенограмма сохранена",
        "SUMMARIZING" => "формирование саммари",
        "READY" => "готово",
        "FAILED" => "ошибка",
        _ => stage,
    };

    private static string FormatBytes(long value)
    {
        if (value <= 0) return "неизвестно";
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var size = (double)value;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.0} {units[index]}";
    }

    private static string FormatMediaTime(long? mediaTimeMs)
    {
        if (mediaTimeMs is null or < 0) return "—";
        return TimeSpan.FromMilliseconds(mediaTimeMs.Value).ToString("hh\\:mm\\:ss");
    }
}

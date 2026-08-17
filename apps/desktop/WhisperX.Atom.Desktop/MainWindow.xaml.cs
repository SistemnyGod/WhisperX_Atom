using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;
using WhisperX_Atom_Desktop.Pages;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop;

public sealed partial class MainWindow : Window
{
    private readonly FrontendServices _services;
    private readonly DispatcherQueue _uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly CancellationTokenSource _statusCts = new();
    private Task? _statusTask;
    private bool _suppressNavigation;
    private int _agentRecoveryRunning;
    private DateTimeOffset _nextAgentRecoveryAtUtc = DateTimeOffset.MinValue;
    private AgentIpcResponse? _lastRecorderResponse;
    private DispatcherQueueTimer? _globalRecordingTimer;
    private long _globalRecordingMediaTimeMs;
    private DateTimeOffset _globalRecordingSampleAtUtc;
    private bool _globalRecordingPaused;

    public MainWindow(FrontendServices services)
    {
        InitializeComponent();
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch
        {
            // Custom title-bar APIs are optional on older Windows 10 builds.
            // The standard title bar remains usable when they are unavailable.
        }
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        _services = services;
        _globalRecordingTimer = _uiDispatcherQueue.CreateTimer();
        _globalRecordingTimer.Interval = TimeSpan.FromSeconds(1);
        _globalRecordingTimer.Tick += GlobalRecordingTimer_Tick;
        var settings = services.Settings.Load();
        PageTitleText.Text = "Главная";
        HomeNavItem.Content = "Главная";
        RecordingNavItem.Content = "Запись";
        MeetingsNavItem.Content = "Совещания";
        SeriesNavItem.Content = "Серии оперативок";
        TranscriptsNavItem.Content = "Стенограммы";
        SpeakersNavItem.Content = "Спикеры";
        SummariesNavItem.Content = "Саммари";
        TasksNavItem.Content = "Задачи";
        SearchNavItem.Content = "Поиск";
        AnalyticsNavItem.Content = "Аналитика";
        AssistantNavItem.Content = "ИИ-помощник";
        AgentsNavItem.Content = "Состояние системы";
        AdministrationNavItem.Content = "Администрирование";
        SettingsNavItem.Content = "Настройки";
        SystemStatusText.Text = "Система";
        ProfileText.Text = string.IsNullOrWhiteSpace(settings.Username) ? "Локальная сессия" : settings.Username;
        Closed += MainWindow_Closed;
        Closed += (_, _) => App.WriteStartupLog("MAIN_WINDOW_CLOSED", null);
    }

    public void StartBackgroundPolling()
    {
        if (_statusTask is null || _statusTask.IsCompleted)
            _statusTask = PollSystemStatusAsync(_statusCts.Token);
    }

    public void NavigateTo(string route, object? payload = null)
    {
        var item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), route, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            _suppressNavigation = true;
            NavView.SelectedItem = item;
            _suppressNavigation = false;
        }
        NavigateToPage(route, payload);
    }

    public IntPtr GetWindowHandle() => WindowNative.GetWindowHandle(this);

    private void NavigateToPage(string route, object? payload = null)
    {
        var normalizedRoute = route.ToLowerInvariant();
        PageTitleText.Text = normalizedRoute switch
        {
            "recording" => "Запись",
            "meetings" => "Совещания",
            "transcripts" => "Стенограммы",
            "summaries" => "Саммари",
            "tasks" => "Задачи",
            "speakers" => "Спикеры",
            "agents" => "Состояние системы",
            "settings" => "Настройки",
            _ => "Главная"
        };
        object parameter = normalizedRoute == "meetings" && payload is MeetingNavigationTarget target
            ? new MeetingNavigationRequest(_services, target)
            : normalizedRoute switch
            {
                "series" => new ComingSoonNavigationRequest(_services, "Серии оперативок", "Группируйте повторяющиеся оперативки и отслеживайте их историю."),
                "search" => new ComingSoonNavigationRequest(_services, "Поиск", "Единый поиск по совещаниям, стенограммам, спикерам и задачам."),
                "analytics" => new ComingSoonNavigationRequest(_services, "Аналитика", "Сводные показатели по записям, обработке и качеству стенограмм."),
                "administration" => new ComingSoonNavigationRequest(_services, "Администрирование", "Управление пользователями, ролями и политиками доступа."),
                _ => _services
            };
        NavFrame.Navigate(normalizedRoute switch
        {
            "recording" => typeof(RecordingPage),
            "meetings" => typeof(MeetingsPage),
            "series" or "search" or "analytics" or "administration" => typeof(ComingSoonPage),
            "assistant" => typeof(AssistantPage),
            "transcripts" => typeof(TranscriptsPage),
            "speakers" => typeof(SpeakersPage),
            "summaries" => typeof(SummariesPage),
            "tasks" => typeof(TasksPage),
            "agents" => typeof(AgentsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        }, parameter);
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack) NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigation || args.SelectedItem is not NavigationViewItem item) return;
        NavigateToPage(item.Tag?.ToString() ?? "home");
    }

    private async Task PollSystemStatusAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await RefreshSystemStatusAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                SetSystemStatus("Сервисы недоступны", "DangerBrush");
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task RefreshSystemStatusAsync(CancellationToken cancellationToken)
    {
        SetSystemStatus("Проверка системы", "NeutralStatusBrush");
        var backendTask = _services.Backend.CheckReadyAsync(cancellationToken);
        var versionTask = _services.Backend.GetSystemVersionAsync(cancellationToken);
        var processingTask = _services.Backend.GetProcessingReadinessAsync(cancellationToken);
        var recorderTask = _services.Recorder.GetHealthAsync(cancellationToken);
        var voiceTask = Task.Run(async () =>
        {
            try { return await new WhisperX.Atom.Desktop.VoiceHostClient().GetStatusAsync(cancellationToken); }
            catch { return null; }
        }, cancellationToken);

        var backendAvailable = false;
        DesktopSystemVersion? serverVersion = null;
        var recorderAvailable = false;
        AgentIpcResponse? recorderResponse = null;
        AgentIpcHealth? recorderHealth = null;
        DesktopProcessingReadiness? processingReadiness = null;
        try { backendAvailable = await backendTask; } catch (OperationCanceledException) { throw; } catch { }
        try { serverVersion = await versionTask; } catch (OperationCanceledException) { throw; } catch { }
        try { processingReadiness = await processingTask; } catch (OperationCanceledException) { throw; } catch { }
        try
        {
            recorderResponse = await recorderTask;
            recorderAvailable = recorderResponse.IsReachable;
            recorderHealth = recorderResponse.Health;
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        var voice = await voiceTask;
        SetVoiceStatus(voice);
        var authenticated = backendAvailable && await _services.Backend.EnsureAuthenticatedAsync(cancellationToken);
        if (authenticated && recorderAvailable)
            QueueAgentRecovery(cancellationToken);
        if (serverVersion is not null && Math.Abs((serverVersion.ServerTimeUtc - DateTimeOffset.UtcNow).TotalMinutes) > 5)
        {
            SetSystemStatus("Время ПК отличается от времени сервера более чем на 5 минут", "WarningBrush");
            return;
        }
        var agentReady = authenticated && recorderAvailable && _services.AgentBootstrap.IsReady;
        var processingReady = processingReadiness?.Ready == true;
        var status = backendAvailable && !authenticated ? ("Требуется вход", "WarningBrush") :
            backendAvailable && recorderAvailable && !agentReady ? ("Recorder доступен; требуется привязка к пользователю", "WarningBrush") :
            backendAvailable && agentReady && processingReadiness is null ? ("WhisperX: readiness недоступна", "WarningBrush") :
            backendAvailable && agentReady && !processingReady ? ("WhisperX / GPU недоступны", "DangerBrush") :
            backendAvailable && agentReady ? ("Система готова · Qwen отключена", "SuccessBrush") :
            backendAvailable ? ("LAN-сервер доступен; Recorder Service не запущен", "WarningBrush") :
            recorderAvailable ? ("Recorder доступен; LAN-сервер недоступен", "WarningBrush") :
            ("LAN-сервер и Recorder недоступны", "DangerBrush");
        SetRuntimeStatus(backendAvailable, authenticated, recorderAvailable, processingReady, recorderHealth);
        _lastRecorderResponse = recorderResponse;
        UpdateGlobalRecordingController(recorderResponse);
        SetSystemStatus(status.Item1, status.Item2);
    }

    private void UpdateGlobalRecordingController(AgentIpcResponse? response)
    {
        if (!_uiDispatcherQueue.HasThreadAccess)
        {
            _uiDispatcherQueue.TryEnqueue(() => UpdateGlobalRecordingController(response));
            return;
        }

        var state = response?.State?.ToUpperInvariant() ?? string.Empty;
        var sessionId = response?.SessionId ?? response?.Health?.ActiveSessionId;
        var active = !string.IsNullOrWhiteSpace(sessionId)
            && state is "RECORDING" or "PAUSED" or "STARTING";
        GlobalRecordingController.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active)
        {
            _globalRecordingTimer?.Stop();
            return;
        }

        var paused = state == "PAUSED";
        _globalRecordingPaused = paused;
        _globalRecordingMediaTimeMs = Math.Max(0, response?.MediaTimeMs ?? 0);
        _globalRecordingSampleAtUtc = DateTimeOffset.UtcNow;
        _globalRecordingTimer?.Start();
        GlobalRecordingStateText.Text = paused ? "Запись приостановлена" : "Идёт запись";
        GlobalRecordingTimerText.Text = FormatMediaTime(_globalRecordingMediaTimeMs);
        GlobalRecordingMicrophoneText.Text = string.IsNullOrWhiteSpace(response?.Health?.EffectiveMicrophoneDeviceName)
            ? "Микрофон"
            : response.Health.EffectiveMicrophoneDeviceName;
        GlobalPauseResumeButton.Content = paused ? "Продолжить" : "Пауза";
        GlobalPauseResumeButton.IsEnabled = state is "RECORDING" or "PAUSED";
        GlobalMarkerButton.IsEnabled = state is "RECORDING" or "PAUSED";
        GlobalStopButton.IsEnabled = state is "RECORDING" or "PAUSED" or "STARTING";
        if (Application.Current.Resources[paused ? "WarningBrush" : "DangerBrush"] is Brush brush)
            GlobalRecordingIndicator.Fill = brush;
    }

    private void GlobalRecordingTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (GlobalRecordingController.Visibility != Visibility.Visible) return;
        var elapsedMs = _globalRecordingPaused
            ? 0
            : Math.Clamp((long)(DateTimeOffset.UtcNow - _globalRecordingSampleAtUtc).TotalMilliseconds, 0, 6000);
        GlobalRecordingTimerText.Text = FormatMediaTime(_globalRecordingMediaTimeMs + elapsedMs);
    }

    private static string FormatMediaTime(long? mediaTimeMs)
    {
        if (mediaTimeMs is not long value || value < 0) return "00:00:00";
        var duration = TimeSpan.FromMilliseconds(value);
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private async void GlobalPauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRecorderResponse?.State is null) return;
        try
        {
            var response = _lastRecorderResponse.State.Equals("PAUSED", StringComparison.OrdinalIgnoreCase)
                ? await _services.Recorder.ResumeAsync(_statusCts.Token)
                : await _services.Recorder.PauseAsync(_statusCts.Token);
            if (!response.Ok)
                SetSystemStatus(response.Error ?? "Не удалось изменить состояние записи", "WarningBrush");
            await RefreshSystemStatusAsync(_statusCts.Token);
        }
        catch (OperationCanceledException) when (_statusCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetSystemStatus($"Не удалось изменить запись: {UiErrorFormatter.Format(ex)}", "WarningBrush");
        }
    }

    private async void GlobalMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var response = await _services.Recorder.AddMarkerAsync("MARKER", _statusCts.Token);
            if (!response.Ok)
                SetSystemStatus(response.Error ?? "Не удалось добавить метку", "WarningBrush");
            await RefreshSystemStatusAsync(_statusCts.Token);
        }
        catch (OperationCanceledException) when (_statusCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetSystemStatus($"Не удалось добавить метку: {UiErrorFormatter.Format(ex)}", "WarningBrush");
        }
    }

    private async void GlobalStopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var response = await _services.Recorder.StopAsync(_statusCts.Token);
            if (!response.Ok)
                SetSystemStatus(response.Error ?? "Не удалось завершить запись", "WarningBrush");
            await RefreshSystemStatusAsync(_statusCts.Token);
        }
        catch (OperationCanceledException) when (_statusCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetSystemStatus($"Не удалось завершить запись: {UiErrorFormatter.Format(ex)}", "WarningBrush");
        }
    }

    private void SetVoiceStatus(WhisperX.Atom.Desktop.DesktopVoiceSnapshot? snapshot)
    {
        if (!_uiDispatcherQueue.HasThreadAccess)
        {
            _uiDispatcherQueue.TryEnqueue(() => SetVoiceStatus(snapshot));
            return;
        }
        if (snapshot is null)
        {
            var controllerError = _services.VoiceHost.LastErrorCode;
            VoiceStatusText.Text = controllerError is not null
                ? $"Мифодий · {controllerError}"
                : _services.VoiceHost.State == "NEEDS_SETUP" ? "Мифодий · требуется настройка" : "Мифодий · выключен";
            SetStatusPill(VoiceStatusPill, VoiceStatusText, controllerError is not null || _services.VoiceHost.State == "NEEDS_SETUP" ? "warning" : "neutral");
            return;
        }
        var heartbeat = snapshot.HeartbeatAtUtc ?? snapshot.UpdatedAt;
        var stale = DateTimeOffset.UtcNow - heartbeat.ToUniversalTime() > TimeSpan.FromSeconds(10);
        var voiceState = stale ? "Мифодий · нет heartbeat" : snapshot.State.ToUpperInvariant() switch
        {
            "LISTENING" => "Мифодий · слушает",
            "STARTING" => "Мифодий · запускается",
            "RECOGNIZING" or "CAPTURING" or "WAKEDETECTED" or "CONFIRMING" => "Мифодий · распознаёт",
            "EXECUTING" => "Мифодий · выполняет",
            "RESPONDING" or "COOLDOWN" => "Мифодий · говорит",
            "DEGRADED" or "ERROR" => "Мифодий · требуется настройка",
            _ => "Мифодий · выключен"
        };
        VoiceStatusText.Text = voiceState;
        SetStatusPill(VoiceStatusPill, VoiceStatusText, stale || snapshot.State.Equals("DEGRADED", StringComparison.OrdinalIgnoreCase) ? "warning" : snapshot.State.Equals("LISTENING", StringComparison.OrdinalIgnoreCase) ? "success" : "neutral");
    }

    private void QueueAgentRecovery(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextAgentRecoveryAtUtc
            || Interlocked.CompareExchange(ref _agentRecoveryRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var status = await _services.AgentBootstrap.EnsureAgentReadyAsync(cancellationToken);
                _nextAgentRecoveryAtUtc = DateTimeOffset.UtcNow.Add(status.Ready
                    ? TimeSpan.FromMinutes(1)
                    : TimeSpan.FromSeconds(15));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception)
            {
                _nextAgentRecoveryAtUtc = DateTimeOffset.UtcNow.AddSeconds(15);
            }
            finally
            {
                Volatile.Write(ref _agentRecoveryRunning, 0);
            }
        }, CancellationToken.None);
    }

    private void SetSystemStatus(string text, string brushKey)
    {
        if (!_uiDispatcherQueue.HasThreadAccess)
        {
            _uiDispatcherQueue.TryEnqueue(() => SetSystemStatus(text, brushKey));
            return;
        }

        SystemStatusText.Text = text;
        if (Application.Current.Resources[brushKey] is Brush brush)
        {
            SystemStatusIndicator.Fill = brush;
        }
    }

    private void SetRuntimeStatus(bool backendAvailable, bool authenticated, bool recorderAvailable, bool processingReady, AgentIpcHealth? recorderHealth = null)
    {
        if (!_uiDispatcherQueue.HasThreadAccess)
        {
            _uiDispatcherQueue.TryEnqueue(() => SetRuntimeStatus(backendAvailable, authenticated, recorderAvailable, processingReady, recorderHealth));
            return;
        }

        RecorderStatusText.Text = recorderAvailable ? "Recorder · готов" : "Recorder · недоступен";
        SetStatusPill(RecorderStatusPill, RecorderStatusText, recorderAvailable ? "success" : "danger");
        ToolTipService.SetToolTip(
            RecorderStatusText,
            recorderAvailable
                ? "Recorder Agent отвечает. Уровень сигнала и выбранный микрофон доступны на странице «Запись»."
                : "Recorder Agent не отвечает. Откройте «Настройки» → «Состояние системы» и проверьте локальный сервис.");
        ServerStatusText.Text = !backendAvailable
            ? "Сервер · офлайн"
            : authenticated
                ? "Сервер · доступен"
                : "Сервер · требуется вход";
        SetStatusPill(ServerStatusPill, ServerStatusText, !backendAvailable ? "warning" : authenticated ? "success" : "warning");
        ToolTipService.SetToolTip(
            ServerStatusText,
            !backendAvailable
                ? "LAN-сервер не отвечает. Локальная запись может продолжиться, доставка будет повторена позже."
                : authenticated
                    ? "LAN-сервер доступен, API-сессия подтверждена."
                    : "LAN-сервер доступен, но требуется вход в API.");
        WhisperXStatusText.Text = processingReady ? "WhisperX · готов" : "WhisperX · не готов";
        SetStatusPill(WhisperXStatusPill, WhisperXStatusText, processingReady ? "success" : "warning");
        ToolTipService.SetToolTip(
            WhisperXStatusText,
            processingReady
                ? "WhisperX и обязательные worker-компоненты готовы к транскрибации."
                : "WhisperX или GPU/worker ещё не готовы. Откройте «Состояние системы» для деталей.");

        StatusFlyoutRecorderText.Text = recorderAvailable
            ? "Recorder Host · готов"
            : "Recorder Host · недоступен";
        StatusFlyoutMicrophoneText.Text = recorderHealth is null
            ? "Микрофон · нет данных"
            : string.IsNullOrWhiteSpace(recorderHealth.EffectiveMicrophoneDeviceName)
                ? "Микрофон · устройство не выбрано"
                : $"Микрофон · {recorderHealth.EffectiveMicrophoneDeviceName}";
        StatusFlyoutStorageText.Text = recorderHealth is { FreeBytes: > 0, TotalBytes: > 0 }
            ? $"Локальное место · {FormatBytes(recorderHealth.FreeBytes)} свободно"
            : "Локальное место · нет данных";
        StatusFlyoutServerText.Text = !backendAvailable
            ? "Сервер · офлайн"
            : authenticated ? "Сервер · доступен" : "Сервер · требуется вход";
        StatusFlyoutWhisperText.Text = processingReady
            ? "WhisperX · готов"
            : "WhisperX · не готов";
        StatusFlyoutSummaryText.Text = backendAvailable && recorderAvailable && processingReady
            ? "Основной цикл готов: запись сохраняется локально, обработка выполняется отдельно."
            : "Есть компоненты, требующие внимания. Подробные коды доступны в настройках и состоянии системы.";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        string[] units = ["КБ", "МБ", "ГБ", "ТБ"];
        var value = (double)bytes;
        var index = -1;
        do
        {
            value /= 1024;
            index++;
        }
        while (value >= 1024 && index < units.Length - 1);
        return $"{value:0.#} {units[index]}";
    }

    private static void SetStatusPill(Border pill, TextBlock text, string state)
    {
        var resources = Application.Current.Resources;
        var (background, border, foreground) = state switch
        {
            "success" => ("SurfaceGreenBrush", "SuccessBrush", "TextBrush"),
            "warning" => ("SurfaceOrangeBrush", "WarningBrush", "TextBrush"),
            "danger" => ("DangerSurfaceBrush", "DangerBorderBrush", "DangerBrush"),
            _ => ("SurfaceBrush", "BorderBrush", "TextSecondaryBrush")
        };
        if (resources[background] is Brush backgroundBrush) pill.Background = backgroundBrush;
        if (resources[border] is Brush borderBrush) pill.BorderBrush = borderBrush;
        if (resources[foreground] is Brush foregroundBrush) text.Foreground = foregroundBrush;
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _globalRecordingTimer?.Stop();
        _statusCts.Cancel();
        _statusCts.Dispose();
        try { await _services.VoiceHost.StopAsync(); } catch { }
    }
}

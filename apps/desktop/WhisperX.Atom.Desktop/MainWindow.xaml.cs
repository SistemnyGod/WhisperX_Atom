using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using WhisperX.Atom.Desktop;
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
        DesktopProcessingReadiness? processingReadiness = null;
        try { backendAvailable = await backendTask; } catch (OperationCanceledException) { throw; } catch { }
        try { serverVersion = await versionTask; } catch (OperationCanceledException) { throw; } catch { }
        try { processingReadiness = await processingTask; } catch (OperationCanceledException) { throw; } catch { }
        try { recorderAvailable = (await recorderTask).IsReachable; } catch (OperationCanceledException) { throw; } catch { }
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
        SetRuntimeStatus(backendAvailable, authenticated, recorderAvailable, processingReady);
        SetSystemStatus(status.Item1, status.Item2);
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
            VoiceStatusText.Text = _services.VoiceHost.State == "NEEDS_SETUP" ? "Мифодий · требуется настройка" : "Мифодий · выключен";
            SetStatusPill(VoiceStatusPill, VoiceStatusText, _services.VoiceHost.State == "NEEDS_SETUP" ? "warning" : "neutral");
            return;
        }
        var stale = DateTimeOffset.UtcNow - snapshot.UpdatedAt > TimeSpan.FromSeconds(10);
        var voiceState = stale ? "Мифодий · нет heartbeat" : snapshot.State.ToUpperInvariant() switch
        {
            "LISTENING" => "Мифодий · слушает",
            "STARTING" => "Мифодий · запускается",
            "RECOGNIZING" or "CAPTURING" or "WAKEDETECTED" => "Мифодий · распознаёт",
            "EXECUTING" => "Мифодий · выполняет",
            "RESPONDING" => "Мифодий · говорит",
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

    private void SetRuntimeStatus(bool backendAvailable, bool authenticated, bool recorderAvailable, bool processingReady)
    {
        if (!_uiDispatcherQueue.HasThreadAccess)
        {
            _uiDispatcherQueue.TryEnqueue(() => SetRuntimeStatus(backendAvailable, authenticated, recorderAvailable, processingReady));
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
        _statusCts.Cancel();
        _statusCts.Dispose();
        try { await _services.VoiceHost.StopAsync(); } catch { }
    }
}

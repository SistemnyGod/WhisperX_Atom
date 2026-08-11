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
    private readonly SemaphoreSlim _agentRecoveryGate = new(1, 1);
    private Task? _statusTask;
    private bool _suppressNavigation;
    private DateTimeOffset _lastAgentRecoveryAttempt = DateTimeOffset.MinValue;

    public MainWindow()
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

        var settingsStore = new DesktopSettingsStore();
        var settings = settingsStore.Load();
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
        AgentsNavItem.Content = "Агенты";
        AdministrationNavItem.Content = "Администрирование";
        SettingsNavItem.Content = "Настройки";
        SystemStatusText.Text = "Система";
        ProfileText.Text = string.IsNullOrWhiteSpace(settings.Username) ? "Локальная сессия" : settings.Username;
        _services = new FrontendServices(
            new RecorderPipeService(),
            new BackendService(settings),
            settingsStore);

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
        var recorderTask = _services.Recorder.GetHealthAsync(cancellationToken);
        await Task.WhenAll(backendTask, recorderTask);

        var backendAvailable = await backendTask;
        var recorderAvailable = (await recorderTask).Ok;
        var authenticated = backendAvailable && await _services.Backend.EnsureAuthenticatedAsync(cancellationToken);
        if (authenticated && recorderAvailable)
            QueueAgentRecovery(cancellationToken);
        SetSystemStatus(
            backendAvailable && !authenticated ? "Требуется вход" :
            backendAvailable && recorderAvailable ? "Система готова" :
            backendAvailable || recorderAvailable ? "Частично доступна" :
            "Сервисы недоступны",
            backendAvailable && !authenticated ? "WarningBrush" :
            backendAvailable && recorderAvailable ? "SuccessBrush" :
            backendAvailable || recorderAvailable ? "WarningBrush" :
            "DangerBrush");
    }

    private void QueueAgentRecovery(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try { await RecoverAgentIfNeededAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }, CancellationToken.None);
    }

    private async Task RecoverAgentIfNeededAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastAgentRecoveryAttempt < TimeSpan.FromSeconds(30)) return;
        if (!await _agentRecoveryGate.WaitAsync(0, cancellationToken)) return;
        _lastAgentRecoveryAttempt = DateTimeOffset.UtcNow;
        try
        {
            var user = await _services.Backend.GetCurrentUserAsync(cancellationToken);
            if (user is null || !string.Equals(user.Role, "Administrator", StringComparison.OrdinalIgnoreCase)) return;

            var healthResponse = await _services.Recorder.GetHealthAsync(cancellationToken);
            var health = healthResponse.Health;
            if (health is null) return;
            if (string.Equals(healthResponse.State, "Recording", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(healthResponse.State, "Paused", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(healthResponse.State, "Finalizing", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(health.ServerConnectionState, "CONNECTED", StringComparison.OrdinalIgnoreCase)) return;

            var installationId = health.InstallationId ?? Guid.NewGuid();
            var enrollment = await _services.Backend.LinkLocalAgentAsync(installationId, health.AgentId, "WhisperX Atom Desktop", cancellationToken);
            var settings = _services.Settings.Load();
            await _services.Recorder.ConfigureAgentAsync(
                _services.Backend.ApiUrl,
                Guid.Parse(enrollment.AgentId),
                enrollment.Token,
                settings.ArchiveRoot ?? DesktopSettings.DefaultArchiveRoot(),
                settings.MicrophoneDeviceId,
                settings.SystemAudioDeviceId,
                cancellationToken);
        }
        catch (DesktopApiException)
        {
            // The next status poll retries after the backoff window. Local recording remains available.
        }
        catch (Exception)
        {
            // Agent recovery is best effort and must never block the desktop shell or local recording.
        }
        finally { _agentRecoveryGate.Release(); }
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

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _statusCts.Cancel();
        _statusCts.Dispose();
        _agentRecoveryGate.Dispose();
        _services.Backend.Dispose();
    }
}

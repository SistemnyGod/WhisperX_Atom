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
        var versionTask = _services.Backend.GetSystemVersionAsync(cancellationToken);
        var recorderTask = _services.Recorder.GetHealthAsync(cancellationToken);

        var backendAvailable = false;
        DesktopSystemVersion? serverVersion = null;
        var recorderAvailable = false;
        try { backendAvailable = await backendTask; } catch (OperationCanceledException) { throw; } catch { }
        try { serverVersion = await versionTask; } catch (OperationCanceledException) { throw; } catch { }
        try { recorderAvailable = (await recorderTask).Ok; } catch (OperationCanceledException) { throw; } catch { }
        var authenticated = backendAvailable && await _services.Backend.EnsureAuthenticatedAsync(cancellationToken);
        if (authenticated && recorderAvailable)
            QueueAgentRecovery(cancellationToken);
        if (serverVersion is not null && Math.Abs((serverVersion.ServerTimeUtc - DateTimeOffset.UtcNow).TotalMinutes) > 5)
        {
            SetSystemStatus("Время ПК отличается от времени сервера более чем на 5 минут", "WarningBrush");
            return;
        }
        var status = backendAvailable && !authenticated ? ("Требуется вход", "WarningBrush") :
            backendAvailable && recorderAvailable ? ("Система готова", "SuccessBrush") :
            backendAvailable ? ("LAN-сервер доступен; Recorder Service не запущен", "WarningBrush") :
            recorderAvailable ? ("Recorder доступен; LAN-сервер недоступен", "WarningBrush") :
            ("LAN-сервер и Recorder недоступны", "DangerBrush");
        SetSystemStatus(status.Item1, status.Item2);
    }

    private void QueueAgentRecovery(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try { await _services.AgentBootstrap.EnsureAgentReadyAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception) { }
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

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _statusCts.Cancel();
        _statusCts.Dispose();
    }
}

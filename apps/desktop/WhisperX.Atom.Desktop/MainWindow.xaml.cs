using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using WhisperX_Atom_Desktop.Pages;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop;

public sealed partial class MainWindow : Window
{
    private readonly FrontendServices _services;
    private readonly CancellationTokenSource _statusCts = new();
    private Task? _statusTask;
    private bool _suppressNavigation;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        var settingsStore = new DesktopSettingsStore();
        var settings = settingsStore.Load();
        HomeNavItem.Content = "Главная";
        RecordingNavItem.Content = "Запись";
        SourcesNavItem.Content = "Источники";
        MeetingsNavItem.Content = "Совещания";
        TasksNavItem.Content = "Поручения";
        AssistantNavItem.Content = "Помощник";
        SettingsNavItem.Content = "Настройки";
        SystemStatusText.Text = "Система";
        ProfileText.Text = string.IsNullOrWhiteSpace(settings.Username) ? "Локальная сессия" : settings.Username;
        _services = new FrontendServices(
            new RecorderPipeService(),
            new BackendService(settings),
            settingsStore);

        Closed += MainWindow_Closed;
        _statusTask = PollSystemStatusAsync(_statusCts.Token);
        NavigateTo("home");
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
        object parameter = string.Equals(route, "meetings", StringComparison.OrdinalIgnoreCase) && payload is MeetingNavigationTarget target
            ? new MeetingNavigationRequest(_services, target)
            : _services;
        NavFrame.Navigate(route.ToLowerInvariant() switch
        {
            "recording" => typeof(RecordingPage),
            "sources" => typeof(SourcesPage),
            "meetings" => typeof(MeetingsPage),
            "tasks" => typeof(TasksPage),
            "assistant" => typeof(AssistantPage),
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
        SetSystemStatus(
            backendAvailable && recorderAvailable ? "Система готова" :
            backendAvailable || recorderAvailable ? "Частично доступна" :
            "Сервисы недоступны",
            backendAvailable && recorderAvailable ? "SuccessBrush" :
            backendAvailable || recorderAvailable ? "WarningBrush" :
            "DangerBrush");
    }

    private void SetSystemStatus(string text, string brushKey)
    {
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
        _services.Backend.Dispose();
    }
}

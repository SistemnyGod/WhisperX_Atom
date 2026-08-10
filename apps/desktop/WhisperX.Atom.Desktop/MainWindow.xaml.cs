using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using WhisperX_Atom_Desktop.Pages;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop;

public sealed partial class MainWindow : Window
{
    private readonly FrontendServices _services;
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
}

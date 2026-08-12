using Microsoft.UI.Xaml;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop;

public partial class App : Application
{
    private MainWindow? _window;
    private LoginWindow? _loginWindow;
    private FrontendServices? _services;
    private static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperXAtom", "desktop-startup.log");

    public static MainWindow MainWindow => ((App)Current)._window
        ?? throw new InvalidOperationException("Main window is not initialized.");

    public App()
    {
        try
        {
            InitializeComponent();
            UnhandledException += (_, args) => WriteStartupLog("XAML_UNHANDLED", args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                WriteStartupLog("APPDOMAIN_UNHANDLED", args.ExceptionObject as Exception);
            WriteStartupLog("APP_CONSTRUCTED", null);
        }
        catch (Exception exception)
        {
            WriteStartupLog("APP_CONSTRUCTOR_FAILED", exception);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = LaunchAsync();
    }

    private async Task LaunchAsync()
    {
        try
        {
            WriteStartupLog("LAUNCH_STARTED", null);
            var settingsStore = new WhisperX_Atom_Desktop.Services.DesktopSettingsStore();
            var settings = settingsStore.Load();
            _services = new WhisperX_Atom_Desktop.Services.FrontendServices(
                new WhisperX_Atom_Desktop.Services.RecorderPipeService(),
                new WhisperX_Atom_Desktop.Services.BackendService(settings),
                settingsStore);
            _services.LoggedOut += HandleLoggedOut;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var authenticated = await _services.Backend.EnsureAuthenticatedAsync(timeout.Token);
            if (authenticated)
            {
                AgentBootstrapStatus bootstrap;
                try { bootstrap = await _services.AgentBootstrap.EnsureAgentReadyAsync(timeout.Token); }
                catch (Exception exception)
                {
                    WriteStartupLog("AGENT_BOOTSTRAP_DEFERRED", exception);
                    bootstrap = new(false, false, _services.Backend.CanUseOffline, "SERVER_UNAVAILABLE", "LAN-сервер временно недоступен.");
                }

                if (bootstrap.Code == "SERVER_UNAVAILABLE" && !_services.Backend.CanUseOffline)
                    ShowLoginWindow("Первый вход должен быть выполнен при доступном LAN-сервере.");
                else
                    ShowMainWindow();
            }
            else if (_services.Backend.CanUseOffline)
            {
                ShowMainWindow();
            }
            else
            {
                ShowLoginWindow("Требуется вход в LAN-сервер.");
            }
        }
        catch (Exception exception)
        {
            WriteStartupLog("LAUNCH_FAILED", exception);
            if (_services is not null && _services.Backend.CanUseOffline)
                ShowMainWindow();
            else if (_services is not null)
                ShowLoginWindow("Сервер пока недоступен. Выполните вход после восстановления LAN.");
        }
    }

    private void ShowLoginWindow(string? message = null)
    {
        if (_services is null) return;
        if (_loginWindow is not null) return;
        _loginWindow = new LoginWindow(_services, OnAuthenticatedAsync, message);
        _loginWindow.Closed += (_, _) => _loginWindow = null;
        _loginWindow.Activate();
    }

    private Task OnAuthenticatedAsync(AgentBootstrapStatus status)
    {
        ShowMainWindow();
        return Task.CompletedTask;
    }

    private void ShowMainWindow()
    {
        if (_services is null || _window is not null) return;
        _window = new MainWindow(_services);
        _window.Closed += (_, _) => _window = null;
        _window.Activate();
        WriteStartupLog("MAIN_WINDOW_ACTIVATED", null);
        _window.NavigateTo("home");
        _window.StartBackgroundPolling();
        WriteStartupLog("BACKGROUND_POLLING_STARTED", null);
    }

    private void HandleLoggedOut()
    {
        if (_window is not null)
        {
            _window.Close();
            _window = null;
        }
        ShowLoginWindow("Выполнен выход. Требуется повторный вход.");
    }

    internal static void WriteStartupLog(string eventName, Exception? exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(StartupLogPath)!;
            Directory.CreateDirectory(directory);
            var details = exception is null ? string.Empty : Environment.NewLine + exception;
            File.AppendAllText(StartupLogPath, $"{DateTimeOffset.Now:O} {eventName}{details}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics must never prevent the UI from launching.
        }
    }
}

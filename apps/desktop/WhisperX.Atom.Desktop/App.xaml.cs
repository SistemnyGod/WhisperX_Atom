using Microsoft.UI.Xaml;

namespace WhisperX_Atom_Desktop;

public partial class App : Application
{
    private MainWindow? _window;
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
        try
        {
            WriteStartupLog("LAUNCH_STARTED", null);
            _window = new MainWindow();
            WriteStartupLog("MAIN_WINDOW_CREATED", null);
            _window.Activate();
            WriteStartupLog("MAIN_WINDOW_ACTIVATED", null);
            _window.NavigateTo("home");
            _window.StartBackgroundPolling();
            WriteStartupLog("BACKGROUND_POLLING_STARTED", null);
        }
        catch (Exception exception)
        {
            WriteStartupLog("LAUNCH_FAILED", exception);
            throw;
        }
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

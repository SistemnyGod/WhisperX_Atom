using Microsoft.UI.Xaml;

namespace WhisperX_Atom_Desktop;

public partial class App : Application
{
    private Window? _window;

    public static MainWindow MainWindow => ((App)Current)._window as MainWindow
        ?? throw new InvalidOperationException("Main window is not initialized.");

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

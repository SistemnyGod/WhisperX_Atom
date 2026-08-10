using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class SettingsPage : Page
{
    private FrontendServices? _services;
    public SettingsViewModel? ViewModel { get; private set; }

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (FrontendServices)e.Parameter;
        ViewModel = new SettingsViewModel(_services);
        DataContext = ViewModel;
        UpdateStatus();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.LoginAsync(PasswordBox.Password);
        PasswordBox.Password = string.Empty;
        UpdateStatus();
    }

    private async void CheckBackendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        try
        {
            var ready = await _services.Backend.CheckReadyAsync();
            StatusInfoBar.Severity = ready ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            StatusInfoBar.Message = ready ? "Локальный API доступен." : "Локальный API не ответил.";
            StatusInfoBar.IsOpen = true;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void RegisterAgentButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RegisterAgentAsync(EnrollmentSecretBox.Password);
        EnrollmentSecretBox.Password = string.Empty;
        UpdateStatus();
    }

    private async void SelectArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await ViewModel.SetArchiveRootAsync(folder.Path);
        UpdateStatus();
    }

    private void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{ViewModel.ArchiveRoot}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void UpdateStatus()
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.StatusText)) return;
        StatusInfoBar.Severity = ViewModel.StatusText.Contains("ошиб", StringComparison.OrdinalIgnoreCase) || ViewModel.StatusText.Contains("не ", StringComparison.OrdinalIgnoreCase)
            ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusInfoBar.Message = ViewModel.StatusText;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowError(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}

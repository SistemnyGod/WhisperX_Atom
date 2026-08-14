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
        SizeChanged += SettingsPage_SizeChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (FrontendServices)e.Parameter;
        ViewModel = new SettingsViewModel(_services);
        DataContext = ViewModel;
        UpdateStatus();
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ChangePasswordAsync(PasswordBox.Password, NewPasswordBox.Password);
        PasswordBox.Password = string.Empty;
        NewPasswordBox.Password = string.Empty;
        UpdateStatus();
    }

    private async void CheckBackendButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.CheckBackendAsync();
        UpdateStatus();
    }

    private async void ApplyServerOriginButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ApplyServerOriginAsync();
        UpdateStatus();
    }

    private async void ReconnectAgentButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var confirmation = new ContentDialog
        {
            Title = "Переподключить Recorder Agent?",
            Content = "Токен будет перевыпущен только для этого явного действия администратора. Текущий токен Agent перестанет работать.",
            PrimaryButtonText = "Переподключить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.ReconnectAgentAsync();
        UpdateStatus();
    }

    private async void StartRecorderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.StartRecorderServiceAsync();
        UpdateStatus();
    }

    private async void SaveDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.SaveDiagnosticsAsync();
        UpdateStatus();
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.LogoutAsync();
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
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось открыть папку архива.")); }
    }

    private void UpdateStatus()
    {
        if (ViewModel is null) return;
        LoginHintText.Visibility = ViewModel.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        PasswordFieldsPanel.Visibility = ViewModel.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        ChangePasswordButton.Visibility = ViewModel.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(ViewModel.StatusText)) return;
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

    private void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResponsiveLayout.SetTwoColumn(SettingsLayoutGrid, ApiSettingsCard, AgentSettingsCard, 420, e.NewSize.Width);
        ApiActionsPanel.Orientation = ResponsiveLayout.GetMode(e.NewSize.Width) == PageLayoutMode.Compact
            ? Orientation.Vertical
            : Orientation.Horizontal;
        ConfigureArchiveLayout(ResponsiveLayout.GetMode(e.NewSize.Width) == PageLayoutMode.Compact);
    }

    private void ConfigureArchiveLayout(bool compact)
    {
        ArchivePathGrid.ColumnDefinitions.Clear();
        ArchivePathGrid.RowDefinitions.Clear();
        if (compact)
        {
            ArchivePathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < 3; index++) ArchivePathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(SelectArchiveButton, 0);
            Grid.SetRow(SelectArchiveButton, 1);
            Grid.SetColumn(OpenArchiveButton, 0);
            Grid.SetRow(OpenArchiveButton, 2);
            return;
        }

        ArchivePathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ArchivePathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ArchivePathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ArchivePathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(SelectArchiveButton, 1);
        Grid.SetRow(SelectArchiveButton, 0);
        Grid.SetColumn(OpenArchiveButton, 2);
        Grid.SetRow(OpenArchiveButton, 0);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class SettingsPage : Page
{
    private FrontendServices? _services;
    private CancellationTokenSource? _voiceRefreshCts;
    private DispatcherQueueTimer? _voiceBarsTimer;
    private readonly List<Border> _voiceBars = [];
    private readonly Brush _voiceWaitingBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
    private readonly Brush _voiceSpeechBrush = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
    private readonly Brush _voiceQuietBrush = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
    private readonly Brush _voiceClippingBrush = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
    private double _voiceLevelSmoothed;
    public SettingsViewModel? ViewModel { get; private set; }

    public SettingsPage()
    {
        InitializeComponent();
        InitializeVoiceBars();
        SizeChanged += SettingsPage_SizeChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (FrontendServices)e.Parameter;
        ViewModel = new SettingsViewModel(_services);
        DataContext = ViewModel;
        UpdateStatus();
        _ = RefreshRuntimeDiagnosticsAsync();
        _voiceRefreshCts?.Cancel();
        _voiceRefreshCts = new CancellationTokenSource();
        _voiceBarsTimer?.Start();
        _ = RefreshVoiceLoopAsync(_voiceRefreshCts.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _voiceRefreshCts?.Cancel();
        _voiceRefreshCts?.Dispose();
        _voiceRefreshCts = null;
        _voiceBarsTimer?.Stop();
        base.OnNavigatedFrom(e);
    }

    private async Task RefreshVoiceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RefreshVoiceDiagnosticsAsync(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { }
            try { await Task.Delay(500, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RefreshRuntimeDiagnosticsAsync()
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshRecorderDiagnosticsAsync();
    }

    private async Task RefreshVoiceDiagnosticsAsync()
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshVoiceDiagnosticsAsync();
    }

    private void InitializeVoiceBars()
    {
        for (var index = 0; index < 24; index++)
        {
            VoiceBarsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                Width = 5,
                Height = 4,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = _voiceWaitingBrush
            };
            Grid.SetColumn(bar, index);
            VoiceBarsPanel.Children.Add(bar);
            _voiceBars.Add(bar);
        }

        _voiceBarsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _voiceBarsTimer.Interval = TimeSpan.FromMilliseconds(33);
        _voiceBarsTimer.Tick += (_, _) => RenderVoiceBars();
    }

    private void RenderVoiceBars()
    {
        if (ViewModel is null || _voiceBars.Count == 0) return;
        var target = Math.Clamp(ViewModel.VoiceLevelNormalized, 0d, 1d);
        _voiceLevelSmoothed += (target - _voiceLevelSmoothed) * 0.22d;
        if (target == 0 && _voiceLevelSmoothed < 0.01) _voiceLevelSmoothed = 0;
        var brush = ViewModel.VoiceSignalState.Contains("Перегрузка", StringComparison.OrdinalIgnoreCase)
            ? _voiceClippingBrush
            : ViewModel.VoiceSignalState.Contains("Голос", StringComparison.OrdinalIgnoreCase)
                ? _voiceSpeechBrush
                : ViewModel.VoiceSignalState.Contains("Тишина", StringComparison.OrdinalIgnoreCase)
                    ? _voiceQuietBrush
                    : _voiceWaitingBrush;
        var center = (_voiceBars.Count - 1) / 2d;
        var radius = Math.Max(1d, center);
        for (var index = 0; index < _voiceBars.Count; index++)
        {
            var symmetry = 1d - Math.Abs(index - center) / radius;
            var height = 4d + (48d * _voiceLevelSmoothed * (0.55d + 0.45d * symmetry));
            _voiceBars[index].Height = Math.Clamp(height, 4d, 54d);
            _voiceBars[index].Background = brush;
        }
    }

    private async void TestVoiceSpeechButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TestVoiceSpeechAsync("Мифодий, начни запись");
        UpdateStatus();
    }

    private async void TestVoiceTtsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TestVoiceTtsAsync();
        UpdateStatus();
    }

    private async void RefreshVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshVoiceDiagnosticsAsync();
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
        await ViewModel.RefreshRecorderDiagnosticsAsync();
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

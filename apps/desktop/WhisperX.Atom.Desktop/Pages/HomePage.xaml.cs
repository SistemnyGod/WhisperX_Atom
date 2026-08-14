using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class HomePage : Page
{
    private FrontendServices? _services;
    private CancellationTokenSource? _pageCts;
    public HomeViewModel? ViewModel { get; private set; }

    public HomePage()
    {
        InitializeComponent();
        SizeChanged += HomePage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (FrontendServices)e.Parameter;
        _pageCts = new CancellationTokenSource();
        ViewModel = new HomeViewModel(_services);
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        await ViewModel.StartPollingAsync(_pageCts.Token);
        UpdateEmptyState();
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        if (ViewModel is not null) await ViewModel.StopPollingAsync();
        _pageCts?.Dispose();
        _pageCts = null;
        if (ViewModel is not null) ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HomeViewModel.HasMeetings)
            or nameof(HomeViewModel.MeetingsMessage)
            or nameof(HomeViewModel.ErrorText)
            or nameof(HomeViewModel.IsLoading)
            or nameof(HomeViewModel.AgentAvailable)
            or nameof(HomeViewModel.MicrophoneSignalState)
            or nameof(HomeViewModel.RecordingBadgeText)
            or nameof(HomeViewModel.MediaTimeText))
            UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (ViewModel is null) return;
        HomeLoadingState.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        MeetingsEmptyState.Visibility = ViewModel.IsLoading || ViewModel.HasMeetings ? Visibility.Collapsed : Visibility.Visible;
        MeetingsList.Visibility = ViewModel.IsLoading || !ViewModel.HasMeetings ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = !ViewModel.IsLoading;
        EmptyImportButton.IsEnabled = ViewModel.ApiAvailable && !ViewModel.IsLoading;
        QuickImportButton.IsEnabled = ViewModel.ApiAvailable && !ViewModel.IsLoading;
        var statusBrushKey = !ViewModel.AgentAvailable
            ? "DangerBrush"
            : ViewModel.MicrophoneSignalState is "READY_NO_SIGNAL" or "CLIPPING" or "NO_PACKETS"
                ? "WarningBrush"
                : "SuccessBrush";
        var statusBrush = (Brush)Application.Current.Resources[statusBrushKey];
        AgentIndicator.Fill = statusBrush;
        AgentRailIndicator.Fill = statusBrush;
        UpdateRecordingBadge();
        OfflineInfoBar.IsOpen = !ViewModel.IsLoading && !ViewModel.ApiAvailable;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.ErrorText) && ViewModel.ApiAvailable;
        ErrorInfoBar.Message = ViewModel.ErrorText;
    }

    private void UpdateRecordingBadge()
    {
        if (ViewModel is null) return;
        var live = string.Equals(ViewModel.RecordingBadgeText, "LIVE", StringComparison.OrdinalIgnoreCase);
        RecordingBadge.Background = (Brush)Application.Current.Resources[live ? "DangerSurfaceBrush" : "SurfaceBrush"];
        RecordingBadge.BorderBrush = (Brush)Application.Current.Resources[live ? "DangerBorderBrush" : "BorderBrush"];
        RecordingBadgeText.Foreground = (Brush)Application.Current.Resources[live ? "DangerBrush" : "AccentBrush"];
    }

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HomeContentGrid is null || HomeMainColumn is null || HomeRailColumn is null || KpiGrid is null) return;
        ResponsiveLayout.SetTwoColumn(HomeContentGrid, HomeMainColumn, HomeRailColumn, 320, e.NewSize.Width);
        ResponsiveLayout.SetCardColumns(KpiGrid, new FrameworkElement?[] { KpiApiCard, KpiStorageCard, KpiQueueCard, KpiSummaryCard, KpiTasksCard, KpiGpuCard }, e.NewSize.Width, 3);
    }

    private void StartRecordingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("recording");
    private void NewMeetingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");
    private void OpenMeetingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");
    private void OpenRecordingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("recording");
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (_pageCts is null) return;
        await ViewModel.RefreshAsync(_pageCts.Token);
        UpdateEmptyState();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
        foreach (var extension in new[] { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".mp4", ".mkv", ".mov", ".webm", ".avi" })
            picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            ErrorInfoBar.IsOpen = false;
            if (_pageCts is null) return;
            await _services.Backend.ImportFileAsync(file.Path, cancellationToken: _pageCts.Token);
            if (ViewModel is not null) await ViewModel.RefreshAsync(_pageCts.Token);
            UpdateEmptyState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Severity = InfoBarSeverity.Error;
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось импортировать файл.");
            ErrorInfoBar.IsOpen = true;
        }
    }
}

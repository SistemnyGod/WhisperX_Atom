using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private bool _applyingVoiceGain;
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
            or nameof(HomeViewModel.RecorderSummary)
            or nameof(HomeViewModel.ServerSummary)
            or nameof(HomeViewModel.WhisperXSummary)
            or nameof(HomeViewModel.VoiceStatus)
            or nameof(HomeViewModel.VoiceProcessingGainDb)
            or nameof(HomeViewModel.RecentMeetingRows)
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
        var statusBrushKey = !ViewModel.AgentAvailable
            ? "DangerBrush"
            : ViewModel.MicrophoneSignalState is "READY_NO_SIGNAL" or "CLIPPING" or "NO_PACKETS"
                ? "WarningBrush"
                : "SuccessBrush";
        var statusBrush = (Brush)Application.Current.Resources[statusBrushKey];
        AgentIndicator.Fill = statusBrush;
        RecorderSummaryIndicator.Fill = StatusBrush(ViewModel.RecorderSummary);
        ServerSummaryIndicator.Fill = StatusBrush(ViewModel.ServerSummary);
        WhisperXSummaryIndicator.Fill = StatusBrush(ViewModel.WhisperXSummary);
        var voiceBrushKey = ViewModel.VoiceStatus.Contains("недоступен", StringComparison.OrdinalIgnoreCase)
            ? "DangerBrush"
            : ViewModel.VoiceStatus.Contains("требует", StringComparison.OrdinalIgnoreCase)
                || ViewModel.VoiceStatus.Contains("ждёт", StringComparison.OrdinalIgnoreCase)
                ? "WarningBrush"
                : ViewModel.VoiceStatus.Contains("слушает", StringComparison.OrdinalIgnoreCase)
                    || ViewModel.VoiceStatus.Contains("готов", StringComparison.OrdinalIgnoreCase)
                    || ViewModel.VoiceStatus.Contains("озвучивает", StringComparison.OrdinalIgnoreCase)
                    ? "SuccessBrush"
                    : "NeutralStatusBrush";
        VoiceSummaryIndicator.Fill = (Brush)Application.Current.Resources[voiceBrushKey];
        UpdateRecordingBadge();
        OfflineInfoBar.IsOpen = !ViewModel.IsLoading && !ViewModel.ApiAvailable;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.ErrorText) && ViewModel.ApiAvailable;
        ErrorInfoBar.Message = ViewModel.ErrorText;
    }

    private static Brush StatusBrush(string? status)
    {
        var value = status ?? string.Empty;
        var key = value.Contains("недоступ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ошиб", StringComparison.OrdinalIgnoreCase)
            ? "DangerBrush"
            : value.Contains("требует", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ожида", StringComparison.OrdinalIgnoreCase)
                || value.Contains("провер", StringComparison.OrdinalIgnoreCase)
                || value.Contains("вход", StringComparison.OrdinalIgnoreCase)
                || value.Contains("не подтверж", StringComparison.OrdinalIgnoreCase)
                ? "WarningBrush"
                : value.Contains("готов", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("подключ", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("идёт", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("занят", StringComparison.OrdinalIgnoreCase)
                    ? "SuccessBrush"
                    : "NeutralStatusBrush";
        return (Brush)Application.Current.Resources[key];
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
        if (HomeContentGrid is null || HomeMainColumn is null || KpiGrid is null) return;
        ResponsiveLayout.SetCardColumns(KpiGrid, new FrameworkElement?[] { KpiApiCard, KpiStorageCard, KpiQueueCard, KpiSummaryCard, KpiTasksCard, KpiGpuCard }, e.NewSize.Width, 3);
        var compact = ResponsiveLayout.GetMode(e.NewSize.Width) == PageLayoutMode.Compact;
        HeroIdentityRow.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        HeroIdentityRow.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        HeroCommandRow.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        HeroRecorderPanel.ColumnDefinitions.Clear();
        HeroRecorderPanel.RowDefinitions.Clear();
        HeroRecorderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (compact)
        {
            HeroRecorderPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HeroRecorderPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HeroRecorderColumn, 0);
            Grid.SetRow(HeroRecorderColumn, 0);
            Grid.SetColumn(VoiceGainCard, 0);
            Grid.SetRow(VoiceGainCard, 1);
            VoiceGainCard.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            HeroRecorderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            Grid.SetColumn(HeroRecorderColumn, 0);
            Grid.SetRow(HeroRecorderColumn, 0);
            Grid.SetColumn(VoiceGainCard, 1);
            Grid.SetRow(VoiceGainCard, 0);
            VoiceGainCard.Margin = new Thickness(0);
        }
        HomeNotices.Width = Math.Min(380, Math.Max(260, e.NewSize.Width - 56));
    }

    private async void VoiceGainSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_applyingVoiceGain || ViewModel is null || _pageCts is null) return;
        _applyingVoiceGain = true;
        try
        {
            await ViewModel.SetVoiceProcessingGainAsync((int)Math.Round(e.NewValue), _pageCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Severity = InfoBarSeverity.Warning;
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось применить усиление голосового мониторинга.");
            ErrorInfoBar.IsOpen = true;
        }
        finally { _applyingVoiceGain = false; }
    }

    private void StartRecordingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("recording");
    private void NewMeetingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");
    private void OpenMeetingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");
    private void OpenMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string meetingId } && Guid.TryParse(meetingId, out _))
            App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(meetingId));
        else
            App.MainWindow.NavigateTo("meetings");
    }
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

    private static HomeMeetingRowViewModel? GetMeetingRow(object sender) => sender switch
    {
        MenuFlyoutItem { Tag: HomeMeetingRowViewModel row } => row,
        FrameworkElement { DataContext: HomeMeetingRowViewModel row } => row,
        _ => null
    };

    private async void StopDeliveryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = GetMeetingRow(sender);
        if (row?.LocalSessionId is null || _services is null || _pageCts is null) return;
        try
        {
            var response = await _services.Recorder.StopDeliveryAsync(row.LocalSessionId, _pageCts.Token);
            if (!response.Ok) ShowError(response.Error ?? "Не удалось приостановить доставку.");
            else await ViewModel!.RefreshAsync(_pageCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось приостановить доставку.")); }
    }

    private async void RetryMeetingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = GetMeetingRow(sender);
        if (row is null || _services is null || _pageCts is null) return;
        try
        {
            if (row.LocalSessionId is not null)
            {
                var response = await _services.Recorder.RetryUploadAsync(row.LocalSessionId, _pageCts.Token);
                if (!response.Ok) { ShowError(response.Error ?? "Не удалось повторить отправку."); return; }
            }
            else if (Guid.TryParse(row.RetryJobId, out var jobId) && await _services.Backend.RetryJobAsync(jobId, _pageCts.Token) is null)
            {
                ShowError("Сервер не принял повторную постановку задания.");
                return;
            }
            await ViewModel!.RefreshAsync(_pageCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось повторить отправку.")); }
    }

    private async void CancelMeetingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = GetMeetingRow(sender);
        if (row is null || _services is null || _pageCts is null || !Guid.TryParse(row.Id, out var meetingId)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Отменить обработку?",
            Content = "Новые этапы обработки будут остановлены. Уже готовые результаты останутся доступными.",
            PrimaryButtonText = "Отменить обработку",
            CloseButtonText = "Не отменять",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (await _services.Backend.CancelMeetingAsync(meetingId, _pageCts.Token) is null) { ShowError("Не удалось отменить обработку."); return; }
            await ViewModel!.RefreshAsync(_pageCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось отменить обработку.")); }
    }

    private async void DeleteMeetingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = GetMeetingRow(sender);
        if (row is null || _services is null || _pageCts is null || !Guid.TryParse(row.Id, out var meetingId)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Удалить запись?",
            Content = "Серверные результаты и медиа будут удалены. Локальный архив Recorder останется на диске.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (!await _services.Backend.DeleteMeetingAsync(meetingId, _pageCts.Token)) { ShowError("Не удалось удалить запись."); return; }
            await ViewModel!.RefreshAsync(_pageCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось удалить запись.")); }
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}

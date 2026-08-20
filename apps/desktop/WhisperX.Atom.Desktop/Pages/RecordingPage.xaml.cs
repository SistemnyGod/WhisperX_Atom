using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class RecordingPage : Page
{
    private FrontendServices? _services;
    public RecordingViewModel? ViewModel { get; private set; }
    private bool _updatingSelections;
    private PageLayoutMode? _lastLayoutMode;
    private bool? _lastActionCompact;
    private RecordingState? _lastRecordedState;

    public RecordingPage()
    {
        InitializeComponent();
        SizeChanged += RecordingPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (FrontendServices)e.Parameter;
        ViewModel = new RecordingViewModel(_services);
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        await ViewModel.StartPollingAsync();
        SyncSelections();
        SyncRecordingProfile();
        UpdateError();
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.StopPollingAsync();
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecordingViewModel.Microphones) or nameof(RecordingViewModel.SystemAudioDevices)) SyncSelections();
        if (e.PropertyName is nameof(RecordingViewModel.RecordingProfile)) SyncRecordingProfile();
        if (e.PropertyName is nameof(RecordingViewModel.ErrorMessage) or nameof(RecordingViewModel.HasError)
            or nameof(RecordingViewModel.WarningMessage) or nameof(RecordingViewModel.HasWarning)) UpdateError();
        if (e.PropertyName is nameof(RecordingViewModel.MicrophoneSignalLabel)
            or nameof(RecordingViewModel.MicrophoneSignalState)
            or nameof(RecordingViewModel.MicrophoneTelemetryStale)) UpdateSignalVisual();
        if (e.PropertyName is nameof(RecordingViewModel.LocalFinalizeState)
            or nameof(RecordingViewModel.LocalFinalizeStatusLabel)) UpdateFinalizeOutcomeVisual();
        if (e.PropertyName is nameof(RecordingViewModel.State))
        {
            RecordSessionState(ViewModel?.State);
            UpdateStateIndicator();
            UpdateActionButtons();
            UpdateStateLayout();
        }
        if (e.PropertyName is nameof(RecordingViewModel.HasTranscript) or nameof(RecordingViewModel.IsProcessing))
            UpdateStateLayout();
    }

    private void SyncSelections()
    {
        if (ViewModel is null) return;
        _updatingSelections = true;
        try
        {
            MicrophoneSelector.SelectedItem = ViewModel.Microphones.FirstOrDefault(x => string.Equals(x.Id, ViewModel.SelectedMicrophoneId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? ViewModel.Microphones.FirstOrDefault();
            SystemAudioSelector.SelectedItem = ViewModel.SystemAudioDevices.FirstOrDefault(x => string.Equals(x.Id, ViewModel.SelectedSystemAudioId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? ViewModel.SystemAudioDevices.FirstOrDefault();
        }
        finally { _updatingSelections = false; }
    }

    private void UpdateError()
    {
        if (ViewModel is null) return;
        ErrorInfoBar.IsOpen = ViewModel.HasError;
        ErrorInfoBar.Message = ViewModel.ErrorMessage;
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        RetryUploadInfoButton.Visibility = ViewModel.CanRetryUpload ? Visibility.Visible : Visibility.Collapsed;
        WarningInfoBar.IsOpen = ViewModel.HasWarning;
        WarningInfoBar.Message = ViewModel.WarningMessage;
        WarningInfoBar.Severity = InfoBarSeverity.Warning;
        UpdateStateIndicator();
        UpdateActionButtons();
        UpdateStateLayout();
        UpdateFinalizeOutcomeVisual();
        UpdateSignalVisual();
    }

    private async void CheckDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshAsync();
        SyncSelections();
        SyncRecordingProfile();
        UpdateError();
    }

    private void SyncRecordingProfile()
    {
        if (ViewModel is null) return;
        RecordingProfileSelector.SelectedValue = ViewModel.RecordingProfile;
    }

    private async void TestMicrophoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TestMicrophoneAsync();
        UpdateError();
    }

    private async void RoomAcousticCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RunRoomAcousticCheckAsync();
        UpdateError();
    }

    private async void TestSystemAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TestSystemAudioAsync();
        UpdateError();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SessionEvents.Clear();
        _lastRecordedState = null;
        await ViewModel.StartRecordingAsync();
        UpdateError();
    }

    private async void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (ViewModel.State == RecordingState.Paused) await ViewModel.ResumeAsync();
        else await ViewModel.PauseAsync();
        UpdateError();
    }

    private async void MarkerButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null && await ViewModel.AddMarkerAsync()) ViewModel.SessionEvents.Add($"{DateTime.Now:HH:mm:ss}  Добавлена метка"); UpdateError(); }
    private async void DecisionButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null && await ViewModel.AddMarkerAsync("DECISION")) ViewModel.SessionEvents.Add($"{DateTime.Now:HH:mm:ss}  Добавлено решение"); UpdateError(); }
    private async void TaskButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null && await ViewModel.AddMarkerAsync("ACTION_ITEM")) ViewModel.SessionEvents.Add($"{DateTime.Now:HH:mm:ss}  Добавлено поручение"); UpdateError(); }
    private async void StopButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.StopRecordingAsync(); UpdateError(); }
    private async void RetryUploadButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.RetryUploadAsync(); UpdateError(); }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");

    private void OpenTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.MeetingId is not Guid meetingId) return;
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(meetingId.ToString(), null, null));
    }

    private async void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections || ViewModel?.IsDeviceListRefreshInProgress == true || ViewModel is null || MicrophoneSelector.SelectedItem is not AudioDeviceOption option) return;
        await ViewModel.SetMicrophoneAsync(option.Id);
        UpdateError();
    }

    private async void SystemAudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections || ViewModel?.IsDeviceListRefreshInProgress == true || ViewModel is null || SystemAudioSelector.SelectedItem is not AudioDeviceOption option) return;
        await ViewModel.SetSystemAudioAsync(option.Id);
        UpdateError();
    }

    private async void RecordingProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || RecordingProfileSelector.SelectedValue is not string profile) return;
        await ViewModel.SetRecordingProfileAsync(profile);
        UpdateError();
    }

    private async void AcousticProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || AcousticProfileSelector.SelectedValue is not string profile) return;
        await ViewModel.SetAcousticProfileAsync(profile);
        UpdateError();
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
        UpdateError();
    }

    private void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel?.ArchivePath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) path = ViewModel?.PlayableAudioPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenPlayableAudioButton_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel?.PlayableAudioFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenPlayableTrackButton_Click(object sender, RoutedEventArgs e)
    {
        var role = (sender as Button)?.Tag?.ToString() ?? string.Empty;
        var path = ViewModel?.GetPlayableAudioPath(role);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void RecordingPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var mode = ResponsiveLayout.GetMode(e.NewSize.Width);
        var actionCompact = e.NewSize.Width < 760;
        if (_lastLayoutMode == mode && _lastActionCompact == actionCompact) return;
        _lastLayoutMode = mode;
        _lastActionCompact = actionCompact;
        UpdateIdleSetupLayout(e.NewSize.Width < 900);
        UpdateActiveRecordingLayout(mode == PageLayoutMode.Compact);
        RecordingActionsPanel.Orientation = actionCompact ? Orientation.Vertical : Orientation.Horizontal;
        UpdateActionButtons();
    }

    private void UpdateActiveRecordingLayout(bool compact)
    {
        ActiveRecordingGrid.ColumnDefinitions.Clear();
        ActiveRecordingGrid.RowDefinitions.Clear();
        if (compact)
        {
            ActiveRecordingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ActiveRecordingGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ActiveRecordingGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(ActiveStatusRail, 0);
            Grid.SetRow(ActiveStatusRail, 1);
            return;
        }

        ActiveRecordingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
        ActiveRecordingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ActiveRecordingGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(ActiveStatusRail, 1);
        Grid.SetRow(ActiveStatusRail, 0);
    }

    private void UpdateIdleSetupLayout(bool compact)
    {
        IdleSetupGrid.ColumnDefinitions.Clear();
        IdleSetupGrid.RowDefinitions.Clear();
        if (compact)
        {
            IdleSetupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            IdleSetupGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            IdleSetupGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(SourceSetupCard, 0);
            Grid.SetRow(SourceSetupCard, 0);
            Grid.SetColumn(SignalPreviewCard, 0);
            Grid.SetRow(SignalPreviewCard, 1);
            return;
        }

        IdleSetupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        IdleSetupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        IdleSetupGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(SourceSetupCard, 0);
        Grid.SetRow(SourceSetupCard, 0);
        Grid.SetColumn(SignalPreviewCard, 1);
        Grid.SetRow(SignalPreviewCard, 0);
    }

    private void UpdateStateLayout()
    {
        if (ViewModel is null) return;
        var active = ViewModel.State is RecordingState.Recording or RecordingState.Paused;
        var finalizing = ViewModel.State == RecordingState.Finalizing;
        var showProcessing = finalizing || ViewModel.HasTranscript || ViewModel.IsProcessing;

        IdleSetupPanel.Visibility = active || finalizing ? Visibility.Collapsed : Visibility.Visible;
        ActiveRecordingPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        FinalizingPanel.Visibility = finalizing ? Visibility.Visible : Visibility.Collapsed;
        ProcessingPanel.Visibility = showProcessing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStateIndicator()
    {
        if (ViewModel is null) return;
        var key = ViewModel.State switch
        {
            RecordingState.Recording => "DangerBrush",
            RecordingState.Paused => "WarningBrush",
            RecordingState.Error or RecordingState.Unavailable => "DangerBrush",
            RecordingState.Finalizing => "WarningBrush",
            _ => "AccentBrush"
        };
        if (Application.Current.Resources[key] is Brush brush)
        {
            StateIndicator.Fill = brush;
            RecordingStateBadgeText.Foreground = brush;
        }

        var surfaceKey = ViewModel.State switch
        {
            RecordingState.Recording or RecordingState.Error or RecordingState.Unavailable => "DangerSurfaceBrush",
            RecordingState.Paused or RecordingState.Finalizing => "SurfaceOrangeBrush",
            _ => "SurfaceBrush"
        };
        var borderKey = ViewModel.State switch
        {
            RecordingState.Recording or RecordingState.Error or RecordingState.Unavailable => "DangerBorderBrush",
            RecordingState.Paused or RecordingState.Finalizing => "WarningBrush",
            _ => "BorderBrush"
        };
        if (Application.Current.Resources[surfaceKey] is Brush surfaceBrush) RecordingStateBadge.Background = surfaceBrush;
        if (Application.Current.Resources[borderKey] is Brush borderBrush) RecordingStateBadge.BorderBrush = borderBrush;
    }

    private void UpdateSignalVisual()
    {
        if (ViewModel is null || SignalStateBadge is null || SignalStateBadgeText is null) return;
        var state = ViewModel.MicrophoneSignalState ?? string.Empty;
        var stale = ViewModel.MicrophoneTelemetryStale;
        var (brushKey, surfaceKey, label) = stale
            ? ("MutedTextBrush", "SurfaceBrush", "Нет сигнала")
            : state.Contains("Перегруз", StringComparison.OrdinalIgnoreCase)
                ? ("DangerBrush", "DangerSurfaceBrush", "Перегрузка")
                : state.Contains("Слаб", StringComparison.OrdinalIgnoreCase) || state.Contains("Тиш", StringComparison.OrdinalIgnoreCase)
                    ? ("WarningBrush", "SurfaceOrangeBrush", "Слабый сигнал")
                    : state.Contains("Голос", StringComparison.OrdinalIgnoreCase) || state.Contains("Норм", StringComparison.OrdinalIgnoreCase)
                        ? ("SuccessBrush", "SurfaceGreenBrush", "Сигнал хороший")
                        : ("AccentBrush", "InfoSurfaceBrush", "Проверка сигнала");
        SignalStateBadgeText.Text = label;
        if (Application.Current.Resources[brushKey] is Brush brush)
        {
            SignalStateBadgeText.Foreground = brush;
            SignalStateBadge.BorderBrush = brush;
        }
        if (Application.Current.Resources[surfaceKey] is Brush surface)
            SignalStateBadge.Background = surface;
        ToolTipService.SetToolTip(SignalStateBadge, ViewModel.MicrophoneSignalLabel);
    }

    private void RecordSessionState(RecordingState? state)
    {
        if (ViewModel is null || state is null || state == _lastRecordedState) return;
        _lastRecordedState = state;
        var label = state.Value switch
        {
            RecordingState.Starting => "Подготовка записи",
            RecordingState.Recording => "Запись начата",
            RecordingState.Paused => "Запись приостановлена",
            RecordingState.Finalizing => "Захват остановлен · сохранение продолжается",
            RecordingState.Error => "Ошибка записи",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(label)) ViewModel.SessionEvents.Add($"{DateTime.Now:HH:mm:ss}  {label}");
    }

    private void UpdateActionButtons()
    {
        if (ViewModel is null) return;
        PauseResumeButton.Content = ViewModel.State == RecordingState.Paused ? "Продолжить" : "Пауза";
        PauseResumeButton.IsEnabled = ViewModel.CanPause || ViewModel.CanResume;
        ToolTipService.SetToolTip(PauseResumeButton, ViewModel.State == RecordingState.Paused
            ? "Продолжить локальную запись"
            : "Приостановить локальную запись");
    }

    private void UpdateFinalizeOutcomeVisual()
    {
        if (ViewModel is null) return;
        var state = ViewModel.LocalFinalizeState?.ToUpperInvariant() ?? string.Empty;
        var brushKey = state switch
        {
            "LOCAL_READY" => "SuccessBrush",
            "LOCAL_FAILED" => "DangerBrush",
            _ => "WarningBrush"
        };
        var surfaceKey = state switch
        {
            "LOCAL_READY" => "SurfaceGreenBrush",
            "LOCAL_FAILED" => "DangerSurfaceBrush",
            _ => "SurfaceOrangeBrush"
        };
        if (Application.Current.Resources[brushKey] is Brush brush)
        {
            FinalizeOutcomeIcon.Foreground = brush;
            FinalizeOutcomeText.Foreground = brush;
            FinalizeOutcomeSurface.BorderBrush = brush;
        }
        if (Application.Current.Resources[surfaceKey] is Brush surface)
            FinalizeOutcomeSurface.Background = surface;
    }
}

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
        if (e.PropertyName is nameof(RecordingViewModel.State))
        {
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

    private async void TestSystemAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TestSystemAudioAsync();
        UpdateError();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
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

    private async void MarkerButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.AddMarkerAsync(); UpdateError(); }
    private async void DecisionButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.AddMarkerAsync("DECISION"); UpdateError(); }
    private async void TaskButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.AddMarkerAsync("ACTION_ITEM"); UpdateError(); }
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
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void RecordingPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The new recorder card is single-column by design. Only the action
        // bar changes at the compact breakpoint, so the primary actions remain
        // visible at 100/125/150% Windows scaling without horizontal overflow.
        RecordingActionsPanel.Orientation = e.NewSize.Width < 760 ? Orientation.Vertical : Orientation.Horizontal;
        UpdateActionButtons();
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

    private void UpdateActionButtons()
    {
        if (ViewModel is null) return;
        PauseResumeButton.Content = ViewModel.State == RecordingState.Paused ? "Продолжить" : "Пауза";
        PauseResumeButton.IsEnabled = ViewModel.CanPause || ViewModel.CanResume;
        ToolTipService.SetToolTip(PauseResumeButton, ViewModel.State == RecordingState.Paused
            ? "Продолжить локальную запись"
            : "Приостановить локальную запись");
    }
}

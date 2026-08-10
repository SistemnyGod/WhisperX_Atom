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
        if (e.PropertyName is nameof(RecordingViewModel.ErrorMessage) or nameof(RecordingViewModel.HasError)) UpdateError();
        if (e.PropertyName is nameof(RecordingViewModel.State)) UpdateStateIndicator();
    }

    private void SyncSelections()
    {
        if (ViewModel is null) return;
        _updatingSelections = true;
        try
        {
            MicrophoneCombo.SelectedItem = ViewModel.Microphones.FirstOrDefault(x => string.Equals(x.Id, ViewModel.SelectedMicrophoneId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? ViewModel.Microphones.FirstOrDefault();
            SystemAudioCombo.SelectedItem = ViewModel.SystemAudioDevices.FirstOrDefault(x => string.Equals(x.Id, ViewModel.SelectedSystemAudioId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
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
        UpdateStateIndicator();
    }

    private async void CheckDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshAsync();
        SyncSelections();
        UpdateError();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.StartRecordingAsync();
        UpdateError();
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.PauseAsync(); UpdateError(); }
    private async void ResumeButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.ResumeAsync(); UpdateError(); }
    private async void MarkerButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.AddMarkerAsync(); UpdateError(); }
    private async void StopButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.StopRecordingAsync(); UpdateError(); }
    private async void RetryUploadButton_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.RetryUploadAsync(); UpdateError(); }

    private async void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections || ViewModel is null || MicrophoneCombo.SelectedItem is not AudioDeviceOption option) return;
        await ViewModel.SetMicrophoneAsync(option.Id);
        UpdateError();
    }

    private async void SystemAudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections || ViewModel is null || SystemAudioCombo.SelectedItem is not AudioDeviceOption option) return;
        await ViewModel.SetSystemAudioAsync(option.Id);
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

    private void RecordingPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResponsiveLayout.SetTwoColumn(RecordingHeroGrid, RecordingMainColumn, RecordingSourcesColumn, 340, e.NewSize.Width);
        ResponsiveLayout.SetCardColumns(LifeCycleGrid, LifeCycleGrid.Children.OfType<FrameworkElement>().ToArray(), e.NewSize.Width);
        RecordingActionsPanel.Orientation = Orientation.Vertical;
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
        if (Application.Current.Resources[key] is Brush brush) StateIndicator.Fill = brush;
    }
}

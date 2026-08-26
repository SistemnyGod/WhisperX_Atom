using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class HomePage : Page
{
    private FrontendServices? _services;
    public HomeViewModel? ViewModel { get; private set; }

    public HomePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (FrontendServices)e.Parameter;
        ViewModel = new HomeViewModel(_services);
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        await ViewModel.RefreshAsync();
        UpdateEmptyState();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HomeViewModel.HasMeetings) or nameof(HomeViewModel.MeetingsMessage) or nameof(HomeViewModel.ErrorText))
            UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (ViewModel is null) return;
        MeetingsEmptyState.Visibility = ViewModel.HasMeetings ? Visibility.Collapsed : Visibility.Visible;
        MeetingsList.Visibility = ViewModel.HasMeetings ? Visibility.Visible : Visibility.Collapsed;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.ErrorText);
        ErrorInfoBar.Message = ViewModel.ErrorText;
    }

    private void StartRecordingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("recording");
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshAsync();
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
            await _services.Backend.ImportFileAsync(file.Path, cancellationToken: CancellationToken.None);
            if (ViewModel is not null) await ViewModel.RefreshAsync();
            UpdateEmptyState();
        }
        catch (Exception ex)
        {
            ErrorInfoBar.Severity = InfoBarSeverity.Error;
            ErrorInfoBar.Message = ex.Message;
            ErrorInfoBar.IsOpen = true;
        }
    }
}

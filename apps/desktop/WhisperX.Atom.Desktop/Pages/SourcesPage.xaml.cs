using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class SourcesPage : Page
{
    private SourcesViewModel? _viewModel;
    private CancellationTokenSource? _pageCts;

    public SourcesPage()
    {
        InitializeComponent();
        SizeChanged += SourcesPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FrontendServices services) return;
        _pageCts = new CancellationTokenSource();
        _viewModel = new SourcesViewModel(services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        await RefreshAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnNavigatedFrom(e);
    }

    private async Task RefreshAsync()
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            await _viewModel.RefreshAsync(_pageCts.Token);
            UpdateState();
        }
        catch (OperationCanceledException) { }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        RefreshButton.IsEnabled = !_viewModel.IsLoading;
        AgentStatusDot.Fill = StatusBrush(_viewModel.AgentAvailable, _viewModel.IsLoading);
        ApiStatusDot.Fill = StatusBrush(_viewModel.ApiAvailable, _viewModel.IsLoading);
        MicrophoneList.Visibility = _viewModel.HasMicrophones ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneEmptyText.Visibility = _viewModel.HasMicrophones ? Visibility.Collapsed : Visibility.Visible;
        SystemAudioList.Visibility = _viewModel.HasSystemAudioDevices ? Visibility.Visible : Visibility.Collapsed;
        SystemAudioEmptyText.Visibility = _viewModel.HasSystemAudioDevices ? Visibility.Collapsed : Visibility.Visible;
        AgentsList.Visibility = _viewModel.HasRegisteredAgents ? Visibility.Visible : Visibility.Collapsed;
        AgentsEmptyState.Visibility = _viewModel.HasRegisteredAgents ? Visibility.Collapsed : Visibility.Visible;
        ErrorInfoBar.Message = _viewModel.ErrorText;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        WarningInfoBar.Message = _viewModel.WarningText;
        WarningInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.WarningText);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OpenRecordingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("recording");
    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");

    private void SourcesPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = ResponsiveLayout.IsWide(e.NewSize.Width);
        SourcesActionsPanel.Orientation = wide ? Orientation.Horizontal : Orientation.Vertical;
        ApplyResponsiveLayout(StatusGrid, LocalAgentCard, BackendCard, wide);
        ApplyResponsiveLayout(DevicesGrid, MicrophoneCard, SystemAudioCard, wide);
    }

    private static Brush StatusBrush(bool available, bool loading)
    {
        var key = loading ? "NeutralStatusBrush" : available ? "SuccessBrush" : "DangerBrush";
        return (Brush)Application.Current.Resources[key];
    }

    private static void ApplyResponsiveLayout(Grid grid, FrameworkElement first, FrameworkElement second, bool wide)
    {
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        grid.RowDefinitions[0].Height = GridLength.Auto;
        grid.RowDefinitions[1].Height = wide ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(first, 0);
        Grid.SetRow(first, 0);
        Grid.SetColumn(second, wide ? 1 : 0);
        Grid.SetRow(second, wide ? 0 : 1);
    }
}

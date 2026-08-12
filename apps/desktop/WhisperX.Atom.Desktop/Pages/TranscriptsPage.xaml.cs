using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class TranscriptsPage : Page
{
    private TranscriptsViewModel? _viewModel;
    private CancellationTokenSource? _pageCts;
    private bool _updatingLayout;

    public TranscriptsPage()
    {
        InitializeComponent();
        SizeChanged += TranscriptsPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FrontendServices services) return;
        _pageCts = new CancellationTokenSource();
        _viewModel = new TranscriptsViewModel(services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        try
        {
            await _viewModel.LoadAsync(_pageCts.Token);
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось загрузить стенограммы.");
            ErrorInfoBar.IsOpen = true;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranscriptsViewModel.SelectedItem) or nameof(TranscriptsViewModel.SelectedSegment))
            UpdateDetails();
        UpdateState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            await _viewModel.LoadAsync(_pageCts.Token);
            TranscriptsList.SelectedItem = null;
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось открыть стенограмму.");
            ErrorInfoBar.IsOpen = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
        UpdateState();
    }

    private void SegmentSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SegmentSearchText = SegmentSearchBox.Text;
        UpdateDetails();
    }

    private async void TranscriptsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null || TranscriptsList.SelectedItem is not TranscriptRegistryItem item) return;
        try { await _viewModel.LoadSelectedAsync(item, _pageCts.Token); UpdateDetails(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorInfoBar.Message = UiErrorFormatter.Format(ex); ErrorInfoBar.IsOpen = true; }
    }

    private void SegmentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SelectedSegment = SegmentsList.SelectedItem as WhisperX.Atom.Desktop.DesktopTranscriptSegment;
        UpdateDetails();
    }

    private void OpenMeetingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");

    private void OpenSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem is not { Meeting.Id: var meetingId } item || _viewModel.SelectedSegment is not { } segment)
            return;
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(meetingId, segment.Id, segment.StartMs));
    }

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        RefreshButton.IsEnabled = !_viewModel.IsLoading;
        TranscriptsList.Visibility = _viewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _viewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = !string.IsNullOrWhiteSpace(_viewModel.ErrorText) ? "Не удалось загрузить стенограммы" : "Стенограмм нет";
        EmptyDescription.Text = !string.IsNullOrWhiteSpace(_viewModel.ErrorText)
            ? _viewModel.ErrorText
            : _viewModel.StatusText;
        ErrorInfoBar.Message = _viewModel.ErrorText;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        WarningInfoBar.Message = _viewModel.WarningText;
        WarningInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.WarningText);
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_viewModel is null) return;
        var hasSelection = _viewModel.HasSelection;
        TranscriptDetails.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        DetailsEmptyText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        SegmentsList.Visibility = _viewModel.HasSegments ? Visibility.Visible : Visibility.Collapsed;
        SegmentEmptyText.Visibility = hasSelection && !_viewModel.HasSegments ? Visibility.Visible : Visibility.Collapsed;
        OpenSegmentButton.IsEnabled = _viewModel.HasSegmentSelection;
        SelectedSegmentText.Text = _viewModel.SelectedSegment is { } segment
            ? $"{segment.TimeLabel} · {segment.Speaker ?? "Спикер не определён"}\n{segment.Text}"
            : string.Empty;
    }

    private void TranscriptsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            var mode = ResponsiveLayout.GetMode(e.NewSize.Width);
            ActionsPanel.Orientation = mode == PageLayoutMode.Compact ? Orientation.Vertical : Orientation.Horizontal;
            var compact = mode != PageLayoutMode.Wide;
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(390);
            WorkspaceGrid.RowDefinitions[0].Height = compact ? new GridLength(430) : new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(ListCard, 0);
            Grid.SetRow(ListCard, 0);
            Grid.SetColumn(DetailsCard, compact ? 0 : 1);
            Grid.SetRow(DetailsCard, compact ? 1 : 0);
        }
        finally { _updatingLayout = false; }
    }
}

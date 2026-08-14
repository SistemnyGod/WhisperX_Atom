using System.ComponentModel;
using Microsoft.UI.Xaml.Automation;
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
    private CancellationTokenSource? _detailCts;
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
        CancelDetailLoad();
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
        CancelDetailLoad();
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

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus(FocusState.Programmatic);
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
        CancelDetailLoad();
        _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        var detailCts = _detailCts;
        _viewModel.SelectedItem = item;
        UpdateDetails();
        try { await _viewModel.LoadSelectedAsync(item, detailCts.Token); UpdateDetails(); }
        catch (OperationCanceledException) when (detailCts.IsCancellationRequested) { }
        catch (Exception ex) { ErrorInfoBar.Message = UiErrorFormatter.Format(ex); ErrorInfoBar.IsOpen = true; }
        finally
        {
            if (ReferenceEquals(_detailCts, detailCts))
            {
                _detailCts.Dispose();
                _detailCts = null;
            }
        }
    }

    private void SegmentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SelectedSegment = SegmentsList.SelectedItem as WhisperX.Atom.Desktop.DesktopTranscriptSegment;
        UpdateDetails();
    }

    private void OpenMeetingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");

    private void OpenSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem is not { Meeting.Id: var meetingId } item || _viewModel.SelectedSegment is not { } segment)
            return;
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(meetingId, segment.Id, segment.StartMs));
    }

    private void CancelDetailLoad()
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;
    }

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        RefreshButton.IsEnabled = !_viewModel.IsLoading;
        AutomationProperties.SetName(RefreshButton, _viewModel.IsLoading ? "Загрузка стенограмм" : "Обновить стенограммы");
        TranscriptsList.Visibility = _viewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _viewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        var hasError = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        EmptyTitle.Text = hasError ? "Не удалось загрузить стенограммы" : "Стенограмм нет";
        var hasSearchNoResults = _viewModel.HasSearchNoResults;
        if (hasError)
        {
            EmptyTitle.Text = "Не удалось загрузить стенограммы";
            EmptyDescription.Text = _viewModel.ErrorText;
        }
        else if (hasSearchNoResults)
        {
            EmptyTitle.Text = "Ничего не найдено";
            EmptyDescription.Text = $"По запросу «{_viewModel.SearchText.Trim()}» совпадений нет.";
        }
        else
        {
            EmptyTitle.Text = "Стенограмм нет";
            EmptyDescription.Text = _viewModel.StatusText;
        }
        EmptyRetryButton.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        EmptyRetryButton.IsEnabled = !_viewModel.IsLoading;
        EmptyClearSearchButton.Visibility = hasSearchNoResults ? Visibility.Visible : Visibility.Collapsed;
        EmptyClearSearchButton.IsEnabled = !_viewModel.IsLoading;
        ErrorInfoBar.Message = _viewModel.ErrorText;
        ErrorInfoBar.IsOpen = hasError;
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
        SegmentEmptyText.Text = _viewModel.IsLoading
            ? "Загружаем сегменты стенограммы…"
            : string.IsNullOrWhiteSpace(_viewModel.SegmentSearchText)
                ? "Сегменты стенограммы не найдены."
                : "Сегментов с таким текстом не найдено.";
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

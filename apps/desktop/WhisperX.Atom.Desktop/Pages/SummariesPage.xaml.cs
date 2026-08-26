using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class SummariesPage : Page
{
    private SummariesViewModel? _viewModel;
    private CancellationTokenSource? _pageCts;
    private string? _pendingMeetingId;
    private bool _updatingLayout;
    private PageLayoutMode _layoutMode = PageLayoutMode.Wide;
    private bool _layoutInitialized;

    public SummariesPage()
    {
        InitializeComponent();
        SizeChanged += SummariesPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        FrontendServices? services = e.Parameter switch
        {
            SummaryNavigationRequest request => request.Services,
            FrontendServices directServices => directServices,
            _ => null
        };
        if (services is null) return;
        _pendingMeetingId = e.Parameter is SummaryNavigationRequest summaryRequest
            ? summaryRequest.Target.MeetingId
            : null;
        _pageCts = new CancellationTokenSource();
        _viewModel = new SummariesViewModel(services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        try
        {
            await _viewModel.LoadAsync(_pageCts.Token);
            if (!string.IsNullOrWhiteSpace(_pendingMeetingId))
                _viewModel.SelectMeeting(_pendingMeetingId);
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось загрузить саммари.");
            ErrorInfoBar.IsOpen = true;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
        _pendingMeetingId = null;
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SummariesViewModel.SelectedItem))
        {
            UpdateDetails();
            ApplySummaryLayout(ActualWidth, force: true);
        }
        else if (e.PropertyName == nameof(SummariesViewModel.IsRebuilding))
            UpdateDetails();
        UpdateState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            await _viewModel.LoadAsync(_pageCts.Token);
            SummariesList.SelectedItem = null;
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось пересобрать саммари.");
            ErrorInfoBar.IsOpen = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
        UpdateState();
    }

    private void SummariesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SelectedItem = SummariesList.SelectedItem as SummaryRegistryItem;
        UpdateDetails();
        ApplySummaryLayout(ActualWidth, force: true);
    }

    private async void RebuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            await _viewModel.RebuildSelectedAsync(_pageCts.Token);
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось открыть саммари.");
            ErrorInfoBar.IsOpen = true;
        }
    }

    private void OpenMeetingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");

    private void OpenMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem is not { Meeting.Id: var meetingId }) return;
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(meetingId));
    }

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        RefreshButton.IsEnabled = !_viewModel.IsLoading;
        var hasError = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        SummariesList.Visibility = _viewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _viewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = hasError ? "Не удалось загрузить саммари" : "Саммари не найдены";
        EmptyDescription.Text = hasError ? _viewModel.ErrorText : _viewModel.StatusText;
        EmptyRetryButton.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        EmptyRetryButton.IsEnabled = !_viewModel.IsLoading;
        ErrorInfoBar.Message = _viewModel.ErrorText;
        ErrorInfoBar.IsOpen = hasError;
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_viewModel is null) return;
        var item = _viewModel.SelectedItem;
        SummaryDetails.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        DetailsEmptyText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        RebuildButton.IsEnabled = _viewModel.CanRebuild;
        OpenMeetingButton.IsEnabled = item is not null;
        if (item is null)
        {
            DetailsTitle.Text = string.Empty;
            DetailsMeta.Text = string.Empty;
            DetailsStatusBadge.Text = string.Empty;
            DetailsStatusBadge.Status = string.Empty;
            SummaryText.Text = string.Empty;
            SummaryQuestionsItems.ItemsSource = null;
            SummaryTasksItems.ItemsSource = null;
            StructuredSummaryPanel.Visibility = Visibility.Collapsed;
            SummaryFallbackPanel.Visibility = Visibility.Visible;
            DetailsReviewText.Text = string.Empty;
            DetailsNotice.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsTitle.Text = item.MeetingTitle;
        DetailsMeta.Text = item.MetaText;
        DetailsStatusBadge.Text = item.StatusText;
        DetailsStatusBadge.Status = item.StatusCode;
        SummaryText.Text = item.SummaryText;
        var protocol = MeetingProtocolParser.Parse(item.Summary?.Content);
        var showStructured = protocol.IsProtocol && protocol.IsValid;
        StructuredSummaryPanel.Visibility = showStructured ? Visibility.Visible : Visibility.Collapsed;
        SummaryFallbackPanel.Visibility = showStructured ? Visibility.Collapsed : Visibility.Visible;
        SummaryQuestionsItems.ItemsSource = showStructured ? protocol.Questions : null;
        SummaryTasksItems.ItemsSource = showStructured ? protocol.Tasks : null;
        SummaryQuestionsEmptyText.Visibility = showStructured && protocol.Questions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryTasksEmptyText.Visibility = showStructured && protocol.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var notice = string.Join(" ", new[] { item.ReviewText, _viewModel.WarningText }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
        DetailsReviewText.Text = notice;
        DetailsNotice.Visibility = string.IsNullOrWhiteSpace(notice) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SummariesPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            ApplySummaryLayout(e.NewSize.Width);
        }
        finally { _updatingLayout = false; }
    }

    private void ApplySummaryLayout(double width, bool force = false)
    {
        if (WorkspaceGrid is null || _viewModel is null) return;
        var nextMode = ResponsiveLayout.GetMode(width);
        if (!force && _layoutInitialized && _layoutMode == nextMode) return;
        _layoutMode = nextMode;
        _layoutInitialized = true;
        var compact = _layoutMode == PageLayoutMode.Compact;
        var standard = _layoutMode == PageLayoutMode.Standard;
        var hasSelection = _viewModel.SelectedItem is not null;
        ActionsPanel.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;

        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        WorkspaceGrid.ColumnDefinitions[1].Width = _layoutMode == PageLayoutMode.Wide ? new GridLength(520) : new GridLength(0);
        WorkspaceGrid.RowDefinitions[0].Height = standard ? new GridLength(430) : new GridLength(1, GridUnitType.Star);
        WorkspaceGrid.RowDefinitions[1].Height = standard ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(ListCard, 0);
        Grid.SetRow(ListCard, 0);
        Grid.SetColumn(DetailsCard, _layoutMode == PageLayoutMode.Wide ? 1 : 0);
        Grid.SetRow(DetailsCard, standard ? 1 : 0);

        ListCard.Visibility = compact && hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DetailsCard.Visibility = compact && !hasSelection ? Visibility.Collapsed : Visibility.Visible;
        BackToSummaryListButton.Visibility = compact && hasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackToSummaryListButton_Click(object sender, RoutedEventArgs e)
    {
        SummariesList.SelectedItem = null;
        if (_viewModel is not null) _viewModel.SelectedItem = null;
        UpdateDetails();
        ApplySummaryLayout(ActualWidth, force: true);
    }
}

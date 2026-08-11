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
    private bool _updatingLayout;

    public SummariesPage()
    {
        InitializeComponent();
        SizeChanged += SummariesPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FrontendServices services) return;
        _pageCts = new CancellationTokenSource();
        _viewModel = new SummariesViewModel(services);
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
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось загрузить саммари.");
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
        if (e.PropertyName is nameof(SummariesViewModel.SelectedItem) or nameof(SummariesViewModel.IsRebuilding))
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
        SummariesList.Visibility = _viewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _viewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = !string.IsNullOrWhiteSpace(_viewModel.ErrorText) ? "Не удалось загрузить саммари" : "Саммари не найдены";
        EmptyDescription.Text = !string.IsNullOrWhiteSpace(_viewModel.ErrorText) ? _viewModel.ErrorText : _viewModel.StatusText;
        ErrorInfoBar.Message = _viewModel.ErrorText;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        WarningInfoBar.Message = _viewModel.WarningText;
        WarningInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.WarningText);
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_viewModel is null) return;
        var item = _viewModel.SelectedItem;
        DetailsCard.Visibility = Visibility.Visible;
        SummaryDetails.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        DetailsEmptyText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        RebuildButton.IsEnabled = _viewModel.CanRebuild;
        OpenMeetingButton.IsEnabled = item is not null;
        if (item is null)
        {
            DetailsTitle.Text = string.Empty;
            DetailsMeta.Text = string.Empty;
            DetailsStatus.Text = string.Empty;
            SummaryText.Text = string.Empty;
            DetailsReviewText.Text = string.Empty;
            DetailsReviewText.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsTitle.Text = item.MeetingTitle;
        DetailsMeta.Text = item.MetaText;
        DetailsStatus.Text = item.StatusText;
        SummaryText.Text = item.SummaryText;
        DetailsReviewText.Text = item.ReviewText;
        DetailsReviewText.Visibility = item.NeedsReview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SummariesPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            var mode = ResponsiveLayout.GetMode(e.NewSize.Width);
            ActionsPanel.Orientation = mode == PageLayoutMode.Compact ? Orientation.Vertical : Orientation.Horizontal;
            var compact = mode != PageLayoutMode.Wide;
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(430);
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

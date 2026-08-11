using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class SpeakersPage : Page
{
    private SpeakersViewModel? _viewModel;
    private CancellationTokenSource? _pageCts;
    private bool _updatingLayout;

    public SpeakersPage()
    {
        InitializeComponent();
        SizeChanged += SpeakersPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FrontendServices services) return;
        _pageCts = new CancellationTokenSource();
        _viewModel = new SpeakersViewModel(services);
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
            ErrorInfoBar.Message = ex.Message;
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
        if (e.PropertyName == nameof(SpeakersViewModel.SelectedItem)) UpdateDetails();
        UpdateState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            await _viewModel.LoadAsync(_pageCts.Token);
            SpeakersList.SelectedItem = null;
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = ex.Message;
            ErrorInfoBar.IsOpen = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
        UpdateState();
    }

    private void SpeakersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SelectedItem = SpeakersList.SelectedItem as SpeakerRegistryItem;
        UpdateDetails();
    }

    private void OpenMeetingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");

    private void OpenMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem is not { Meeting.Id: var meetingId } item) return;
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(meetingId));
    }

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        RefreshButton.IsEnabled = !_viewModel.IsLoading;
        SpeakersList.Visibility = _viewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _viewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = !string.IsNullOrWhiteSpace(_viewModel.ErrorText) ? "Не удалось загрузить спикеров" : "Спикеры не найдены";
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
        SpeakerDetails.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        DetailsEmptyText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        OpenMeetingButton.IsEnabled = item is not null;
        if (item is null) return;
        DetailsName.Text = item.DisplayName;
        DetailsKey.Text = item.StableKey;
        DetailsMeeting.Text = item.MeetingTitle;
        DetailsDate.Text = item.MeetingDateText;
    }

    private void SpeakersPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            var mode = ResponsiveLayout.GetMode(e.NewSize.Width);
            ActionsPanel.Orientation = mode == PageLayoutMode.Compact ? Orientation.Vertical : Orientation.Horizontal;
            var compact = mode != PageLayoutMode.Wide;
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(360);
            WorkspaceGrid.RowDefinitions[0].Height = compact ? new GridLength(400) : new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(ListCard, 0);
            Grid.SetRow(ListCard, 0);
            Grid.SetColumn(DetailsCard, compact ? 0 : 1);
            Grid.SetRow(DetailsCard, compact ? 1 : 0);

            SummaryGrid.ColumnDefinitions.Clear();
            SummaryGrid.RowDefinitions.Clear();
            var columns = mode == PageLayoutMode.Compact ? 1 : 2;
            for (var index = 0; index < columns; index++)
                SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < (int)Math.Ceiling(2d / columns); index++)
                SummaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var index = 0; index < 2; index++)
            {
                if (SummaryGrid.Children[index] is not FrameworkElement child) continue;
                Grid.SetColumn(child, index % columns);
                Grid.SetRow(child, index / columns);
            }
        }
        finally { _updatingLayout = false; }
    }
}

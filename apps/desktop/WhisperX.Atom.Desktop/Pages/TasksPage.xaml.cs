using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class TasksPage : Page
{
    private FrontendServices? _services;
    private TasksViewModel? _viewModel;
    private CancellationTokenSource? _pageCts;
    private bool _updatingEditor;
    private bool _updatingLayout;

    public TasksPage()
    {
        InitializeComponent();
        SizeChanged += TasksPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = e.Parameter as FrontendServices;
        if (_services is null) return;
        _pageCts = new CancellationTokenSource();
        _viewModel = new TasksViewModel(_services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        try
        {
            await _viewModel.LoadAsync(_pageCts.Token);
            RebuildMeetingFilter();
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось выполнить операцию с поручением.")); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TasksViewModel.IsLoading) or nameof(TasksViewModel.HasItems) or nameof(TasksViewModel.ErrorText) or nameof(TasksViewModel.WarningText) or nameof(TasksViewModel.StatusText) or nameof(TasksViewModel.HasSelection) or nameof(TasksViewModel.IsSaving))
            UpdateState();
        if (e.PropertyName == nameof(TasksViewModel.SelectedItem)) UpdateDetails();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text; UpdateState(); }
    private void ResponsibleFilterBox_TextChanged(object sender, TextChangedEventArgs e) { if (_viewModel is not null) _viewModel.ResponsibleFilter = ResponsibleFilterBox.Text; UpdateState(); }

    private void StatusFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && StatusFilterBox.SelectedItem is ComboBoxItem item) _viewModel.StatusFilter = item.Tag?.ToString() ?? "ACTIVE";
        UpdateState();
    }

    private void DeadlineFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && DeadlineFilterBox.SelectedItem is ComboBoxItem item) _viewModel.DeadlineFilter = item.Tag?.ToString() ?? "ALL";
        UpdateState();
    }

    private void MeetingFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && MeetingFilterBox.SelectedItem is ComboBoxItem item) _viewModel.MeetingFilterId = item.Tag?.ToString() ?? string.Empty;
        UpdateState();
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.SelectedItem = TaskList.SelectedItem as TaskRegistryItem;
        UpdateDetails();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            await _viewModel.LoadAsync(_pageCts.Token);
            RebuildMeetingFilter();
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось обновить поручение.")); }
    }

    private void TaskEditor_TextChanged(object sender, TextChangedEventArgs e) { if (!_updatingEditor && _viewModel is not null) _viewModel.EditTaskText = TaskEditor.Text; }
    private void ResponsibleEditor_TextChanged(object sender, TextChangedEventArgs e) { if (!_updatingEditor && _viewModel is not null) _viewModel.EditResponsible = ResponsibleEditor.Text; }
    private void DeadlinePicker_DateChanged(object sender, DatePickerValueChangedEventArgs args)
    {
        if (!_updatingEditor && _viewModel is not null && NoDeadlineCheckBox.IsChecked != true)
            _viewModel.EditDeadline = DeadlinePicker.Date;
    }

    private void NoDeadlineCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingEditor || _viewModel is null) return;
        var noDeadline = NoDeadlineCheckBox.IsChecked == true;
        DeadlinePicker.IsEnabled = !noDeadline;
        _viewModel.EditDeadline = noDeadline ? null : new DateTimeOffset(DeadlinePicker.Date.Date);
    }

    private void StatusEditor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEditor || _viewModel is null || StatusEditor.SelectedValue is not string status) return;
        _viewModel.EditStatus = status;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            if (!await _viewModel.SaveSelectedAsync(_pageCts.Token)) return;
            UpdateDetails();
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось загрузить свидетельство.")); }
    }

    private void EvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        var item = _viewModel?.SelectedItem;
        if (item is null || item.EvidenceSegmentId is not Guid segmentId) return;
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(item.Meeting.Id, segmentId.ToString()));
    }

    private void RebuildMeetingFilter()
    {
        MeetingFilterBox.Items.Clear();
        MeetingFilterBox.Items.Add(new ComboBoxItem { Content = "Все встречи", Tag = string.Empty, IsSelected = true });
        if (_viewModel is null) return;
        foreach (var meeting in _viewModel.Meetings.OrderByDescending(item => item.CreatedAt))
            MeetingFilterBox.Items.Add(new ComboBoxItem { Content = meeting.Title, Tag = meeting.Id });
    }

    private void UpdateDetails()
    {
        if (_viewModel is null) return;
        var item = _viewModel.SelectedItem;
        _updatingEditor = true;
        try
        {
            DetailsMeetingText.Text = item is null ? string.Empty : $"{item.MeetingTitle} · {item.MeetingDateText}";
            TaskEditor.Text = item?.Task.Task ?? string.Empty;
            ResponsibleEditor.Text = item?.Task.Responsible ?? string.Empty;
            DeadlinePicker.Date = item?.Task.Deadline is DateTime deadline ? new DateTimeOffset(deadline) : DateTimeOffset.Now;
            NoDeadlineCheckBox.IsChecked = item?.Task.Deadline is null;
            DeadlinePicker.IsEnabled = item?.Task.Deadline is not null;
            StatusEditor.SelectedValue = item?.Task.Status;
            SaveButton.IsEnabled = item is not null && !_viewModel.IsSaving;
            EvidenceButton.Visibility = item?.EvidenceSegmentId is Guid ? Visibility.Visible : Visibility.Collapsed;
            DetailsEmptyText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingEditor = false; }
    }

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        TaskList.Visibility = _viewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _viewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _viewModel.ErrorText.Length > 0 ? "Не удалось загрузить поручения" : "Поручений нет";
        EmptyDescription.Text = string.IsNullOrWhiteSpace(_viewModel.ErrorText) ? _viewModel.StatusText : _viewModel.ErrorText;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        ErrorInfoBar.Message = _viewModel.ErrorText;
        WarningInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.WarningText);
        WarningInfoBar.Message = _viewModel.WarningText;
        RefreshButton.IsEnabled = !_viewModel.IsLoading;
        SaveButton.IsEnabled = _viewModel.HasSelection && !_viewModel.IsSaving;
    }

    private void TasksPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            ApplyFilterLayout(e.NewSize.Width);
            var compact = !ResponsiveLayout.IsWide(e.NewSize.Width);
            TasksGrid.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            TasksGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(360);
            TasksGrid.RowDefinitions[0].Height = compact ? new GridLength(330) : new GridLength(1, GridUnitType.Star);
            TasksGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(ListCard, 0);
            Grid.SetRow(ListCard, 0);
            Grid.SetColumn(DetailsCard, compact ? 0 : 1);
            Grid.SetRow(DetailsCard, compact ? 1 : 0);
            DetailsCard.Visibility = Visibility.Visible;
        }
        finally { _updatingLayout = false; }
    }

    private void ApplyFilterLayout(double width)
    {
        var mode = ResponsiveLayout.GetMode(width);
        FiltersGrid.ColumnDefinitions.Clear();
        FiltersGrid.RowDefinitions.Clear();

        if (mode == PageLayoutMode.Wide)
        {
            FiltersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            for (var index = 0; index < 3; index++) FiltersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FiltersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FiltersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PlaceFilter(SearchBox, 0, 0);
            PlaceFilter(StatusFilterBox, 1, 0);
            PlaceFilter(DeadlineFilterBox, 2, 0);
            PlaceFilter(MeetingFilterBox, 3, 0);
            PlaceFilter(ResponsibleFilterBox, 4, 0);
            return;
        }

        if (mode == PageLayoutMode.Standard)
        {
            for (var index = 0; index < 3; index++) FiltersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < 2; index++) FiltersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PlaceFilter(SearchBox, 0, 0, 2);
            PlaceFilter(StatusFilterBox, 2, 0);
            PlaceFilter(DeadlineFilterBox, 0, 1);
            PlaceFilter(MeetingFilterBox, 1, 1);
            PlaceFilter(ResponsibleFilterBox, 2, 1);
            return;
        }

        FiltersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 5; index++) FiltersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        PlaceFilter(SearchBox, 0, 0);
        PlaceFilter(StatusFilterBox, 0, 1);
        PlaceFilter(DeadlineFilterBox, 0, 2);
        PlaceFilter(MeetingFilterBox, 0, 3);
        PlaceFilter(ResponsibleFilterBox, 0, 4);
    }

    private static void PlaceFilter(FrameworkElement element, int column, int row, int columnSpan = 1)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}

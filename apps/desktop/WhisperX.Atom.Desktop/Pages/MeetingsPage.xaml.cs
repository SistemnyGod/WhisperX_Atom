using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class MeetingsPage : Page
{
    private FrontendServices? _services;
    private CancellationTokenSource? _pageCts;
    private CancellationTokenSource? _workspaceCts;
    private MeetingsViewModel? _viewModel;
    private MeetingWorkspaceViewModel? _workspace;
    private bool _updatingTaskStatus;
    private bool _updatingLayout;
    private bool _suppressMeetingSelection;
    private MeetingNavigationTarget? _pendingTarget;

    public MeetingsPage()
    {
        InitializeComponent();
        SizeChanged += MeetingsPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MeetingNavigationRequest request)
        {
            _services = request.Services;
            _pendingTarget = request.Target;
            _services.Navigation.PendingMeetingTarget = request.Target;
        }
        else
        {
            _services = (FrontendServices)e.Parameter;
            _pendingTarget = _services.Navigation.PendingMeetingTarget;
        }
        _pageCts = new CancellationTokenSource();
        _viewModel = new MeetingsViewModel(_services);
        _workspace = new MeetingWorkspaceViewModel(_services);
        DataContext = _viewModel;
        WorkspaceCard.DataContext = _workspace;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _workspace.PropertyChanged += Workspace_PropertyChanged;
        try
        {
            await _viewModel.RefreshAsync(_pageCts.Token);
            UpdateListState();
            UpdateWorkspaceState();
            if (_pendingTarget is not null)
            {
                var targetMeeting = _viewModel.FilteredMeetings.FirstOrDefault(item => item.Id == _pendingTarget.MeetingId);
                if (targetMeeting is not null)
                {
                    _suppressMeetingSelection = true;
                    MeetingsList.SelectedItem = targetMeeting;
                    _suppressMeetingSelection = false;
                    await LoadSelectedMeetingAsync(targetMeeting);
                    await ApplyPendingTargetAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
        _workspaceCts?.Cancel();
        _workspaceCts?.Dispose();
        _workspaceCts = null;
        _workspace?.ClearSelection();
        PreviewPlayer.Source = null;
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MeetingsViewModel.HasMeetings) or nameof(MeetingsViewModel.HasFilteredMeetings) or nameof(MeetingsViewModel.StatusText) or nameof(MeetingsViewModel.ErrorText) or nameof(MeetingsViewModel.IsLoading))
            UpdateListState();
    }

    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MeetingWorkspaceViewModel.HasMeeting) or nameof(MeetingWorkspaceViewModel.IsLoading) or nameof(MeetingWorkspaceViewModel.ErrorText) or nameof(MeetingWorkspaceViewModel.CanRetryLatestJob))
            UpdateWorkspaceState();
        if (e.PropertyName is nameof(MeetingWorkspaceViewModel.PipelineText) or nameof(MeetingWorkspaceViewModel.DurationText) or nameof(MeetingWorkspaceViewModel.SummaryText) or nameof(MeetingWorkspaceViewModel.SummaryMetaText))
            UpdateWorkspaceText();
    }

    private async void MeetingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMeetingSelection || _viewModel is null || _workspace is null || _pageCts is null) return;
        _viewModel.SelectedMeeting = MeetingsList.SelectedItem as DesktopMeeting;
        if (_viewModel.SelectedMeeting is null)
        {
            _workspaceCts?.Cancel();
            _workspace.ClearSelection();
            PreviewPlayer.Source = null;
            UpdateWorkspaceState();
            UpdateWorkspaceText();
            return;
        }
        await LoadSelectedMeetingAsync(_viewModel.SelectedMeeting);
    }

    private async Task LoadSelectedMeetingAsync(DesktopMeeting meeting)
    {
        if (_viewModel is null || _workspace is null || _pageCts is null) return;
        _viewModel.SelectedMeeting = meeting;
        _workspaceCts?.Cancel();
        _workspaceCts?.Dispose();
        _workspaceCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        try
        {
            await _workspace.LoadAsync(meeting, _workspaceCts.Token);
            UpdateWorkspaceState();
            UpdateWorkspaceText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task ApplyPendingTargetAsync()
    {
        if (_pendingTarget is null || _workspace is null || _pageCts is null) return;
        WorkspaceTabs.SelectedIndex = 1;
        if (!string.IsNullOrWhiteSpace(_pendingTarget.SegmentId))
        {
            var segment = _workspace.TranscriptSegments.FirstOrDefault(item => item.Id == _pendingTarget.SegmentId);
            if (segment is not null) TranscriptList.SelectedItem = segment;
        }

        var startMs = _pendingTarget.StartMs;
        if (startMs is null && !string.IsNullOrWhiteSpace(_pendingTarget.SegmentId))
            startMs = _workspace.TranscriptSegments.FirstOrDefault(item => item.Id == _pendingTarget.SegmentId)?.StartMs;
        if (startMs is null) return;

        if (PreviewPlayer.Source is null)
        {
            var media = _workspace.Media.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PreviewStorageKey));
            if (media is not null)
            {
                var path = await _workspace.LoadPreviewAsync(media, _pageCts.Token);
                if (!string.IsNullOrWhiteSpace(path)) PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            }
        }
        if (PreviewPlayer.MediaPlayer is not null)
            PreviewPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromMilliseconds(Math.Max(0, startMs.Value));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
        UpdateListState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            _workspaceCts?.Cancel();
            _workspace?.ClearSelection();
            MeetingsList.SelectedItem = null;
            PreviewPlayer.Source = null;
            await _viewModel.RefreshAsync(_pageCts.Token);
            UpdateListState();
            UpdateWorkspaceState();
            UpdateWorkspaceText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null || _viewModel is null || _pageCts is null) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
        foreach (var extension in new[] { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".mp4", ".mkv", ".mov", ".webm", ".avi" })
            picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            ErrorInfoBar.IsOpen = false;
            await _services.Backend.ImportFileAsync(file.Path, cancellationToken: _pageCts.Token);
            await _viewModel.RefreshAsync(_pageCts.Token);
            UpdateListState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void RefreshWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedMeeting is null || _workspace is null || _pageCts is null) return;
        _workspaceCts?.Cancel();
        _workspaceCts?.Dispose();
        _workspaceCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        try
        {
            await _workspace.LoadAsync(_viewModel.SelectedMeeting, _workspaceCts.Token);
            UpdateWorkspaceState();
            UpdateWorkspaceText();
        }
        catch (OperationCanceledException) { }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _pageCts is null) return;
        try
        {
            await _workspace.RetryLatestJobAsync(_pageCts.Token);
            UpdateWorkspaceState();
            UpdateWorkspaceText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void RebuildSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _pageCts is null) return;
        try
        {
            if (!await _workspace.RebuildSummaryAsync(_pageCts.Token)) ShowError("API не принял запрос на пересборку саммари.");
            UpdateWorkspaceText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void TaskStatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTaskStatus || sender is not ComboBox combo || combo.DataContext is not DesktopTask task || combo.SelectedValue is not string status || status.Equals(task.Status, StringComparison.OrdinalIgnoreCase) || _workspace is null || _pageCts is null)
            return;
        try
        {
            _updatingTaskStatus = true;
            var updated = task with { Status = status };
            if (!await _workspace.UpdateTaskAsync(updated, _pageCts.Token)) ShowError("Не удалось обновить статус поручения.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _updatingTaskStatus = false; }
    }

    private async void OpenPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DesktopMedia media } || _workspace is null || _pageCts is null) return;
        if (string.IsNullOrWhiteSpace(media.PreviewStorageKey))
        {
            PreviewStatusText.Text = "Preview пока недоступен для этого файла.";
            return;
        }
        try
        {
            PreviewStatusText.Text = "Загрузка preview…";
            var path = await _workspace.LoadPreviewAsync(media, _pageCts.Token);
            if (string.IsNullOrWhiteSpace(path))
            {
                PreviewPlayer.Source = null;
                PreviewStatusText.Text = "Preview пока недоступен.";
                return;
            }
            PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            PreviewStatusText.Text = path;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewStatusText.Text = ex.Message; }
    }

    private void TranscriptSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_workspace is null) return;
        var query = TranscriptSearchBox.Text.Trim();
        IReadOnlyList<DesktopTranscriptSegment> visible = string.IsNullOrWhiteSpace(query)
            ? _workspace.TranscriptSegments
            : _workspace.TranscriptSegments.Where(segment => segment.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        TranscriptList.ItemsSource = visible;
    }

    private void TranscriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptList.SelectedItem is not DesktopTranscriptSegment segment || PreviewPlayer.MediaPlayer is null) return;
        PreviewPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromMilliseconds(segment.StartMs);
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_workspace is null) return;
        UpdateEmptyStates();
    }

    private void MeetingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            var compact = !ResponsiveLayout.IsWide(e.NewSize.Width);
            MeetingsActionsPanel.Orientation = ResponsiveLayout.IsWide(e.NewSize.Width)
                ? Orientation.Horizontal
                : Orientation.Vertical;
            MeetingsGrid.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(340);
            MeetingsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            MeetingsGrid.RowDefinitions[0].Height = compact ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
            MeetingsGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(ListCard, 0);
            Grid.SetRow(ListCard, 0);
            Grid.SetColumn(WorkspaceCard, compact ? 0 : 1);
            Grid.SetRow(WorkspaceCard, compact ? 1 : 0);
            WorkspaceCard.Visibility = Visibility.Visible;
        }
        finally { _updatingLayout = false; }
    }

    private void UpdateListState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        MeetingsList.Visibility = _viewModel.HasFilteredMeetings ? Visibility.Visible : Visibility.Collapsed;
        ListEmptyState.Visibility = _viewModel.HasFilteredMeetings ? Visibility.Collapsed : Visibility.Visible;
        ListEmptyTitle.Text = _viewModel.HasMeetings ? "Ничего не найдено" : "Совещаний нет";
        ListEmptyDescription.Text = string.IsNullOrWhiteSpace(_viewModel.ErrorText) ? _viewModel.StatusText : _viewModel.ErrorText;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        ErrorInfoBar.Message = _viewModel.ErrorText;
    }

    private void UpdateWorkspaceState()
    {
        if (_workspace is null) return;
        WorkspaceEmptyState.Visibility = _workspace.HasMeeting ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceContent.Visibility = _workspace.HasMeeting ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceLoadingOverlay.Visibility = _workspace.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        RefreshWorkspaceButton.IsEnabled = _workspace.HasMeeting && !_workspace.IsLoading;
        RetryButton.IsEnabled = _workspace.CanRetryLatestJob && !_workspace.IsLoading;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_workspace.ErrorText);
        ErrorInfoBar.Message = _workspace.ErrorText;
        UpdateEmptyStates();
    }

    private void UpdateWorkspaceText()
    {
        if (_workspace is null) return;
        WorkspaceTitle.Text = _workspace.Meeting?.Title ?? "Выберите встречу слева";
        WorkspaceMeta.Text = _workspace.Meeting is null ? string.Empty : $"{_workspace.MeetingDateText} · {_workspace.Meeting.Status}";
        WorkspaceStatusText.Text = _workspace.Meeting is null ? "Ожидает выбора встречи" : _workspace.IsLoading ? "Загрузка данных встречи" : _workspace.PipelineText;
        PipelineText.Text = _workspace.PipelineText;
        OverviewPipelineText.Text = _workspace.PipelineText;
        DurationText.Text = _workspace.DurationText;
        MediaCountText.Text = _workspace.Media.Count.ToString();
        SummaryText.Text = _workspace.SummaryText;
        SummaryMetaText.Text = _workspace.SummaryMetaText;
        if (string.IsNullOrWhiteSpace(TranscriptSearchBox.Text)) TranscriptList.ItemsSource = _workspace.TranscriptSegments;
    }

    private void UpdateEmptyStates()
    {
        if (_workspace is null) return;
        OverviewEmptyText.Visibility = _workspace.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SpeakersEmptyText.Visibility = _workspace.Speakers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DecisionsEmptyText.Visibility = _workspace.Decisions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksEmptyText.Visibility = _workspace.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaEmptyText.Visibility = _workspace.Media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}

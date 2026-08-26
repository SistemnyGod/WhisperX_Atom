using System.ComponentModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
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
    private bool _workspaceExpanded;
    private MeetingNavigationTarget? _pendingTarget;
    private IReadOnlyList<DesktopTranscriptSegment> _searchMatches = [];
    private int _searchMatchIndex = -1;
    private bool _suppressSegmentSeek;
    private bool _transcriptPlaybackSubscribed;
    private bool _hideTechnicalEvents = true;
    private string? _lastLayoutKey;

    public MeetingsPage()
    {
        InitializeComponent();
        SizeChanged += MeetingsPage_SizeChanged;
        KeyDown += MeetingsPage_KeyDown;
        var searchAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.F,
            Modifiers = VirtualKeyModifiers.Control
        };
        searchAccelerator.Invoked += (_, _) => SearchBox.Focus(FocusState.Keyboard);
        KeyboardAccelerators.Add(searchAccelerator);
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
            try
            {
                var currentUser = await _services.Backend.GetCurrentUserAsync(_pageCts.Token);
                RepairPipelineButton.Visibility = currentUser?.IsPrivileged == true ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception)
            {
                RepairPipelineButton.Visibility = Visibility.Collapsed;
            }
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
                    _workspaceExpanded = true;
                    await LoadSelectedMeetingAsync(targetMeeting);
                    await ApplyPendingTargetAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
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
        TranscriptPlayer.Source = null;
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
        if (e.PropertyName is nameof(MeetingWorkspaceViewModel.PipelineText) or nameof(MeetingWorkspaceViewModel.DurationText) or nameof(MeetingWorkspaceViewModel.SummaryText) or nameof(MeetingWorkspaceViewModel.SummaryMetaText) or nameof(MeetingWorkspaceViewModel.TranscriptMetaText) or nameof(MeetingWorkspaceViewModel.TranscriptQualityText) or nameof(MeetingWorkspaceViewModel.TranscriptWarningText))
            UpdateWorkspaceText();
    }

    private async void MeetingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMeetingSelection || _viewModel is null || _workspace is null || _pageCts is null) return;
        _viewModel.SelectedMeeting = MeetingsList.SelectedItem as DesktopMeeting;
        if (_viewModel.SelectedMeeting is null)
        {
            _workspaceExpanded = false;
            _workspaceCts?.Cancel();
            _workspace.ClearSelection();
            PreviewPlayer.Source = null;
            TranscriptPlayer.Source = null;
            UpdateWorkspaceState();
            UpdateWorkspaceText();
            return;
        }
        _workspaceExpanded = false;
        await LoadSelectedMeetingAsync(_viewModel.SelectedMeeting);
    }

    private async Task LoadSelectedMeetingAsync(DesktopMeeting meeting)
    {
        if (_viewModel is null || _workspace is null || _pageCts is null) return;
        _viewModel.SelectedMeeting = meeting;
        TranscriptSearchBox.Text = string.Empty;
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
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
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
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var source = MediaSource.CreateFromUri(new Uri(path));
                    PreviewPlayer.Source = source;
                    TranscriptPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
                    AttachTranscriptPlayback();
                }
            }
        }
        var player = TranscriptPlayer.MediaPlayer ?? PreviewPlayer.MediaPlayer;
        if (player is not null) player.PlaybackSession.Position = TimeSpan.FromMilliseconds(Math.Max(0, startMs.Value));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
        UpdateListState();
    }

    private void MeetingsPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _workspaceExpanded)
        {
            _workspaceExpanded = false;
            UpdateWorkspaceState();
            e.Handled = true;
        }
    }

    private void OpenWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || !_workspace.HasMeeting) return;
        _workspaceExpanded = !_workspaceExpanded;
        UpdateWorkspaceState();
    }

    private void StartRecordingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("recording");

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");

    private void StatusFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && StatusFilterCombo.SelectedItem is string value)
            _viewModel.StatusFilter = value;
        UpdateListState();
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        SearchBox.Text = string.Empty;
        StatusFilterCombo.SelectedItem = "Все статусы";
        _viewModel.SearchText = string.Empty;
        _viewModel.StatusFilter = "Все статусы";
        UpdateListState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            _workspaceCts?.Cancel();
            _workspace?.ClearSelection();
            _workspaceExpanded = false;
            MeetingsList.SelectedItem = null;
            PreviewPlayer.Source = null;
            TranscriptPlayer.Source = null;
            await _viewModel.RefreshAsync(_pageCts.Token);
            UpdateListState();
            UpdateWorkspaceState();
            UpdateWorkspaceText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
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
            await ImportFilesAsync(new[] { file });
            await _viewModel.RefreshAsync(_pageCts.Token);
            UpdateListState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
    }

    private void ImportDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Импортировать файлы";
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void ImportDropZone_Drop(object sender, DragEventArgs e)
    {
        if (_services is null || _viewModel is null || _pageCts is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>().ToList();
            if (files.Count == 0) return;
            ErrorInfoBar.IsOpen = false;
            await ImportFilesAsync(files);
            await _viewModel.RefreshAsync(_pageCts.Token);
            UpdateListState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось импортировать перетащенные файлы.")); }
    }

    private async Task ImportFilesAsync(IEnumerable<StorageFile> files)
    {
        if (_services is null || _pageCts is null) return;
        var batch = files.ToList();
        if (batch.Count == 0) return;
        ImportProgressPanel.Visibility = Visibility.Visible;
        foreach (var file in batch)
        {
            var notificationKey = $"import:{Guid.NewGuid():N}";
            _services.Notifications.Publish(
                $"{notificationKey}:started",
                "Загрузка файла",
                $"{file.Name}: передаём файл на сервер.");
            try
            {
                var progress = new Progress<DesktopImportProgress>(state =>
                {
                    UpdateImportProgress(state);
                    if (state.Stage.Equals("PROCESSING", StringComparison.OrdinalIgnoreCase))
                        _services.Notifications.Publish(
                            $"{notificationKey}:uploaded",
                            "Файл загружен",
                            $"{file.Name}: сервер принял файл и запускает обработку.",
                            TransientNotificationSeverity.Success);
                });
                await _services.Backend.ImportFileWithProgressAsync(
                    file.Path,
                    Path.GetFileNameWithoutExtension(file.Name),
                    progress,
                    _pageCts.Token);
            }
            catch
            {
                _services.Notifications.Publish(
                    $"{notificationKey}:failed",
                    "Загрузка не завершена",
                    $"{file.Name}: не удалось передать файл на сервер.",
                    TransientNotificationSeverity.Error);
                throw;
            }
        }

        ImportProgressText.Text = batch.Count == 1
            ? "Файл принят. Аудио будет подготовлено и отправлено на распознавание."
            : $"Файлы приняты: {batch.Count}. Обработка выполняется в фоне.";
        ImportProgressPercentText.Text = "100%";
        ImportProgressBar.Value = 100;
    }

    private void UpdateImportProgress(DesktopImportProgress progress)
    {
        ImportProgressPanel.Visibility = Visibility.Visible;
        var percent = (int)Math.Round(progress.Fraction * 100, MidpointRounding.AwayFromZero);
        ImportProgressBar.Value = percent;
        ImportProgressPercentText.Text = $"{percent}%";
        ImportProgressText.Text = progress.Stage switch
        {
            "CREATING" => progress.Message ?? "Создаём карточку совещания…",
            "RESUMING" => progress.Message ?? "Продолжаем загрузку…",
            "UPLOADING" => $"Загрузка: {FormatImportBytes(progress.UploadedBytes)} из {FormatImportBytes(progress.TotalBytes)}",
            "PROCESSING" => progress.Message ?? "Файл загружен. Сервер готовит аудио…",
            _ => progress.Message ?? "Подготавливаем импорт…"
        };
    }

    private static string FormatImportBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var value = Math.Max(0, (double)bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return index == 0 ? $"{value:0} {units[index]}" : $"{value:0.0} {units[index]}";
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
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
    }

    private async void CancelProcessingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace?.Meeting is not { } meeting || _services is null || _pageCts is null || !Guid.TryParse(meeting.Id, out var meetingId)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Отменить обработку?",
            Content = "Новые этапы обработки и сообщения очереди будут отменены. Уже готовые результаты останутся в реестре.",
            PrimaryButtonText = "Отменить обработку",
            CloseButtonText = "Не отменять",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (await _services.Backend.CancelMeetingAsync(meetingId, _pageCts.Token) is null)
            {
                ShowError("Не удалось отменить обработку. Сначала завершите активную запись.");
                return;
            }
            await ReloadMeetingAfterChangeAsync(meetingId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
    }

    private async void DeleteMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace?.Meeting is not { } meeting || _services is null || _pageCts is null || !Guid.TryParse(meeting.Id, out var meetingId)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Удалить совещание?",
            Content = "Будут удалены запись совещания, связанные результаты и серверные медиафайлы. Локальный архив Recorder Agent не удаляется.",
            PrimaryButtonText = "Удалить навсегда",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (!await _services.Backend.DeleteMeetingAsync(meetingId, _pageCts.Token))
            {
                ShowError("Не удалось удалить совещание. Сначала завершите активную запись.");
                return;
            }
            _workspaceCts?.Cancel();
            _workspace.ClearSelection();
            _workspaceExpanded = false;
            PreviewPlayer.Source = null;
            _suppressMeetingSelection = true;
            MeetingsList.SelectedItem = null;
            _suppressMeetingSelection = false;
            await _viewModel!.RefreshAsync(_pageCts.Token);
            UpdateListState();
            UpdateWorkspaceState();
            UpdateWorkspaceText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
    }

    private async Task ReloadMeetingAfterChangeAsync(Guid meetingId)
    {
        if (_viewModel is null || _workspace is null || _pageCts is null) return;
        await _viewModel.RefreshAsync(_pageCts.Token);
        UpdateListState();
        var updated = _viewModel.FilteredMeetings.FirstOrDefault(item => string.Equals(item.Id, meetingId.ToString(), StringComparison.OrdinalIgnoreCase));
        if (updated is null)
        {
            _workspace.ClearSelection();
            _workspaceExpanded = false;
            UpdateWorkspaceState();
            UpdateWorkspaceText();
            return;
        }
        _suppressMeetingSelection = true;
        MeetingsList.SelectedItem = updated;
        _suppressMeetingSelection = false;
        await LoadSelectedMeetingAsync(updated);
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
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
    }

    private async void RepairPipelineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace?.Meeting is not { } meeting || _services is null || _pageCts is null || !Guid.TryParse(meeting.Id, out var meetingId)) return;
        try
        {
            var preview = await _services.Backend.RepairMeetingPipelineAsync(meetingId, "PREVIEW", _pageCts.Token);
            if (preview is null) { ShowError("Не удалось получить план восстановления. Требуются права администратора."); return; }
            var plan = string.Join(Environment.NewLine, preview.Actions.Select(item => $"{item.Stage}: {item.State}" + (string.IsNullOrWhiteSpace(item.Reason) ? string.Empty : $" — {item.Reason}")));
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Восстановить обработку?",
                Content = string.IsNullOrWhiteSpace(plan) ? "Изменения не требуются." : plan,
                PrimaryButtonText = "Применить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var applied = await _services.Backend.RepairMeetingPipelineAsync(meetingId, "APPLY", _pageCts.Token);
            if (applied is null) { ShowError("API не применил план восстановления."); return; }
            await ReloadMeetingAfterChangeAsync(meetingId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
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
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex)); }
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
            TranscriptPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            AttachTranscriptPlayback();
            PreviewStatusText.Text = path;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewStatusText.Text = UiErrorFormatter.Format(ex, "Не удалось загрузить preview."); }
    }

    private async void DownloadAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DesktopMedia media } || _workspace is null || _pageCts is null) return;
        await DownloadAudioAsync(media);
    }

    private async void DownloadOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DesktopMedia media } || _workspace is null || _pageCts is null) return;
        await DownloadOriginalAsync(media);
    }

    private async void DownloadPrimaryAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace?.Media.FirstOrDefault() is not { } media)
        {
            ShowError("Аудио пока недоступно: архив ещё не готов.");
            return;
        }
        await DownloadAudioAsync(media);
    }

    private async Task DownloadAudioAsync(DesktopMedia media)
    {
        if (_workspace is null || _pageCts is null) return;
        var extension = Path.GetExtension(media.ArchiveStorageKey);
        if (string.IsNullOrWhiteSpace(extension)) extension = Path.GetExtension(media.OriginalName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".flac";
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(media.OriginalName));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = SanitizeFileName(_workspace.Meeting?.Title ?? "meeting-audio");
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
                SuggestedFileName = baseName + extension
            };
            InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
            var label = extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ? "FLAC аудио" : "Аудиофайл";
            picker.FileTypeChoices.Add(label, new List<string> { extension });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            PreviewStatusText.Text = "Скачивание аудио…";
            var downloaded = await _workspace.DownloadMediaAsync(media, file.Path, _pageCts.Token);
            PreviewStatusText.Text = downloaded
                ? $"Аудио сохранено: {file.Path}"
                : "Аудио пока недоступно: архив ещё не готов.";
            if (!downloaded) ShowError("Сервер ещё не подготовил архив аудиозаписи. Повторите после завершения обработки.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось скачать аудио совещания.")); }
    }

    private async Task DownloadOriginalAsync(DesktopMedia media)
    {
        if (_workspace is null || _pageCts is null) return;
        var extension = Path.GetExtension(media.OriginalName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(media.OriginalName));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = SanitizeFileName(_workspace.Meeting?.Title ?? "meeting-source");
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SuggestedFileName = baseName + extension
            };
            InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
            picker.FileTypeChoices.Add("Исходный медиафайл", new List<string> { extension });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            PreviewStatusText.Text = "Скачивание исходного файла…";
            var downloaded = await _workspace.DownloadOriginalMediaAsync(media, file.Path, _pageCts.Token);
            PreviewStatusText.Text = downloaded
                ? $"Исходный файл сохранён: {file.Path}"
                : "Исходный файл пока недоступен.";
            if (!downloaded) ShowError("Сервер ещё не подготовил или не сохранил исходный медиафайл.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось скачать исходный медиафайл.")); }
    }

    private void TranscriptSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyTranscriptFilter();
    }

    private void HideTechnicalEventsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _hideTechnicalEvents = HideTechnicalEventsCheckBox.IsChecked == true;
        ApplyTranscriptFilter();
    }

    private async void TranscriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditSegmentButton.IsEnabled = TranscriptList.SelectedItem is DesktopTranscriptSegment;
        if (_suppressSegmentSeek || TranscriptList.SelectedItem is not DesktopTranscriptSegment segment || _workspace is null || _pageCts is null) return;
        var player = TranscriptPlayer.MediaPlayer ?? PreviewPlayer.MediaPlayer;
        if (player is null)
        {
            var media = _workspace.Media.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PreviewStorageKey));
            if (media is not null)
            {
                var path = await _workspace.LoadPreviewAsync(media, _pageCts.Token);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    TranscriptPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
                    AttachTranscriptPlayback();
                    player = TranscriptPlayer.MediaPlayer;
                }
            }
        }
        if (player is not null) player.PlaybackSession.Position = TimeSpan.FromMilliseconds(segment.StartMs);
    }

    private void AttachTranscriptPlayback()
    {
        if (_transcriptPlaybackSubscribed || TranscriptPlayer.MediaPlayer is null) return;
        _transcriptPlaybackSubscribed = true;
        TranscriptPlayer.MediaPlayer.PlaybackSession.PositionChanged += (_, _) =>
        {
            var positionMs = (long)TranscriptPlayer.MediaPlayer.PlaybackSession.Position.TotalMilliseconds;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_workspace is null) return;
                var segment = _workspace.TranscriptSegments.FirstOrDefault(item => positionMs >= item.StartMs && positionMs < item.EndMs);
                if (segment is null || Equals(TranscriptList.SelectedItem, segment)) return;
                _suppressSegmentSeek = true;
                try
                {
                    TranscriptList.SelectedItem = segment;
                    if (TranscriptAutoScrollCheckBox.IsChecked == true) TranscriptList.ScrollIntoView(segment);
                }
                finally { _suppressSegmentSeek = false; }
            });
        };
    }

    private async void ReprocessTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _pageCts is null) return;
        try
        {
            if (!await _workspace.ReprocessTranscriptAsync(_pageCts.Token)) ShowError("Не удалось поставить новую версию стенограммы в очередь.");
            UpdateWorkspaceText();
        }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось запустить повторную обработку стенограммы.")); }
    }

    private async void EditSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _pageCts is null || TranscriptList.SelectedItem is not DesktopTranscriptSegment segment) return;
        var editor = new TextBox { Text = segment.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 520, MaxLength = 8000 };
        var dialog = new ContentDialog
        {
            Title = "Редактирование сегмента",
            Content = editor,
            PrimaryButtonText = "Сохранить версию",
            CloseButtonText = "Отмена",
            XamlRoot = this.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (!Guid.TryParse(segment.Id, out var segmentId) || !await _workspace.EditTranscriptSegmentAsync(segmentId, editor.Text, _pageCts.Token))
                ShowError("Не удалось создать новую пользовательскую версию стенограммы.");
            else UpdateWorkspaceText();
        }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось сохранить сегмент.")); }
    }

    private void PreviousTranscriptMatchButton_Click(object sender, RoutedEventArgs e) => SelectTranscriptMatch(-1);
    private void NextTranscriptMatchButton_Click(object sender, RoutedEventArgs e) => SelectTranscriptMatch(1);

    private void SelectTranscriptMatch(int direction)
    {
        if (_searchMatches.Count == 0) return;
        _searchMatchIndex = (_searchMatchIndex + direction + _searchMatches.Count) % _searchMatches.Count;
        var match = _searchMatches[_searchMatchIndex];
        TranscriptList.SelectedItem = match;
        TranscriptList.ScrollIntoView(match);
        TranscriptSearchStatus.Text = $"{_searchMatchIndex + 1} / {_searchMatches.Count}";
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_workspace is null) return;
        if (e.AddedItems.OfType<TabViewItem>().FirstOrDefault()?.Header?.ToString() == "Мифодий"
            && _services is not null && MeetingAssistantFrame.Content is null)
            MeetingAssistantFrame.Navigate(typeof(AssistantPage), _services);
        UpdateEmptyStates();
    }

    private void OpenSummariesButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("summaries");

    private void OpenSummaryTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace?.Meeting is { Id: var meetingId } && !string.IsNullOrWhiteSpace(meetingId))
        {
            App.MainWindow.NavigateTo("summaries", new MeetingNavigationTarget(meetingId));
            return;
        }
        WorkspaceTabs.SelectedIndex = 3;
    }

    private void MeetingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            var layoutKey = $"{ResponsiveLayout.GetMode(e.NewSize.Width)}:{e.NewSize.Width < 1080}:{e.NewSize.Width < 1240}:{_workspaceExpanded}";
            if (string.Equals(_lastLayoutKey, layoutKey, StringComparison.Ordinal)) return;
            _lastLayoutKey = layoutKey;
            MeetingsActionsPanel.Orientation = ResponsiveLayout.IsWide(e.NewSize.Width)
                ? Orientation.Horizontal
                : Orientation.Vertical;
            WorkspaceActionsPanel.Orientation = e.NewSize.Width < 1080
                ? Orientation.Vertical
                : Orientation.Horizontal;
            ApplyWorkspaceLayout(e.NewSize.Width);
            ApplyMeetingContentLayout(e.NewSize.Width);
        }
        finally { _updatingLayout = false; }
    }

    private void ApplyWorkspaceLayout(double width)
    {
        var compact = !ResponsiveLayout.IsWide(width);
        var expanded = _workspaceExpanded && _workspace?.HasMeeting == true;

        // An opened workspace becomes the reading surface. The list is hidden so
        // the transcript is not squeezed into the former 380px inspector.
        MeetingsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        MeetingsGrid.ColumnDefinitions[1].Width = expanded || compact
            ? new GridLength(0)
            : new GridLength(420);
        MeetingsGrid.RowDefinitions[0].Height = expanded || !compact
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(300);
        MeetingsGrid.RowDefinitions[1].Height = expanded || !compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);

        ListCard.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceCard.Visibility = Visibility.Visible;
        Grid.SetColumn(ListCard, 0);
        Grid.SetRow(ListCard, 0);
        Grid.SetColumn(WorkspaceCard, expanded || compact ? 0 : 1);
        Grid.SetRow(WorkspaceCard, expanded || !compact ? 0 : 1);
        Grid.SetColumnSpan(WorkspaceCard, expanded ? 2 : 1);
    }

    private void ApplyMeetingContentLayout(double width)
    {
        if (TranscriptWorkspaceGrid is null || TranscriptStatusPanel is null || ProtocolCardsGrid is null) return;
        // The page width includes navigation and padding. At notebook widths the
        // processing rail moves below the transcript instead of squeezing the
        // reading column into a narrow strip.
        var stackTranscriptRail = width < 1240;
        TranscriptWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        TranscriptWorkspaceGrid.ColumnDefinitions[1].Width = stackTranscriptRail ? new GridLength(0) : new GridLength(300);
        TranscriptWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        TranscriptWorkspaceGrid.RowDefinitions[1].Height = stackTranscriptRail ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(TranscriptStatusPanel, stackTranscriptRail ? 0 : 1);
        Grid.SetRow(TranscriptStatusPanel, stackTranscriptRail ? 1 : 0);

        var compact = ResponsiveLayout.GetMode(width) == PageLayoutMode.Compact;
        TranscriptHeaderGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : GridLength.Auto;
        TranscriptHeaderGrid.ColumnDefinitions[2].Width = compact ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(TranscriptReprocessButton, compact ? 0 : 1);
        Grid.SetRow(TranscriptReprocessButton, compact ? 1 : 0);
        Grid.SetColumn(TranscriptExportButton, compact ? 0 : 2);
        Grid.SetRow(TranscriptExportButton, compact ? 1 : 0);
        TranscriptReprocessButton.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        TranscriptExportButton.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        TranscriptOptionsPanel.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        ProtocolCardsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        ProtocolCardsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (ProtocolCardsGrid.Children.Count > 1 && ProtocolCardsGrid.Children[1] is FrameworkElement secondaryProtocolCard)
        {
            Grid.SetColumn(secondaryProtocolCard, compact ? 0 : 1);
            Grid.SetRow(secondaryProtocolCard, compact ? 1 : 0);
        }
    }

    private void UpdateListState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        ListLoadingRing.IsActive = _viewModel.IsLoading;
        MeetingsList.Visibility = _viewModel.IsLoading || !_viewModel.HasFilteredMeetings ? Visibility.Collapsed : Visibility.Visible;
        ListEmptyState.Visibility = _viewModel.IsLoading || _viewModel.HasFilteredMeetings ? Visibility.Collapsed : Visibility.Visible;
        var hasError = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        var hasActiveFilters = !string.IsNullOrWhiteSpace(_viewModel.SearchText)
            || !string.Equals(_viewModel.StatusFilter, "Все статусы", StringComparison.Ordinal);
        ClearFiltersButton.Visibility = _viewModel.HasMeetings && hasActiveFilters ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersButton.IsEnabled = !_viewModel.IsLoading;
        if (hasError)
        {
            ListEmptyTitle.Text = "Не удалось загрузить совещания";
            ListEmptyDescription.Text = _viewModel.ErrorText;
        }
        else if (_viewModel.HasMeetings && !_viewModel.HasFilteredMeetings)
        {
            ListEmptyTitle.Text = "Ничего не найдено";
            ListEmptyDescription.Text = "Измените запрос или сбросьте фильтры, чтобы увидеть совещания.";
        }
        else
        {
            ListEmptyTitle.Text = "Совещаний нет";
            ListEmptyDescription.Text = _viewModel.StatusText;
        }
        ListEmptyStartButton.Visibility = hasError || _viewModel.HasMeetings ? Visibility.Collapsed : Visibility.Visible;
        ListEmptySettingsButton.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        ErrorInfoBar.IsOpen = hasError;
        ErrorInfoBar.Message = _viewModel.ErrorText;
    }

    private void UpdateWorkspaceState()
    {
        if (_workspace is null) return;
        WorkspaceEmptyState.Visibility = _workspace.HasMeeting ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceInspector.Visibility = _workspace.HasMeeting && !_workspaceExpanded ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceContent.Visibility = _workspace.HasMeeting && _workspaceExpanded ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceLoadingOverlay.Visibility = _workspace.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ApplyWorkspaceLayout(MeetingsGrid.ActualWidth > 0 ? MeetingsGrid.ActualWidth : PageRoot.ActualWidth);
        ApplyMeetingContentLayout(PageRoot.ActualWidth);
        OpenWorkspaceButton.IsEnabled = _workspace.HasMeeting && !_workspace.IsLoading;
        OpenWorkspaceButton.Content = _workspaceExpanded ? "Свернуть" : "Открыть совещание";
        WorkspaceMoreButton.IsEnabled = _workspace.HasMeeting && !_workspace.IsLoading;
        RefreshWorkspaceButton.IsEnabled = _workspace.HasMeeting && !_workspace.IsLoading;
        RetryButton.IsEnabled = _workspace.CanRetryLatestJob && !_workspace.IsLoading;
        CancelProcessingButton.IsEnabled = CanCancelProcessing(_workspace.Meeting) && !_workspace.IsLoading;
        DeleteMeetingButton.IsEnabled = _workspace.HasMeeting && !_workspace.IsLoading;
        RefreshWorkspaceMenuItem.IsEnabled = RefreshWorkspaceButton.IsEnabled;
        RetryMenuItem.IsEnabled = RetryButton.IsEnabled;
        CancelProcessingMenuItem.IsEnabled = CancelProcessingButton.IsEnabled;
        ExportTranscriptMenuItem.IsEnabled = _workspace.HasTranscript && !_workspace.IsLoading;
        DeleteMeetingMenuItem.IsEnabled = DeleteMeetingButton.IsEnabled;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_workspace.ErrorText);
        ErrorInfoBar.Message = _workspace.ErrorText;
        UpdateEmptyStates();
    }

    private static bool CanCancelProcessing(DesktopMeeting? meeting) => meeting is not null && meeting.Status is not ("READY" or "PARTIAL_READY" or "FAILED" or "CANCELLED");

    private void UpdateWorkspaceText()
    {
        if (_workspace is null) return;
        WorkspaceTitle.Text = _workspace.Meeting?.Title ?? "Выберите встречу слева";
        WorkspaceMeta.Text = _workspace.Meeting is null ? string.Empty : $"{_workspace.MeetingDateText} · {UiStatusMapper.Text(_workspace.Meeting.Status)}";
        var statusCode = _workspace.Meeting?.Status ?? string.Empty;
        WorkspaceStatusBadge.Status = statusCode;
        WorkspaceStatusBadge.Text = _workspace.Meeting is null ? "Ожидает выбора встречи" : _workspace.IsLoading ? "Загрузка данных встречи" : UiStatusMapper.Text(statusCode);
        PipelineText.Text = _workspace.PipelineText;
        OverviewPipelineText.Text = _workspace.PipelineText;
        DurationText.Text = _workspace.DurationText;
        MediaCountText.Text = _workspace.Media.Count.ToString();
        TranscriptMetaText.Text = _workspace.TranscriptMetaText;
        TranscriptQualityText.Text = _workspace.TranscriptQualityText;
        TranscriptWarningText.Text = _workspace.TranscriptWarningText;
        SummaryText.Text = _workspace.SummaryText;
        SummaryMetaText.Text = _workspace.SummaryMetaText;
        ApplyTranscriptFilter();
    }

    private void UpdateEmptyStates()
    {
        if (_workspace is null) return;
        OverviewEmptyText.Visibility = _workspace.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TranscriptJobsEmptyText.Visibility = _workspace.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SpeakersEmptyText.Visibility = _workspace.Speakers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DecisionsEmptyText.Visibility = _workspace.Decisions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksEmptyText.Visibility = _workspace.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaEmptyText.Visibility = _workspace.Media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyTranscriptFilter();
    }

    private void ApplyTranscriptFilter()
    {
        if (_workspace is null) return;
        var query = TranscriptSearchBox.Text.Trim();
        IReadOnlyList<DesktopTranscriptSegment> visible = _workspace.TranscriptSegments
            .Where(segment => !_hideTechnicalEvents || (!segment.IsHidden && !string.Equals(segment.SegmentKind, "TECHNICAL", StringComparison.OrdinalIgnoreCase)))
            .Where(segment => string.IsNullOrWhiteSpace(query)
                || segment.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (segment.Speaker?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        TranscriptList.ItemsSource = visible;
        _searchMatches = visible;
        _searchMatchIndex = visible.Count == 0 ? -1 : Math.Clamp(_searchMatchIndex, 0, visible.Count - 1);
        TranscriptSearchStatus.Text = string.IsNullOrWhiteSpace(query) ? $"Сегментов: {visible.Count}" : $"Совпадений: {visible.Count}";
        TranscriptEmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TranscriptEmptyTitle.Text = _workspace.Transcript is null
            ? "Стенограмма ещё не готова"
            : string.IsNullOrWhiteSpace(query) ? "Сегментов нет" : "Ничего не найдено";
        TranscriptEmptyDescription.Text = _workspace.Transcript is null
            ? "Дождитесь завершения обработки или обновите данные встречи."
            : string.IsNullOrWhiteSpace(query) ? "Сервер вернул стенограмму без доступных сегментов." : "Измените поисковый запрос.";
    }

    private async void ExportTranscriptTextButton_Click(object sender, RoutedEventArgs e) => await ExportTranscriptAsync(false);

    private async void ExportTranscriptSrtButton_Click(object sender, RoutedEventArgs e) => await ExportTranscriptAsync(true);

    private async Task ExportTranscriptAsync(bool srt)
    {
        if (_workspace is null || !_workspace.HasTranscript)
        {
            ShowError("Стенограмма пока не содержит сегментов для экспорта.");
            return;
        }

        try
        {
            var title = SanitizeFileName(_workspace.Meeting?.Title ?? "transcript");
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = title + (srt ? ".srt" : ".txt")
            };
            InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
            picker.FileTypeChoices.Add(srt ? "Субтитры SubRip" : "Текстовая стенограмма", new List<string> { srt ? ".srt" : ".txt" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var content = srt ? BuildSrt(_workspace.TranscriptSegments) : BuildText(_workspace.Meeting?.Title, _workspace.TranscriptSegments);
            await FileIO.WriteTextAsync(file, content, Windows.Storage.Streams.UnicodeEncoding.Utf8);
            ErrorInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError(UiErrorFormatter.Format(ex, "Не удалось сохранить стенограмму."));
        }
    }

    private static string BuildText(string? title, IEnumerable<DesktopTranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) builder.AppendLine(title.Trim()).AppendLine();
        foreach (var segment in segments.OrderBy(item => item.Ordinal))
        {
            var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? "Спикер не определён" : segment.Speaker.Trim();
            builder.Append('[').Append(segment.TimeLabel).Append("] ").Append(speaker).Append(": ").AppendLine(segment.Text.Trim());
        }
        return builder.ToString();
    }

    private static string BuildSrt(IEnumerable<DesktopTranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var segment in segments.OrderBy(item => item.Ordinal))
        {
            var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? "Спикер не определён" : segment.Speaker.Trim();
            builder.AppendLine(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(FormatSrtTime(segment.StartMs)).Append(" --> ").AppendLine(FormatSrtTime(segment.EndMs));
            builder.Append(speaker).Append(": ").AppendLine(segment.Text.Trim());
            builder.AppendLine();
            index++;
        }
        return builder.ToString();
    }

    private static string FormatSrtTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "transcript" : sanitized;
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}

using System.ComponentModel;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class TranscriptsPage : Page
{
    private TranscriptsViewModel? _viewModel;
    private CancellationTokenSource? _pageCts;
    private CancellationTokenSource? _detailCts;
    private bool _updatingLayout;
    private PageLayoutMode? _lastLayoutMode;

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
        StatusFilterCombo.SelectedIndex = 0;
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

    private void StatusFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && StatusFilterCombo.SelectedItem is ComboBoxItem item)
            _viewModel.StatusFilter = item.Tag?.ToString() ?? "ALL";
        UpdateState();
    }

    private void HideTechnicalEventsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.HideTechnicalEvents = HideTechnicalEventsCheckBox.IsChecked == true;
        UpdateDetails();
    }

    private void PreviousSegmentButton_Click(object sender, RoutedEventArgs e) => SelectAdjacentSegment(-1);

    private void NextSegmentButton_Click(object sender, RoutedEventArgs e) => SelectAdjacentSegment(1);

    private void SelectAdjacentSegment(int direction)
    {
        if (_viewModel is null || _viewModel.FilteredSegmentRows.Count == 0 || _viewModel.SearchMatchCount == 0) return;
        var matches = _viewModel.FilteredSegmentRows.Where(row => row.IsMatch).ToList();
        var currentIndex = _viewModel.SelectedSegment is null
            ? -1
            : matches.FindIndex(row => ReferenceEquals(row.Segment, _viewModel.SelectedSegment));
        var targetIndex = direction < 0
            ? currentIndex <= 0 ? matches.Count - 1 : currentIndex - 1
            : currentIndex < 0 || currentIndex >= matches.Count - 1 ? 0 : currentIndex + 1;
        var target = matches[targetIndex];
        SegmentsList.SelectedItem = target;
        SegmentsList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
        SegmentsList.Focus(FocusState.Programmatic);
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
        if (_viewModel is not null)
            _viewModel.SelectedSegment = (SegmentsList.SelectedItem as TranscriptSegmentRowViewModel)?.Segment;
        UpdateDetails();
    }

    private void OpenMeetingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("meetings");

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");

    private void BackToTranscriptListButton_Click(object sender, RoutedEventArgs e)
    {
        TranscriptsList.SelectedItem = null;
        ListCard.StartBringIntoView();
        TranscriptsList.Focus(FocusState.Programmatic);
    }

    private void OpenSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem is not { Meeting.Id: var meetingId } item || _viewModel.SelectedSegment is not { } segment)
            return;
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(meetingId, segment.Id, segment.StartMs));
    }

    private async void ExportTranscriptTextButton_Click(object sender, RoutedEventArgs e) => await ExportTranscriptAsync(srt: false);

    private async void ExportTranscriptSrtButton_Click(object sender, RoutedEventArgs e) => await ExportTranscriptAsync(srt: true);

    private async void ExportTranscriptDocxButton_Click(object sender, RoutedEventArgs e) => await ExportTranscriptDocxAsync();

    private async Task ExportTranscriptAsync(bool srt)
    {
        var selectedItem = _viewModel?.SelectedItem;
        var transcript = selectedItem?.Transcript;
        if (transcript is null || transcript.Segments.Count == 0)
        {
            WarningInfoBar.Message = "Выберите встречу с готовыми сегментами стенограммы.";
            WarningInfoBar.IsOpen = true;
            return;
        }

        try
        {
            var title = SanitizeFileName(selectedItem?.MeetingTitle ?? "transcript");
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = title + (srt ? ".srt" : ".txt")
            };
            InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
            picker.FileTypeChoices.Add(srt ? "Субтитры SubRip" : "Текстовая стенограмма", new List<string> { srt ? ".srt" : ".txt" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var content = srt
                ? TranscriptExportService.BuildSrt(transcript.Segments)
                : TranscriptExportService.BuildText(selectedItem?.MeetingTitle, transcript.Segments);
            await FileIO.WriteTextAsync(file, content, Windows.Storage.Streams.UnicodeEncoding.Utf8);
            WarningInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось сохранить стенограмму.");
            ErrorInfoBar.IsOpen = true;
        }
    }

    private async Task ExportTranscriptDocxAsync()
    {
        var selectedItem = _viewModel?.SelectedItem;
        var transcript = selectedItem?.Transcript;
        if (transcript is null || transcript.Segments.Count == 0)
        {
            WarningInfoBar.Message = "Выберите встречу с готовыми сегментами стенограммы.";
            WarningInfoBar.IsOpen = true;
            return;
        }

        try
        {
            var title = SanitizeFileName(selectedItem?.MeetingTitle ?? "transcript");
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = title + ".docx"
            };
            InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
            picker.FileTypeChoices.Add("Документ Word", new List<string> { ".docx" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var bytes = TranscriptExportService.BuildDocx(
                selectedItem?.MeetingTitle,
                selectedItem?.MeetingDateText,
                selectedItem?.StatusText,
                selectedItem?.VersionText,
                selectedItem?.QualityText,
                transcript.Segments);
            await FileIO.WriteBytesAsync(file, bytes);
            WarningInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось сохранить документ Word.");
            ErrorInfoBar.IsOpen = true;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "transcript" : sanitized;
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
        var hasFilterNoResults = _viewModel.HasFilterNoResults;
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
        else if (hasFilterNoResults)
        {
            EmptyTitle.Text = "Нет встреч в этом фильтре";
            EmptyDescription.Text = "Выберите другой статус или сбросьте фильтр.";
        }
        else
        {
            EmptyTitle.Text = "Стенограмм нет";
            EmptyDescription.Text = _viewModel.StatusText;
        }
        EmptyRetryButton.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        EmptyRetryButton.IsEnabled = !_viewModel.IsLoading;
        EmptyClearSearchButton.Visibility = hasSearchNoResults || hasFilterNoResults ? Visibility.Visible : Visibility.Collapsed;
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
        SegmentSearchStatusText.Text = _viewModel.SegmentSearchStatus;
        QualityWarningPanel.Visibility = string.IsNullOrWhiteSpace(_viewModel.SelectedQualityWarningText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        var status = _viewModel.SelectedStatus;
        var failed = status.Contains("ошиб", StringComparison.CurrentCultureIgnoreCase)
                     || status.Contains("не удалось", StringComparison.CurrentCultureIgnoreCase)
                     || status.Contains("заблок", StringComparison.CurrentCultureIgnoreCase)
                     || status.Contains("некоррект", StringComparison.CurrentCultureIgnoreCase);
        var processing = status.Contains("обработ", StringComparison.CurrentCultureIgnoreCase)
                         || status.Contains("ожида", StringComparison.CurrentCultureIgnoreCase)
                         || status.Contains("загруз", StringComparison.CurrentCultureIgnoreCase);
        var warning = !string.IsNullOrWhiteSpace(_viewModel.SelectedQualityWarningText);
        SelectedVersionBadge.Background = ResourceBrush(failed ? "DangerSurfaceBrush" : processing || warning ? "SurfaceOrangeBrush" : "SurfaceGreenBrush");
        SelectedVersionBadge.BorderBrush = ResourceBrush(failed ? "DangerBorderBrush" : processing || warning ? "WarningBrush" : "SuccessBrush");
        SelectedVersionBadgeText.Foreground = ResourceBrush(failed ? "DangerBrush" : processing || warning ? "WarningBrush" : "SuccessBrush");
        SelectedSegmentPanel.Visibility = _viewModel.HasSegmentSelection ? Visibility.Visible : Visibility.Collapsed;
        var canNavigateSegments = hasSelection && _viewModel.SearchMatchCount > 0;
        PreviousSegmentButton.IsEnabled = canNavigateSegments;
        NextSegmentButton.IsEnabled = canNavigateSegments;
        OpenSegmentButton.IsEnabled = _viewModel.HasSegmentSelection;
        SelectedSegmentText.Text = _viewModel.SelectedSegment is { } segment
            ? $"{segment.TimeLabel} · {segment.Speaker ?? "Спикер не определён"} · {TimeSpan.FromMilliseconds(Math.Max(0, segment.EndMs - segment.StartMs)):mm\\:ss}\n{segment.Text}"
            : string.Empty;
    }

    private static Brush? ResourceBrush(string key) => Application.Current.Resources[key] as Brush;

    private void TranscriptsPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _viewModel?.HasSegmentSelection == true)
        {
            OpenSegmentButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void TranscriptsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            var mode = ResponsiveLayout.GetMode(e.NewSize.Width);
            if (_lastLayoutMode == mode) return;
            _lastLayoutMode = mode;
            ActionsPanel.Orientation = mode == PageLayoutMode.Compact ? Orientation.Vertical : Orientation.Horizontal;
            var compact = mode != PageLayoutMode.Wide;
            BackToTranscriptListButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(520);
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

using System.ComponentModel;
using System.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
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

    private async void ExportTranscriptTextButton_Click(object sender, RoutedEventArgs e) => await ExportTranscriptAsync(srt: false);

    private async void ExportTranscriptSrtButton_Click(object sender, RoutedEventArgs e) => await ExportTranscriptAsync(srt: true);

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
            var content = srt ? BuildSrt(transcript.Segments) : BuildText(selectedItem?.MeetingTitle, transcript.Segments);
            await FileIO.WriteTextAsync(file, content, Windows.Storage.Streams.UnicodeEncoding.Utf8);
            WarningInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = UiErrorFormatter.Format(ex, "Не удалось сохранить стенограмму.");
            ErrorInfoBar.IsOpen = true;
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

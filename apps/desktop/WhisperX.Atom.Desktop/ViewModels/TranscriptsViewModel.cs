using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class TranscriptRegistryItem
{
    public TranscriptRegistryItem(DesktopMeeting meeting, DesktopTranscript? transcript)
    {
        Meeting = meeting;
        Transcript = transcript;
    }

    public DesktopMeeting Meeting { get; }
    public DesktopTranscript? Transcript { get; }
    public string MeetingTitle => string.IsNullOrWhiteSpace(Meeting.Title) ? "Без названия" : Meeting.Title;
    public string MeetingDateText => Meeting.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
    public string StatusText => Transcript?.Status ?? "Недоступна";
    public string SegmentCountText => Transcript is null ? "—" : Transcript.Segments.Count.ToString(CultureInfo.CurrentCulture);
    public string DurationText => Transcript is null || Transcript.Segments.Count == 0
        ? "—"
        : FormatDuration(Transcript.Segments.Max(segment => segment.EndMs));

    private static string FormatDuration(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}

public sealed class TranscriptsViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private readonly List<TranscriptRegistryItem> _allItems = [];
    private bool _isLoading;
    private string _searchText = string.Empty;
    private string _segmentSearchText = string.Empty;
    private string _errorText = string.Empty;
    private string _warningText = string.Empty;
    private string _statusText = string.Empty;
    private TranscriptRegistryItem? _selectedItem;
    private DesktopTranscriptSegment? _selectedSegment;

    public TranscriptsViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<TranscriptRegistryItem> FilteredItems { get; } = [];
    public ObservableCollection<DesktopTranscriptSegment> FilteredSegments { get; } = [];

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilters(); }
    }
    public string SegmentSearchText
    {
        get => _segmentSearchText;
        set { if (SetProperty(ref _segmentSearchText, value)) ApplySegmentFilter(); }
    }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string WarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public TranscriptRegistryItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            SelectedSegment = null;
            ApplySegmentFilter();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedStatus));
        }
    }

    public DesktopTranscriptSegment? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (!SetProperty(ref _selectedSegment, value)) return;
            OnPropertyChanged(nameof(HasSegmentSelection));
        }
    }

    public bool HasItems => FilteredItems.Count > 0;
    public bool HasSelection => SelectedItem is not null;
    public bool HasSegments => FilteredSegments.Count > 0;
    public bool HasSegmentSelection => SelectedSegment is not null;
    public string SelectedTitle => SelectedItem?.MeetingTitle ?? "Совещание не выбрано";
    public string SelectedStatus => SelectedItem?.StatusText ?? "—";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;
        WarningText = string.Empty;
        StatusText = string.Empty;
        _allItems.Clear();
        FilteredItems.Clear();
        FilteredSegments.Clear();
        SelectedItem = null;

        try
        {
            if (!await _services.Backend.CheckReadyAsync(cancellationToken))
            {
                ErrorText = "API недоступен. Проверьте backend и подключение.";
                return;
            }

            if (!_services.Backend.HasSession)
            {
                StatusText = "Войдите в API, чтобы загрузить стенограммы.";
                return;
            }

            var meetings = await LoadAllMeetingsAsync(cancellationToken);
            var loaded = new List<TranscriptRegistryItem>();
            var failures = 0;
            using var gate = new SemaphoreSlim(4, 4);
            var work = meetings.Select(async meeting =>
            {
                DesktopTranscript? transcript = null;
                if (!Guid.TryParse(meeting.Id, out var meetingId))
                {
                    Interlocked.Increment(ref failures);
                }
                else
                {
                    await gate.WaitAsync(cancellationToken);
                    try { transcript = await _services.Backend.GetTranscriptAsync(meetingId, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch { Interlocked.Increment(ref failures); }
                    finally { gate.Release(); }
                }

                lock (loaded) loaded.Add(new TranscriptRegistryItem(meeting, transcript));
            });
            await Task.WhenAll(work);

            _allItems.AddRange(loaded.OrderByDescending(item => item.Meeting.CreatedAt));
            ApplyFilters();
            StatusText = _allItems.Count == 0
                ? "Встреч пока нет."
                : $"Загружено встреч: {_allItems.Count}; стенограммы: {_allItems.Count(item => item.Transcript is not null)}.";
            if (failures > 0)
                WarningText = $"Не удалось загрузить стенограммы для встреч: {failures}. Доступные данные сохранены.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorText = SafeError(ex, "Не удалось загрузить стенограммы.");
            _allItems.Clear();
            ApplyFilters();
        }
        finally { IsLoading = false; }
    }

    private async Task<IReadOnlyList<DesktopMeeting>> LoadAllMeetingsAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var result = new List<DesktopMeeting>();
        var offset = 0;
        while (true)
        {
            var page = await _services.Backend.GetMeetingsPageAsync(pageSize, offset, cancellationToken);
            result.AddRange(page);
            if (page.Count < pageSize) return result;
            offset += page.Count;
        }
    }

    private void ApplyFilters()
    {
        var query = SearchText.Trim();
        FilteredItems.Clear();
        foreach (var item in _allItems.Where(item =>
                     string.IsNullOrWhiteSpace(query)
                     || item.MeetingTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (item.Meeting.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                 .OrderByDescending(item => item.Meeting.CreatedAt))
            FilteredItems.Add(item);

        if (SelectedItem is not null && !FilteredItems.Contains(SelectedItem))
            SelectedItem = null;
        OnPropertyChanged(nameof(HasItems));
    }

    private void ApplySegmentFilter()
    {
        FilteredSegments.Clear();
        var segments = SelectedItem?.Transcript?.Segments ?? [];
        var query = SegmentSearchText.Trim();
        foreach (var segment in segments.Where(segment => string.IsNullOrWhiteSpace(query) || segment.Text.Contains(query, StringComparison.OrdinalIgnoreCase)))
            FilteredSegments.Add(segment);
        OnPropertyChanged(nameof(HasSegments));
    }

    private static string SafeError(Exception ex, string fallback) => string.IsNullOrWhiteSpace(ex.Message) ? fallback : ex.Message;
}

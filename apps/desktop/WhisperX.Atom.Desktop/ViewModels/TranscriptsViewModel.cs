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
    public DesktopTranscriptRegistry? Registry { get; }
    public DesktopTranscript? Transcript { get; private set; }
    public void SetTranscript(DesktopTranscript? transcript) => Transcript = transcript;
    public TranscriptRegistryItem(DesktopTranscriptRegistry registry)
    {
        Registry = registry;
        Meeting = new DesktopMeeting(registry.MeetingId, registry.MeetingTitle, null, registry.Status, registry.MeetingCreatedAt);
    }
    public string QualityText => QualityCategory(Transcript?.QualityScore ?? Registry?.QualityScore);
    public bool HasQualityWarning => Transcript?.IsPartial == true || Registry?.IsPartial == true;
    public string MeetingTitle => string.IsNullOrWhiteSpace(Meeting.Title) ? "Без названия" : Meeting.Title;
    public string MeetingDateText => Meeting.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
    public string StatusText => Transcript?.Status ?? Registry?.Status ?? "Недоступна";
    public string SegmentCountText => (Transcript?.Segments.Count ?? Registry?.SegmentCount)?.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string DurationText => Transcript is { Segments.Count: > 0 }
        ? FormatDuration(Transcript.Segments.Max(segment => segment.EndMs))
        : Registry is not null ? FormatDuration(Registry.DurationMs) : "—";

    private static string FormatDuration(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string QualityCategory(double? score) => score switch
    {
        >= 85 => "Высокое",
        >= 65 => "Среднее",
        >= 0 => "Требует проверки",
        _ => "Качество не рассчитано"
    };
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
            OnPropertyChanged(nameof(SelectedQualityText));
            OnPropertyChanged(nameof(SelectedQualityWarningText));
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
    public string SelectedQualityText => SelectedItem?.QualityText ?? "Качество не рассчитано";
    public string SelectedQualityWarningText => SelectedItem?.HasQualityWarning == true
        ? "Стенограмма получена с предупреждениями."
        : string.Empty;
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

            var registry = await _services.Backend.GetTranscriptRegistryPageAsync(limit: 200, offset: 0, cancellationToken: cancellationToken);
            _allItems.AddRange(registry.Select(item => new TranscriptRegistryItem(item)));
            ApplyFilters();
            StatusText = _allItems.Count == 0
                ? "Встреч пока нет."
                : $"Загружено встреч: {_allItems.Count}. Стенограмма загружается после выбора встречи.";
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

    public async Task LoadSelectedAsync(TranscriptRegistryItem item, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(item.Meeting.Id, out var meetingId)) return;
        IsLoading = true;
        try
        {
            item.SetTranscript(await _services.Backend.GetTranscriptAsync(meetingId, cancellationToken));
            SelectedItem = item;
            ApplySegmentFilter();
            OnPropertyChanged(nameof(SelectedQualityText));
            OnPropertyChanged(nameof(SelectedQualityWarningText));
        }
        finally { IsLoading = false; }
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

    private static string SafeError(Exception ex, string fallback) => UiErrorFormatter.Format(ex, fallback);
}

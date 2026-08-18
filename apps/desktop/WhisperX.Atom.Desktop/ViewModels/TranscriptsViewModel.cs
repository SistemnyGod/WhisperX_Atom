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
    public string StatusText => UiStatusMapper.Text(Transcript?.Status ?? Registry?.Status ?? "UNKNOWN");
    public string SegmentCountText => (Transcript?.Segments.Count ?? Registry?.SegmentCount)?.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string DurationText => Transcript is { Segments.Count: > 0 }
        ? FormatDuration(Transcript.Segments.Max(segment => segment.EndMs))
        : Registry is not null ? FormatDuration(Registry.DurationMs) : "—";
    public string VersionText => Registry?.TranscriptVersion switch
    {
        >= 2 => "Enriched V2",
        1 => "ASR V1",
        _ => "Ожидает стенограмму"
    };
    public string VersionBadgeText => Registry?.TranscriptVersion switch
    {
        >= 2 => "Стенограмма V2 готова",
        1 => "Стенограмма V1 доступна",
        _ => "Обработка стенограммы"
    };

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
    private string _statusFilter = "ALL";
    private bool _hideTechnicalEvents = true;
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
    public string StatusFilter
    {
        get => _statusFilter;
        set { if (SetProperty(ref _statusFilter, value)) ApplyFilters(); }
    }
    public bool HideTechnicalEvents
    {
        get => _hideTechnicalEvents;
        set { if (SetProperty(ref _hideTechnicalEvents, value)) ApplySegmentFilter(); }
    }

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
            OnPropertyChanged(nameof(SelectedVersionText));
            OnPropertyChanged(nameof(SelectedVersionBadgeText));
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
    public bool HasLoadedItems => _allItems.Count > 0;
    public bool HasSearchNoResults => HasLoadedItems && !HasItems && !string.IsNullOrWhiteSpace(SearchText);
    public bool HasFilterNoResults => HasLoadedItems && !HasItems && StatusFilter != "ALL";
    public bool HasSelection => SelectedItem is not null;
    public bool HasSegments => FilteredSegments.Count > 0;
    public bool HasSegmentSelection => SelectedSegment is not null;
    public string SelectedQualityText => SelectedItem?.QualityText ?? "Качество не рассчитано";
    public string SelectedQualityWarningText
    {
        get
        {
            var warnings = SelectedItem?.Transcript?.Warnings;
            if (ContainsWarning(warnings, "NO_SPEECH_DETECTED"))
                return "Аудио сохранено корректно, но речь не обнаружена. Проверьте выбранный микрофон и уровень сигнала.";
            return SelectedItem?.HasQualityWarning == true
                ? "Стенограмма получена с предупреждениями. Текст доступен для чтения и экспорта."
                : string.Empty;
        }
    }
    public string SelectedTitle => SelectedItem?.MeetingTitle ?? "Совещание не выбрано";
    public string SelectedStatus => SelectedItem?.StatusText ?? "—";
    public string RegistryCountText => $"Загружено встреч: {_allItems.Count}";
    public string SelectedVersionText => SelectedItem?.VersionText ?? "Версия ещё не определена";
    public string SelectedVersionBadgeText => SelectedItem?.VersionBadgeText ?? "Стенограмма не выбрана";

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
        OnPropertyChanged(nameof(HasLoadedItems));
        OnPropertyChanged(nameof(RegistryCountText));

        try
        {
            if (!await _services.Backend.CheckReadyAsync(cancellationToken))
            {
                ErrorText = "API недоступен. Проверьте backend и подключение.";
                return;
            }

            if (!await _services.Backend.EnsureAuthenticatedAsync(cancellationToken))
            {
                ErrorText = _services.Backend.AuthState == DesktopAuthState.Offline
                    ? "Сервер перестал отвечать. Проверьте LAN-подключение и повторите попытку."
                    : "Сеанс API истёк. Откройте настройки и выполните вход повторно.";
                return;
            }

            var registry = await _services.Backend.GetTranscriptRegistryPageAsync(limit: 200, offset: 0, cancellationToken: cancellationToken);
            _allItems.AddRange(registry.Select(item => new TranscriptRegistryItem(item)));
            OnPropertyChanged(nameof(HasLoadedItems));
            OnPropertyChanged(nameof(RegistryCountText));
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
            // The item is commonly already selected when a background poll
            // refreshes its V1/V2 transcript.  In that case SetProperty in the
            // SelectedItem setter is a no-op, so explicitly refresh every
            // property bound by the selected-transcript card.
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedStatus));
            OnPropertyChanged(nameof(SelectedQualityText));
            OnPropertyChanged(nameof(SelectedQualityWarningText));
            OnPropertyChanged(nameof(SelectedVersionText));
            OnPropertyChanged(nameof(SelectedVersionBadgeText));
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
                 .Where(item => MatchesStatus(item, StatusFilter))
                 .OrderByDescending(item => item.Meeting.CreatedAt))
            FilteredItems.Add(item);

        if (SelectedItem is not null && !FilteredItems.Contains(SelectedItem))
            SelectedItem = null;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasSearchNoResults));
        OnPropertyChanged(nameof(HasFilterNoResults));
    }

    private void ApplySegmentFilter()
    {
        FilteredSegments.Clear();
        var segments = SelectedItem?.Transcript?.Segments ?? [];
        var query = SegmentSearchText.Trim();
        foreach (var segment in segments.Where(segment =>
                     (!HideTechnicalEvents || !IsTechnical(segment))
                     && (string.IsNullOrWhiteSpace(query) || segment.Text.Contains(query, StringComparison.OrdinalIgnoreCase))))
            FilteredSegments.Add(segment);
        OnPropertyChanged(nameof(HasSegments));
    }

    private static bool IsTechnical(DesktopTranscriptSegment segment) => segment.IsHidden
        || string.Equals(segment.SegmentKind, "TECHNICAL", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesStatus(TranscriptRegistryItem item, string filter)
    {
        if (string.Equals(filter, "ALL", StringComparison.OrdinalIgnoreCase)) return true;
        var status = item.Registry?.Status ?? item.Transcript?.Status ?? string.Empty;
        if (string.Equals(filter, "READY", StringComparison.OrdinalIgnoreCase))
            return status.Contains("READY", StringComparison.OrdinalIgnoreCase)
                || status.Contains("COMPLETED", StringComparison.OrdinalIgnoreCase)
                || status.Contains("DONE", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filter, "PROCESSING", StringComparison.OrdinalIgnoreCase))
            return status.Contains("PROCESS", StringComparison.OrdinalIgnoreCase)
                || status.Contains("QUEUE", StringComparison.OrdinalIgnoreCase)
                || status.Contains("UPLOAD", StringComparison.OrdinalIgnoreCase)
                || status.Contains("ENCOD", StringComparison.OrdinalIgnoreCase)
                || status.Contains("ASR", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filter, "ERROR", StringComparison.OrdinalIgnoreCase))
            return status.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
                || status.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || status.Contains("INVALID", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string SafeError(Exception ex, string fallback) => UiErrorFormatter.Format(ex, fallback);

    private static bool ContainsWarning(System.Text.Json.JsonDocument? warnings, string code)
    {
        if (warnings is null) return false;
        return ContainsWarning(warnings.RootElement, code);
    }

    private static bool ContainsWarning(System.Text.Json.JsonElement element, string code)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
            return string.Equals(element.GetString(), code, StringComparison.OrdinalIgnoreCase);
        if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            return element.EnumerateArray().Any(item => ContainsWarning(item, code));
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, code, StringComparison.OrdinalIgnoreCase)
                && (property.Value.ValueKind == System.Text.Json.JsonValueKind.True
                    || property.Value.ValueKind == System.Text.Json.JsonValueKind.String))
                return true;
            if (ContainsWarning(property.Value, code)) return true;
        }
        return false;
    }
}

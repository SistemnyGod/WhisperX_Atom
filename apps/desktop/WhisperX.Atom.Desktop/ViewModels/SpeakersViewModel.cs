using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class SpeakerRegistryItem
{
    public SpeakerRegistryItem(DesktopMeeting meeting, DesktopSpeaker speaker)
    {
        Meeting = meeting;
        Speaker = speaker;
    }

    public DesktopMeeting Meeting { get; }
    public DesktopSpeaker Speaker { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Speaker.DisplayName) ? "Без имени" : Speaker.DisplayName;
    public string StableKey => string.IsNullOrWhiteSpace(Speaker.StableKey) ? "—" : Speaker.StableKey;
    public string MatchStatus => Speaker.ProfileMatchStatus switch
    {
        "MATCHED" => "Профиль подтверждён",
        "SUGGESTION" when Speaker.ProfileConfidence is double confidence => $"Возможно: {Speaker.ProfileSuggestionName ?? "профиль"} · {confidence:P0}",
        _ => "Имя не подтверждено"
    };
    public string? MatchReason => Speaker.ProfileMatchReason;
    public bool HasSuggestion => string.Equals(Speaker.ProfileMatchStatus, "SUGGESTION", StringComparison.OrdinalIgnoreCase);
    public string MeetingTitle => string.IsNullOrWhiteSpace(Meeting.Title) ? "Без названия" : Meeting.Title;
    public string MeetingDateText => Meeting.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
}

public sealed class SpeakersViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private readonly List<SpeakerRegistryItem> _allItems = [];
    private bool _isLoading;
    private string _searchText = string.Empty;
    private string _errorText = string.Empty;
    private string _warningText = string.Empty;
    private string _statusText = string.Empty;
    private SpeakerRegistryItem? _selectedItem;
    private CancellationTokenSource? _filterDebounce;

    public SpeakersViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<SpeakerRegistryItem> FilteredItems { get; } = [];

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ScheduleFilter(); }
    }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string WarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public SpeakerRegistryItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasItems => FilteredItems.Count > 0;
    public bool HasSelection => SelectedItem is not null;
    public string SpeakerCountText => _allItems.Select(item => item.Speaker.StableKey).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.CurrentCulture);
    public string MeetingCountText => _allItems.Select(item => item.Meeting.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.CurrentCulture);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;
        WarningText = string.Empty;
        StatusText = string.Empty;
        _allItems.Clear();
        FilteredItems.Clear();
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
                StatusText = "Войдите в API, чтобы загрузить спикеров.";
                return;
            }

            var page = await _services.Backend.GetSpeakerRegistryPageAsync(1, 100, SearchText, cancellationToken: cancellationToken);
            _allItems.AddRange(page.Items.Select(item => new SpeakerRegistryItem(item.Meeting, item.Speaker))
                .OrderBy(item => item.DisplayName).ThenByDescending(item => item.Meeting.CreatedAt));
            ApplyFilterNow();
            StatusText = page.TotalCount == 0
                ? "Спикеры пока не определены."
                : $"Показано записей спикеров: {_allItems.Count} из {page.TotalCount}.";
            NotifyCounts();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorText = SafeError(ex, "Не удалось загрузить спикеров.");
            _allItems.Clear();
            ApplyFilterNow();
            NotifyCounts();
        }
        finally { IsLoading = false; }
    }

    private void ScheduleFilter()
    {
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        var cts = _filterDebounce = new CancellationTokenSource();
        _ = ApplyFilterDebouncedAsync(cts.Token);
    }

    private async Task ApplyFilterDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            ApplyFilterNow();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_filterDebounce is { } current && current.Token == cancellationToken)
            {
                _filterDebounce = null;
                current.Dispose();
            }
        }
    }

    private void ApplyFilterNow()
    {
        var query = SearchText.Trim();
        var visible = _allItems.Where(item =>
                     string.IsNullOrWhiteSpace(query)
                     || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.StableKey.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.MeetingTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
                  .OrderBy(item => item.DisplayName)
                  .ThenByDescending(item => item.Meeting.CreatedAt).ToList();
        if (!FilteredItems.SequenceEqual(visible))
        {
            FilteredItems.Clear();
            foreach (var item in visible) FilteredItems.Add(item);
        }

        if (SelectedItem is not null && !FilteredItems.Contains(SelectedItem)) SelectedItem = null;
        OnPropertyChanged(nameof(HasItems));
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(SpeakerCountText));
        OnPropertyChanged(nameof(MeetingCountText));
    }

    private static string SafeError(Exception ex, string fallback) => UiErrorFormatter.Format(ex, fallback);
}

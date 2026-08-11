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

    public SpeakersViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<SpeakerRegistryItem> FilteredItems { get; } = [];

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
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

            var meetings = await LoadAllMeetingsAsync(cancellationToken);
            var loaded = new List<SpeakerRegistryItem>();
            var failures = 0;
            using var gate = new SemaphoreSlim(4, 4);
            var work = meetings.Select(async meeting =>
            {
                if (!Guid.TryParse(meeting.Id, out var meetingId))
                {
                    Interlocked.Increment(ref failures);
                    return;
                }

                await gate.WaitAsync(cancellationToken);
                try
                {
                    var speakers = await _services.Backend.GetSpeakersAsync(meetingId, cancellationToken);
                    lock (loaded)
                        foreach (var speaker in speakers) loaded.Add(new SpeakerRegistryItem(meeting, speaker));
                }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref failures); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(work);

            _allItems.AddRange(loaded.OrderBy(item => item.DisplayName).ThenByDescending(item => item.Meeting.CreatedAt));
            ApplyFilter();
            StatusText = _allItems.Count == 0
                ? "Спикеры пока не определены."
                : $"Загружено записей спикеров: {_allItems.Count}.";
            if (failures > 0)
                WarningText = $"Не удалось загрузить спикеров для встреч: {failures}. Доступные данные сохранены.";
            NotifyCounts();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorText = SafeError(ex, "Не удалось загрузить спикеров.");
            _allItems.Clear();
            ApplyFilter();
            NotifyCounts();
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

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        FilteredItems.Clear();
        foreach (var item in _allItems.Where(item =>
                     string.IsNullOrWhiteSpace(query)
                     || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.StableKey.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.MeetingTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(item => item.DisplayName)
                 .ThenByDescending(item => item.Meeting.CreatedAt))
            FilteredItems.Add(item);

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

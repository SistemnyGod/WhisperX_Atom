using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class SummaryRegistryItem
{
    public SummaryRegistryItem(DesktopMeeting meeting, DesktopSummary? summary)
    {
        Meeting = meeting;
        Summary = summary;
    }

    public DesktopMeeting Meeting { get; }
    public DesktopSummary? Summary { get; private set; }
    public string MeetingTitle => string.IsNullOrWhiteSpace(Meeting.Title) ? "Без названия" : Meeting.Title;
    public string MeetingDateText => Meeting.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string StatusText => Summary?.Status ?? "Не готово";
    public string MetaText => Summary is null ? "Итог ещё не создан" : $"Версия {Summary.Version} · {Summary.ModelName}";
    public string SummaryText => FormatSummary(Summary);
    public bool HasSummary => Summary is not null;
    public string ReviewText => FormatReview(Summary);
    public bool NeedsReview => !string.IsNullOrWhiteSpace(ReviewText);

    public void ReplaceSummary(DesktopSummary? summary)
    {
        Summary = summary;
    }

    private static string FormatSummary(DesktopSummary? summary)
    {
        return MeetingProtocolParser.Parse(summary?.Content).DisplayText;
    }

    private static string FormatReview(DesktopSummary? summary)
    {
        if (summary is null) return string.Empty;
        var root = summary.Content.RootElement;
        if (!root.TryGetProperty("validation", out var validation) || validation.ValueKind != JsonValueKind.Object)
            return string.Equals(summary.Status, "NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase) ? "Требует проверки." : string.Empty;
        var rejected = validation.TryGetProperty("rejected_facts", out var rejectedValue) && rejectedValue.TryGetInt32(out var rejectedCount)
            ? rejectedCount
            : 0;
        var unsupported = validation.TryGetProperty("unsupported_claims", out var unsupportedValue) && unsupportedValue.TryGetInt32(out var unsupportedCount)
            ? unsupportedCount
            : 0;
        if (rejected == 0 && unsupported == 0 && !string.Equals(summary.Status, "NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return $"Требует проверки: кандидатов отклонено — {rejected}, неподтверждённых тезисов — {unsupported}.";
    }
}

public sealed class SummariesViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private readonly List<SummaryRegistryItem> _allItems = [];
    private bool _isLoading;
    private bool _isRebuilding;
    private string _searchText = string.Empty;
    private string _errorText = string.Empty;
    private string _warningText = string.Empty;
    private string _statusText = string.Empty;
    private SummaryRegistryItem? _selectedItem;

    public SummariesViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<SummaryRegistryItem> FilteredItems { get; } = [];
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsRebuilding { get => _isRebuilding; private set => SetProperty(ref _isRebuilding, value); }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string WarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public SummaryRegistryItem? SelectedItem
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
    public bool CanRebuild => SelectedItem is not null && !IsRebuilding;

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
                StatusText = "Войдите в API, чтобы загрузить саммари.";
                return;
            }

            try
            {
                var readiness = await _services.Backend.GetProcessingReadinessAsync(cancellationToken);
                if (UiStatusMapper.IsQwenDisabled(readiness))
                    WarningText = "Саммари отключено настройками сервера. После включения Qwen новая сборка появится здесь автоматически.";
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            var meetings = await LoadAllMeetingsAsync(cancellationToken);
            var loaded = new List<SummaryRegistryItem>();
            var failures = 0;
            using var gate = new SemaphoreSlim(4, 4);
            var work = meetings.Select(async meeting =>
            {
                DesktopSummary? summary = null;
                if (!Guid.TryParse(meeting.Id, out var meetingId))
                {
                    Interlocked.Increment(ref failures);
                }
                else
                {
                    await gate.WaitAsync(cancellationToken);
                    try { summary = await _services.Backend.GetSummaryAsync(meetingId, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch { Interlocked.Increment(ref failures); }
                    finally { gate.Release(); }
                }
                lock (loaded) loaded.Add(new SummaryRegistryItem(meeting, summary));
            });
            await Task.WhenAll(work);

            _allItems.AddRange(loaded.OrderByDescending(item => item.Meeting.CreatedAt));
            ApplyFilter();
            StatusText = _allItems.Count == 0
                ? "Встреч пока нет."
                : $"Загружено встреч: {_allItems.Count}; саммари готовы: {_allItems.Count(item => item.HasSummary)}.";
            if (failures > 0)
                WarningText = $"Не удалось загрузить саммари для встреч: {failures}. Доступные данные сохранены.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorText = SafeError(ex, "Не удалось загрузить саммари.");
            _allItems.Clear();
            ApplyFilter();
        }
        finally { IsLoading = false; }
    }

    public async Task<bool> RebuildSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedItem is not { Meeting.Id: var meetingId } item || !Guid.TryParse(meetingId, out var parsedMeetingId))
            return false;

        IsRebuilding = true;
        ErrorText = string.Empty;
        try
        {
            var readiness = await _services.Backend.GetProcessingReadinessAsync(cancellationToken);
            if (UiStatusMapper.IsQwenDisabled(readiness))
            {
                ErrorText = UiStatusMapper.SummaryDisabledMessage;
                StatusText = "Саммари отключено настройками сервера";
                return false;
            }
            var job = await _services.Backend.QueueSummaryRebuildAsync(parsedMeetingId, cancellationToken);
            if (job is null)
            {
                ErrorText = "Саммари не поставлено в очередь. Проверьте, что Qwen включена и стенограмма готова.";
                return false;
            }
            var completed = await _services.JobTracker.WaitForTerminalAsync(job, current =>
                StatusText = $"{UiStatusMapper.Text(current.Status)} · {UiStatusMapper.Text(current.Stage)} · {current.Progress}%", cancellationToken);
            if (completed is null || !string.Equals(completed.Status, "READY", StringComparison.OrdinalIgnoreCase))
            {
                ErrorText = completed?.Error ?? "Пересборка саммари завершилась ошибкой.";
                return false;
            }
            var summary = await _services.Backend.GetSummaryAsync(parsedMeetingId, cancellationToken);
            item.ReplaceSummary(summary);
            OnPropertyChanged(nameof(SelectedItem));
            ApplyFilter();
            StatusText = summary is null ? "Запрос принят, саммари ещё не готово." : "Саммари обновлено.";
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorText = SafeError(ex, "Не удалось пересобрать саммари.");
            return false;
        }
        finally
        {
            IsRebuilding = false;
            OnPropertyChanged(nameof(CanRebuild));
        }
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
                     || item.MeetingTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (item.Meeting.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                     || item.SummaryText.Contains(query, StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(item => item.Meeting.CreatedAt))
            FilteredItems.Add(item);
        if (SelectedItem is not null && !FilteredItems.Contains(SelectedItem)) SelectedItem = null;
        OnPropertyChanged(nameof(HasItems));
    }

    private static string SafeError(Exception ex, string fallback) => UiErrorFormatter.Format(ex, fallback);

}

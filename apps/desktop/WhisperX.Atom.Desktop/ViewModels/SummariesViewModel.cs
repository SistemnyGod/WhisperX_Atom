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
    public string StatusText => Summary is null ? "Не готово" : UiStatusMapper.Text(Summary.Status);
    public string StatusCode => Summary?.Status ?? "SUMMARY_NOT_READY";
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
        return SummaryPresentation.Format(summary);
    }

    private static string FormatReview(DesktopSummary? summary)
    {
        if (summary is null) return string.Empty;
        if (string.Equals(summary.ContentValidity, "NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase))
            return "Требует проверки: часть тезисов отклонена или не имеет подтверждённого источника.";
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
    private CancellationTokenSource? _filterDebounce;

    public SummariesViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<SummaryRegistryItem> FilteredItems { get; } = [];
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsRebuilding { get => _isRebuilding; private set => SetProperty(ref _isRebuilding, value); }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ScheduleFilter(); }
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

    public bool SelectMeeting(string? meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId)) return false;
        var item = FilteredItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Meeting.Id, meetingId, StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;
        SelectedItem = item;
        return true;
    }

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

            var page = await _services.Backend.GetSummaryRegistryPageAsync(1, 100, SearchText, cancellationToken: cancellationToken);
            _allItems.AddRange(page.Items.Select(item => new SummaryRegistryItem(item.Meeting, item.Summary))
                .OrderByDescending(item => item.Meeting.CreatedAt));
            ApplyFilterNow();
            StatusText = page.TotalCount == 0
                ? "Встреч пока нет."
                : $"Показано встреч: {_allItems.Count} из {page.TotalCount}; саммари готовы: {_allItems.Count(item => item.HasSummary)}.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorText = SafeError(ex, "Не удалось загрузить саммари.");
            _allItems.Clear();
            ApplyFilterNow();
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
                StatusText = $"{UiStatusMapper.Text(current.Status)} · {UiStatusMapper.Text(current.Stage)} · {current.Progress}%", cancellationToken,
                observation =>
                {
                    if (observation.State == ProcessingJobState.Background)
                        StatusText = "Обработка продолжается в фоне. Обновите встречу позже.";
                    else if (observation.State is ProcessingJobState.Stalled or ProcessingJobState.Blocked)
                        StatusText = observation.State == ProcessingJobState.Blocked
                            ? $"Обработка заблокирована: {observation.Reason}"
                            : $"Обработка приостановлена: {observation.Reason}";
                });
            if (completed is null || !string.Equals(completed.Status, "READY", StringComparison.OrdinalIgnoreCase))
            {
                ErrorText = completed?.Error ?? "Пересборка саммари завершилась ошибкой.";
                return false;
            }
            var summary = await _services.Backend.GetSummaryAsync(parsedMeetingId, cancellationToken);
            item.ReplaceSummary(summary);
            OnPropertyChanged(nameof(SelectedItem));
            ApplyFilterNow();
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
                     || item.MeetingTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (item.Meeting.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                     || item.SummaryText.Contains(query, StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(item => item.Meeting.CreatedAt).ToList();
        if (!FilteredItems.SequenceEqual(visible))
        {
            FilteredItems.Clear();
            foreach (var item in visible) FilteredItems.Add(item);
        }
        if (SelectedItem is not null && !FilteredItems.Contains(SelectedItem)) SelectedItem = null;
        OnPropertyChanged(nameof(HasItems));
    }

    private static string SafeError(Exception ex, string fallback) => UiErrorFormatter.Format(ex, fallback);

}

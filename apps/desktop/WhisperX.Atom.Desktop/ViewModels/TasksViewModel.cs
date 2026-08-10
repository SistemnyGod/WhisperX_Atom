using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class TaskRegistryItem
{
    public TaskRegistryItem(DesktopMeeting meeting, DesktopTask task)
    {
        Meeting = meeting;
        Task = task;
    }

    public DesktopMeeting Meeting { get; }
    public DesktopTask Task { get; private set; }
    public string MeetingTitle => string.IsNullOrWhiteSpace(Meeting.Title) ? "Без названия" : Meeting.Title;
    public string MeetingDateText => Meeting.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
    public string TaskText => Task.Task;
    public string ResponsibleText => string.IsNullOrWhiteSpace(Task.Responsible) ? "Ответственный не указан" : Task.Responsible!;
    public string StatusText => Task.Status;
    public string DeadlineText => Task.Deadline.HasValue
        ? Task.Deadline.Value.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture)
        : "Без срока";
    public bool IsOverdue => Task.Deadline.HasValue
        && Task.Deadline.Value.Date < DateTime.Now.Date
        && !IsTerminal(Task.Status);
    public Guid? EvidenceSegmentId => Task.EvidenceSegmentId;

    public void Replace(DesktopTask task) => Task = task;

    private static bool IsTerminal(string status) => status.Equals("DONE", StringComparison.OrdinalIgnoreCase)
        || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
}

public sealed class TasksViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private readonly List<TaskRegistryItem> _allItems = [];
    private bool _isLoading;
    private bool _isSaving;
    private string _searchText = string.Empty;
    private string _statusFilter = "ACTIVE";
    private string _deadlineFilter = "ALL";
    private string _meetingFilterId = string.Empty;
    private string _responsibleFilter = string.Empty;
    private string _errorText = string.Empty;
    private string _warningText = string.Empty;
    private string _statusText = string.Empty;
    private TaskRegistryItem? _selectedItem;
    private string _editTaskText = string.Empty;
    private string _editResponsible = string.Empty;
    private DateTimeOffset? _editDeadline;
    private string _editStatus = "OPEN";

    public TasksViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<TaskRegistryItem> FilteredItems { get; } = [];
    public ObservableCollection<DesktopMeeting> Meetings { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set => SetProperty(ref _isSaving, value);
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilters(); }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set { if (SetProperty(ref _statusFilter, value)) ApplyFilters(); }
    }

    public string DeadlineFilter
    {
        get => _deadlineFilter;
        set { if (SetProperty(ref _deadlineFilter, value)) ApplyFilters(); }
    }

    public string MeetingFilterId
    {
        get => _meetingFilterId;
        set { if (SetProperty(ref _meetingFilterId, value)) ApplyFilters(); }
    }

    public string ResponsibleFilter
    {
        get => _responsibleFilter;
        set { if (SetProperty(ref _responsibleFilter, value)) ApplyFilters(); }
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set => SetProperty(ref _warningText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public TaskRegistryItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            EditTaskText = value?.Task.Task ?? string.Empty;
            EditResponsible = value?.Task.Responsible ?? string.Empty;
            EditDeadline = value?.Task.Deadline;
            EditStatus = value?.Task.Status ?? "OPEN";
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedItem is not null;

    public string EditTaskText
    {
        get => _editTaskText;
        set => SetProperty(ref _editTaskText, value);
    }

    public string EditResponsible
    {
        get => _editResponsible;
        set => SetProperty(ref _editResponsible, value);
    }

    public DateTimeOffset? EditDeadline
    {
        get => _editDeadline;
        set => SetProperty(ref _editDeadline, value);
    }

    public string EditStatus
    {
        get => _editStatus;
        set => SetProperty(ref _editStatus, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;
        WarningText = string.Empty;
        StatusText = string.Empty;
        SelectedItem = null;
        _allItems.Clear();
        FilteredItems.Clear();
        Meetings.Clear();

        try
        {
            if (!await _services.Backend.CheckReadyAsync(cancellationToken))
            {
                ErrorText = "API недоступен. Проверьте backend и подключение.";
                return;
            }

            if (!_services.Backend.HasSession)
            {
                StatusText = "Войдите в API, чтобы загрузить поручения.";
                return;
            }

            var meetings = await LoadAllMeetingsAsync(cancellationToken);
            foreach (var meeting in meetings) Meetings.Add(meeting);

            var loaded = new List<(DesktopMeeting Meeting, IReadOnlyList<DesktopTask> Tasks)>();
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
                    var tasks = await _services.Backend.GetTasksAsync(meetingId, cancellationToken);
                    lock (loaded) loaded.Add((meeting, tasks));
                }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref failures); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(work);

            foreach (var group in loaded)
                foreach (var task in group.Tasks)
                    _allItems.Add(new TaskRegistryItem(group.Meeting, task));

            ApplyFilters();
            StatusText = _allItems.Count == 0
                ? "Поручений пока нет."
                : $"Загружено поручений: {_allItems.Count}";
            if (failures > 0)
                WarningText = $"Не удалось загрузить задачи для встреч: {failures}. Доступные данные сохранены.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorText = SafeError(ex);
            _allItems.Clear();
            ApplyFilters();
        }
        finally { IsLoading = false; }
    }

    public async Task<bool> SaveSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedItem is null || string.IsNullOrWhiteSpace(EditTaskText)) return false;
        var current = SelectedItem.Task;
        var updated = current with
        {
            Task = EditTaskText.Trim(),
            Responsible = string.IsNullOrWhiteSpace(EditResponsible) ? null : EditResponsible.Trim(),
            Deadline = EditDeadline?.DateTime,
            Status = EditStatus,
        };

        IsSaving = true;
        ErrorText = string.Empty;
        try
        {
            if (!await _services.Backend.UpdateTaskAsync(updated, cancellationToken))
            {
                ErrorText = "API не принял изменения поручения. Локальная строка не изменена.";
                return false;
            }

            SelectedItem.Replace(updated);
            ApplyFilters();
            OnPropertyChanged(nameof(SelectedItem));
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorText = SafeError(ex);
            return false;
        }
        finally { IsSaving = false; }
    }

    private async Task<IReadOnlyList<DesktopMeeting>> LoadAllMeetingsAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var meetings = new List<DesktopMeeting>();
        var offset = 0;
        while (true)
        {
            var page = await _services.Backend.GetMeetingsPageAsync(pageSize, offset, cancellationToken);
            if (page.Count == 0) break;
            meetings.AddRange(page);
            if (page.Count < pageSize) break;
            offset += page.Count;
        }
        return meetings.OrderByDescending(item => item.CreatedAt).ToList();
    }

    private void ApplyFilters()
    {
        var query = SearchText.Trim();
        var responsible = ResponsibleFilter.Trim();
        var now = DateTime.Now.Date;
        var nextWeek = now.AddDays(7);
        var visible = _allItems.Where(item =>
            (string.IsNullOrWhiteSpace(query)
                || item.TaskText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.MeetingTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.ResponsibleText.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(responsible) || item.ResponsibleText.Contains(responsible, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(MeetingFilterId) || item.Meeting.Id == MeetingFilterId)
            && MatchesStatus(item.Task.Status)
            && MatchesDeadline(item, now, nextWeek))
            .OrderByDescending(item => item.IsOverdue)
            .ThenBy(item => item.Task.Deadline ?? DateTime.MaxValue)
            .ThenByDescending(item => item.Meeting.CreatedAt)
            .ToList();

        FilteredItems.Clear();
        foreach (var item in visible) FilteredItems.Add(item);
        OnPropertyChanged(nameof(HasItems));
    }

    public bool HasItems => FilteredItems.Count > 0;

    private bool MatchesStatus(string status) => StatusFilter switch
    {
        "ALL" => true,
        "ACTIVE" => status.Equals("NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase) || status.Equals("OPEN", StringComparison.OrdinalIgnoreCase),
        _ => status.Equals(StatusFilter, StringComparison.OrdinalIgnoreCase),
    };

    private bool MatchesDeadline(TaskRegistryItem item, DateTime now, DateTime nextWeek) => DeadlineFilter switch
    {
        "ALL" => true,
        "OVERDUE" => item.IsOverdue,
        "TODAY" => item.Task.Deadline?.Date == now,
        "NEXT_7_DAYS" => item.Task.Deadline?.Date >= now && item.Task.Deadline?.Date <= nextWeek,
        "NO_DEADLINE" => !item.Task.Deadline.HasValue,
        _ => true,
    };

    private static string SafeError(Exception ex) => ex is HttpRequestException
        ? "Не удалось подключиться к API. Проверьте backend и вход в API."
        : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}

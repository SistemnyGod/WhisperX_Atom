using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private bool _isLoading;
    private bool _apiAvailable;
    private bool _agentAvailable;
    private string _apiStatus = "Проверка API…";
    private string _agentStatus = "Проверка Recorder Agent…";
    private string _recordingStatus = "Проверяется состояние записи…";
    private string _storageText = "Ожидание проверки";
    private string _pendingUploadsText = "—";
    private string _archiveText = "Путь архива будет показан после проверки Agent";
    private string _errorText = string.Empty;
    private string _meetingsMessage = "Войдите в API, чтобы загрузить совещания";

    public HomeViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<DesktopMeeting> RecentMeetings { get; } = [];
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool ApiAvailable { get => _apiAvailable; private set => SetProperty(ref _apiAvailable, value); }
    public bool AgentAvailable { get => _agentAvailable; private set => SetProperty(ref _agentAvailable, value); }
    public string ApiStatus { get => _apiStatus; private set => SetProperty(ref _apiStatus, value); }
    public string AgentStatus { get => _agentStatus; private set => SetProperty(ref _agentStatus, value); }
    public string RecordingStatus { get => _recordingStatus; private set => SetProperty(ref _recordingStatus, value); }
    public string StorageText { get => _storageText; private set => SetProperty(ref _storageText, value); }
    public string PendingUploadsText { get => _pendingUploadsText; private set => SetProperty(ref _pendingUploadsText, value); }
    public string ArchiveText { get => _archiveText; private set => SetProperty(ref _archiveText, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string MeetingsMessage { get => _meetingsMessage; private set => SetProperty(ref _meetingsMessage, value); }
    public bool HasMeetings => RecentMeetings.Count > 0;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;

        try
        {
            var response = await _services.Recorder.GetHealthAsync(cancellationToken);
            AgentAvailable = response.Ok;
            AgentStatus = response.Ok ? "Recorder Agent подключён" : "Recorder Agent сообщил об ошибке";
            RecordingStatus = response.State switch
            {
                "Recording" => "Идёт запись",
                "Paused" => "Запись приостановлена",
                "Finalizing" => "Сохранение и отправка записи",
                _ => "Готов к новой записи"
            };
            if (response.Health is { } health)
            {
                StorageText = FormatStorage(health.FreeBytes, health.TotalBytes);
                PendingUploadsText = health.PendingUploadSessions.ToString(CultureInfo.InvariantCulture);
                ArchiveText = string.IsNullOrWhiteSpace(health.ArchiveRoot) ? "Путь архива не передан Agent" : health.ArchiveRoot;
            }
            else
            {
                StorageText = "Нет данных";
                PendingUploadsText = "—";
            }
        }
        catch (Exception ex)
        {
            AgentAvailable = false;
            AgentStatus = "Recorder Agent недоступен";
            RecordingStatus = "Запись недоступна";
            StorageText = "Ожидание проверки";
            PendingUploadsText = "—";
            ErrorText = SafeError(ex);
        }

        try
        {
            ApiAvailable = await _services.Backend.CheckReadyAsync(cancellationToken);
            ApiStatus = ApiAvailable ? "API доступен" : "API недоступен";
            if (!ApiAvailable)
            {
                MeetingsMessage = "API недоступен. Проверьте подключение в Настройках.";
                RecentMeetings.Clear();
            }
            else if (!_services.Backend.HasSession)
            {
                MeetingsMessage = "Войдите в API, чтобы загрузить совещания";
                RecentMeetings.Clear();
            }
            else
            {
                var meetings = await _services.Backend.GetMeetingsAsync(cancellationToken);
                RecentMeetings.Clear();
                foreach (var meeting in meetings.OrderByDescending(x => x.CreatedAt).Take(7)) RecentMeetings.Add(meeting);
                MeetingsMessage = RecentMeetings.Count == 0 ? "Совещаний пока нет" : string.Empty;
                OnPropertyChanged(nameof(HasMeetings));
            }
        }
        catch (Exception ex)
        {
            ApiAvailable = false;
            ApiStatus = "API недоступен";
            MeetingsMessage = _services.Backend.HasSession ? "Не удалось загрузить совещания" : "Войдите в API, чтобы загрузить совещания";
            ErrorText = string.IsNullOrWhiteSpace(ErrorText) ? SafeError(ex) : ErrorText;
            RecentMeetings.Clear();
            OnPropertyChanged(nameof(HasMeetings));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string FormatStorage(long free, long total) => free <= 0 || total <= 0
        ? "Нет данных"
        : $"{FormatBytes(free)} свободно из {FormatBytes(total)}";

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024L * 1024L) return $"{bytes:N0} Б";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024d / 1024d:N1} МБ";
        return $"{bytes / 1024d / 1024d / 1024d:N1} ГБ";
    }

    private static string SafeError(Exception ex) => ex is TimeoutException
        ? "Сервис не ответил вовремя"
        : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}

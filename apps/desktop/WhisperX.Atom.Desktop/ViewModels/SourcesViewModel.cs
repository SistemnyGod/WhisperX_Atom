using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class SourcesViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private bool _isLoading;
    private bool _agentAvailable;
    private bool _apiAvailable;
    private string _localAgentStatus = "Проверка подключения…";
    private string _recordingState = "—";
    private string _archiveRoot = "—";
    private string _storageText = "—";
    private string _pendingUploadsText = "—";
    private string _selectedMicrophone = "Не выбрано";
    private string _selectedSystemAudio = "Не выбрано";
    private string _backendStatus = "Проверка API…";
    private string _errorText = string.Empty;
    private string _warningText = string.Empty;

    public SourcesViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<AudioDeviceOption> Microphones { get; } = [];
    public ObservableCollection<AudioDeviceOption> SystemAudioDevices { get; } = [];
    public ObservableCollection<DesktopAgent> RegisteredAgents { get; } = [];

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool AgentAvailable { get => _agentAvailable; private set => SetProperty(ref _agentAvailable, value); }
    public bool ApiAvailable { get => _apiAvailable; private set => SetProperty(ref _apiAvailable, value); }
    public string LocalAgentStatus { get => _localAgentStatus; private set => SetProperty(ref _localAgentStatus, value); }
    public string RecordingState { get => _recordingState; private set => SetProperty(ref _recordingState, value); }
    public string ArchiveRoot { get => _archiveRoot; private set => SetProperty(ref _archiveRoot, value); }
    public string StorageText { get => _storageText; private set => SetProperty(ref _storageText, value); }
    public string PendingUploadsText { get => _pendingUploadsText; private set => SetProperty(ref _pendingUploadsText, value); }
    public string SelectedMicrophone { get => _selectedMicrophone; private set => SetProperty(ref _selectedMicrophone, value); }
    public string SelectedSystemAudio { get => _selectedSystemAudio; private set => SetProperty(ref _selectedSystemAudio, value); }
    public string BackendStatus { get => _backendStatus; private set => SetProperty(ref _backendStatus, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string WarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }
    public bool HasMicrophones => Microphones.Count > 0;
    public bool HasSystemAudioDevices => SystemAudioDevices.Count > 0;
    public bool HasRegisteredAgents => RegisteredAgents.Count > 0;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorText = string.Empty;
        WarningText = string.Empty;

        var recorderTask = LoadRecorderAsync(cancellationToken);
        var backendTask = LoadBackendAsync(cancellationToken);
        await Task.WhenAll(recorderTask, backendTask);

        var issues = new[] { recorderTask.Result, backendTask.Result }
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();
        if (issues.Length == 2) ErrorText = string.Join(" ", issues);
        else if (issues.Length == 1) WarningText = issues[0]!;
        IsLoading = false;
    }

    private async Task<string?> LoadRecorderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _services.Recorder.GetHealthAsync(cancellationToken);
            AgentAvailable = response.Ok;
            LocalAgentStatus = AgentStatusFormatter.Format(response);
            RecordingState = TranslateState(response.State);
            ApplyHealth(response.Health);
            return response.Ok ? null : response.Error ?? "Recorder Agent не готов к работе.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AgentAvailable = false;
            LocalAgentStatus = "Recorder Agent недоступен";
            RecordingState = "Недоступно";
            ClearDevices();
            return SafeError(ex, "Не удалось получить состояние Recorder Agent.");
        }
    }

    private async Task<string?> LoadBackendAsync(CancellationToken cancellationToken)
    {
        try
        {
            ApiAvailable = await _services.Backend.CheckReadyAsync(cancellationToken);
            if (!ApiAvailable)
            {
                BackendStatus = "API недоступен";
                RegisteredAgents.Clear();
                OnPropertyChanged(nameof(HasRegisteredAgents));
                return "Список зарегистрированных агентов недоступен: API не отвечает.";
            }

            if (!_services.Backend.HasSession)
            {
                BackendStatus = "API доступен, требуется вход";
                RegisteredAgents.Clear();
                OnPropertyChanged(nameof(HasRegisteredAgents));
                return "Войдите в API в настройках, чтобы увидеть зарегистрированные агенты.";
            }

            var agents = await _services.Backend.GetAgentsAsync(cancellationToken);
            RegisteredAgents.Clear();
            foreach (var agent in agents.OrderBy(item => item.Name)) RegisteredAgents.Add(agent);
            BackendStatus = $"API подключён · агентов: {RegisteredAgents.Count}";
            OnPropertyChanged(nameof(HasRegisteredAgents));
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ApiAvailable = false;
            BackendStatus = "Не удалось загрузить агенты";
            RegisteredAgents.Clear();
            OnPropertyChanged(nameof(HasRegisteredAgents));
            return SafeError(ex, "Не удалось загрузить зарегистрированные агенты.");
        }
    }

    private void ApplyHealth(AgentIpcHealth? health)
    {
        if (health is null)
        {
            ArchiveRoot = "Нет данных";
            StorageText = "Нет данных";
            PendingUploadsText = "—";
            ClearDevices();
            return;
        }

        ArchiveRoot = string.IsNullOrWhiteSpace(health.ArchiveRoot) ? "Не настроен" : health.ArchiveRoot;
        StorageText = FormatStorage(health.FreeBytes, health.TotalBytes);
        PendingUploadsText = health.PendingUploadSessions.ToString();
        ReplaceDevices(Microphones, health.CaptureDevices);
        ReplaceDevices(SystemAudioDevices, health.RenderDevices);
        SelectedMicrophone = ResolveSelectedDevice(Microphones, health.SelectedMicrophoneDeviceId);
        SelectedSystemAudio = ResolveSelectedDevice(SystemAudioDevices, health.SelectedSystemAudioDeviceId);
        OnPropertyChanged(nameof(HasMicrophones));
        OnPropertyChanged(nameof(HasSystemAudioDevices));
    }

    private void ClearDevices()
    {
        Microphones.Clear();
        SystemAudioDevices.Clear();
        SelectedMicrophone = "Недоступно";
        SelectedSystemAudio = "Недоступно";
        OnPropertyChanged(nameof(HasMicrophones));
        OnPropertyChanged(nameof(HasSystemAudioDevices));
    }

    private static void ReplaceDevices(ObservableCollection<AudioDeviceOption> target, IReadOnlyList<AgentIpcAudioDevice>? source)
    {
        target.Clear();
        foreach (var device in source ?? []) target.Add(new AudioDeviceOption(device.Id, device.Name, device.IsDefault, device.State));
    }

    private static string ResolveSelectedDevice(IEnumerable<AudioDeviceOption> devices, string? selectedId)
    {
        var selected = devices.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        return selected?.DisplayName ?? "Windows по умолчанию";
    }

    private static string TranslateState(string state) => state switch
    {
        "Recording" => "Идёт запись",
        "Paused" => "Запись приостановлена",
        "Finalizing" => "Сохранение записи",
        "Error" => "Ошибка записи",
        _ => "Готов к записи"
    };

    private static string FormatStorage(long free, long total) => free <= 0 || total <= 0
        ? "Нет данных"
        : $"{FormatBytes(free)} свободно из {FormatBytes(total)}";

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024L * 1024L => $"{bytes:N0} Б",
        < 1024L * 1024L * 1024L => $"{bytes / 1024d / 1024d:N1} МБ",
        _ => $"{bytes / 1024d / 1024d / 1024d:N1} ГБ"
    };

    private static string SafeError(Exception ex, string fallback) => UiErrorFormatter.Format(ex, fallback);
}

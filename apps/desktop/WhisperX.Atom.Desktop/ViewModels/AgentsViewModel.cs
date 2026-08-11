using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class AgentsViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private bool _isLoading;
    private bool _apiAvailable;
    private bool _localAgentAvailable;
    private string _apiStatus = "Проверка API…";
    private string _localAgentStatus = "Проверка Recorder Agent…";
    private string _archiveRoot = "—";
    private string _storageText = "—";
    private string _pendingUploadsText = "—";
    private string _microphoneText = "—";
    private string _systemAudioText = "—";
    private string _errorText = string.Empty;
    private string _warningText = string.Empty;
    private DesktopAgent? _selectedAgent;

    public AgentsViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<DesktopAgent> Agents { get; } = [];

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool ApiAvailable { get => _apiAvailable; private set => SetProperty(ref _apiAvailable, value); }
    public bool LocalAgentAvailable { get => _localAgentAvailable; private set => SetProperty(ref _localAgentAvailable, value); }
    public string ApiStatus { get => _apiStatus; private set => SetProperty(ref _apiStatus, value); }
    public string LocalAgentStatus { get => _localAgentStatus; private set => SetProperty(ref _localAgentStatus, value); }
    public string ArchiveRoot { get => _archiveRoot; private set => SetProperty(ref _archiveRoot, value); }
    public string StorageText { get => _storageText; private set => SetProperty(ref _storageText, value); }
    public string PendingUploadsText { get => _pendingUploadsText; private set => SetProperty(ref _pendingUploadsText, value); }
    public string MicrophoneText { get => _microphoneText; private set => SetProperty(ref _microphoneText, value); }
    public string SystemAudioText { get => _systemAudioText; private set => SetProperty(ref _systemAudioText, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string WarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }

    public DesktopAgent? SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            if (!SetProperty(ref _selectedAgent, value)) return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasAgents => Agents.Count > 0;
    public bool HasSelection => SelectedAgent is not null;
    public string AgentCountText => Agents.Count.ToString();
    public string OnlineCountText => Agents.Count(agent => IsOnline(agent.Status)).ToString();

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
            LocalAgentAvailable = response.Ok;
            LocalAgentStatus = AgentStatusFormatter.Format(response);
            ApplyHealth(response.Health);
            return response.Ok ? null : response.Error ?? "Recorder Agent не готов к работе.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LocalAgentAvailable = false;
            LocalAgentStatus = "Recorder Agent недоступен";
            ClearHealth();
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
                ApiStatus = "API недоступен";
                ClearAgents();
                return "Реестр агентов недоступен: API не отвечает.";
            }

            if (!_services.Backend.HasSession)
            {
                ApiStatus = "API доступен, требуется вход";
                ClearAgents();
                return "Войдите в API в настройках, чтобы увидеть зарегистрированные агенты.";
            }

            var agents = await _services.Backend.GetAgentsAsync(cancellationToken);
            Agents.Clear();
            foreach (var agent in agents.OrderBy(item => item.Name)) Agents.Add(agent);
            SelectedAgent = null;
            ApiStatus = $"API подключён · агентов: {Agents.Count}";
            NotifyCollectionStateChanged();
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ApiAvailable = false;
            ApiStatus = "Не удалось загрузить реестр";
            ClearAgents();
            return SafeError(ex, "Не удалось загрузить зарегистрированных агентов.");
        }
    }

    private void ApplyHealth(AgentIpcHealth? health)
    {
        if (health is null)
        {
            ClearHealth();
            return;
        }

        ArchiveRoot = string.IsNullOrWhiteSpace(health.ArchiveRoot) ? "Не настроен" : health.ArchiveRoot;
        StorageText = FormatStorage(health.FreeBytes, health.TotalBytes);
        PendingUploadsText = health.PendingUploadSessions.ToString();
        MicrophoneText = $"{(health.Microphone ? "готов" : "не найден")} · {health.CaptureDeviceCount} устройств";
        SystemAudioText = $"{(health.SystemAudio ? "готов" : "не найден")} · {health.RenderDeviceCount} устройств";
    }

    private void ClearHealth()
    {
        ArchiveRoot = "Нет данных";
        StorageText = "Нет данных";
        PendingUploadsText = "—";
        MicrophoneText = "Нет данных";
        SystemAudioText = "Нет данных";
    }

    private void ClearAgents()
    {
        Agents.Clear();
        SelectedAgent = null;
        NotifyCollectionStateChanged();
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(AgentCountText));
        OnPropertyChanged(nameof(OnlineCountText));
    }

    private static bool IsOnline(string status) => status.Equals("ONLINE", StringComparison.OrdinalIgnoreCase)
        || status.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

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

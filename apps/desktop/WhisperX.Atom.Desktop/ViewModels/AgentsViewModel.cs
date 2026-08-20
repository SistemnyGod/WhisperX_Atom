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
    private string _retentionText = "Проверка очистки…";
    private string _retentionWarningText = string.Empty;
    private string _microphoneText = "—";
    private string _systemAudioText = "—";
    private string _processingStatus = "Проверка WhisperX…";
    private string _gpuStatus = "GPU: проверка…";
    private string _summaryStatus = "Воркер саммари: проверка…";
    private string _errorText = string.Empty;
    private string _warningText = string.Empty;
    private string _searchText = string.Empty;
    private DesktopAgent? _selectedAgent;

    public AgentsViewModel(FrontendServices services) => _services = services;

    public ObservableCollection<DesktopAgent> Agents { get; } = [];
    public ObservableCollection<DesktopAgent> FilteredAgents { get; } = [];

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool ApiAvailable { get => _apiAvailable; private set => SetProperty(ref _apiAvailable, value); }
    public bool LocalAgentAvailable { get => _localAgentAvailable; private set => SetProperty(ref _localAgentAvailable, value); }
    public string ApiStatus { get => _apiStatus; private set => SetProperty(ref _apiStatus, value); }
    public string LocalAgentStatus { get => _localAgentStatus; private set => SetProperty(ref _localAgentStatus, value); }
    public string ArchiveRoot { get => _archiveRoot; private set => SetProperty(ref _archiveRoot, value); }
    public string StorageText { get => _storageText; private set => SetProperty(ref _storageText, value); }
    public string PendingUploadsText { get => _pendingUploadsText; private set => SetProperty(ref _pendingUploadsText, value); }
    public string RetentionText { get => _retentionText; private set => SetProperty(ref _retentionText, value); }
    public string RetentionWarningText { get => _retentionWarningText; private set => SetProperty(ref _retentionWarningText, value); }
    public string MicrophoneText { get => _microphoneText; private set => SetProperty(ref _microphoneText, value); }
    public string SystemAudioText { get => _systemAudioText; private set => SetProperty(ref _systemAudioText, value); }
    public string ProcessingStatus { get => _processingStatus; private set => SetProperty(ref _processingStatus, value); }
    public string GpuStatus { get => _gpuStatus; private set => SetProperty(ref _gpuStatus, value); }
    public string SummaryStatus { get => _summaryStatus; private set => SetProperty(ref _summaryStatus, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public string WarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ApplyFilter();
        }
    }

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
    public bool HasVisibleAgents => FilteredAgents.Count > 0;
    public bool HasSelection => SelectedAgent is not null;
    public string AgentCountText => Agents.Count.ToString();
    public string OnlineCountText => Agents.Count(agent => agent.IsActive).ToString();
    public string UnavailableCountText => Agents.Count(agent => !agent.IsActive && !string.Equals(agent.EffectiveStatus, "REVOKED", StringComparison.OrdinalIgnoreCase)).ToString();
    public string RevokedCountText => Agents.Count(agent => string.Equals(agent.EffectiveStatus, "REVOKED", StringComparison.OrdinalIgnoreCase)).ToString();

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
            LocalAgentAvailable = response.IsReachable;
            LocalAgentStatus = AgentStatusFormatter.Format(response);
            ApplyHealth(response.Health);
            return response.IsReachable ? null : response.Error ?? "Recorder Agent не готов к работе.";
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
                ClearReadiness();
                return "Реестр агентов недоступен: API не отвечает.";
            }

            if (!_services.Backend.HasSession)
            {
                ApiStatus = "API доступен, требуется вход";
                ClearAgents();
                ClearReadiness();
                return "Войдите в API в настройках, чтобы увидеть зарегистрированные агенты.";
            }

            var agents = await _services.Backend.GetAgentsAsync(cancellationToken);
            Agents.Clear();
            foreach (var agent in agents.OrderBy(item => item.Name)) Agents.Add(agent);
            ApplyFilter();
            SelectedAgent = null;
            ApiStatus = $"API подключён · агентов: {Agents.Count}";
            try
            {
                ApplyReadiness(await _services.Backend.GetProcessingReadinessAsync(cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Component readiness is diagnostic enrichment. A temporary
                // readiness failure must not hide a successfully loaded agent registry.
                ClearReadiness();
            }
            NotifyCollectionStateChanged();
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ApiAvailable = false;
            ApiStatus = "Не удалось загрузить реестр";
            ClearAgents();
            ClearReadiness();
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
        var reclaimed = health.StorageRetentionRawBytesReclaimed
            + health.StorageRetentionTransportBytesReclaimed
            + health.StorageRetentionPlayableBytesReclaimed
            + health.StorageRetentionArchiveBytesReclaimed
            + health.StorageRetentionTemporaryBytesReclaimed;
        RetentionText = health.StorageRetentionLastRunAtUtc is null
            ? "Ещё не запускалась"
            : $"Последний проход: {health.StorageRetentionLastRunAtUtc.Value.ToLocalTime():dd.MM.yyyy HH:mm} · освобождено {FormatBytes(reclaimed)}";
        RetentionWarningText = health.StorageRetentionFailures > 0
            ? $"Ошибки отдельных операций: {health.StorageRetentionFailures} · повтор будет выполнен автоматически"
            : "Ошибок retention нет";
    }

    private void ClearHealth()
    {
        ArchiveRoot = "Нет данных";
        StorageText = "Нет данных";
        PendingUploadsText = "—";
        MicrophoneText = "Нет данных";
        SystemAudioText = "Нет данных";
        RetentionText = "Нет данных";
        RetentionWarningText = string.Empty;
    }

    private void ClearAgents()
    {
        Agents.Clear();
        FilteredAgents.Clear();
        SelectedAgent = null;
        NotifyCollectionStateChanged();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        FilteredAgents.Clear();
        foreach (var agent in Agents.Where(agent =>
                     string.IsNullOrWhiteSpace(query)
                     || agent.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || agent.EffectiveStatusText.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || agent.RoomText.Contains(query, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(agent => agent.Name))
            FilteredAgents.Add(agent);

        if (SelectedAgent is not null && !FilteredAgents.Contains(SelectedAgent)) SelectedAgent = null;
        OnPropertyChanged(nameof(HasVisibleAgents));
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(HasVisibleAgents));
        OnPropertyChanged(nameof(AgentCountText));
        OnPropertyChanged(nameof(OnlineCountText));
        OnPropertyChanged(nameof(UnavailableCountText));
        OnPropertyChanged(nameof(RevokedCountText));
    }

    private void ApplyReadiness(DesktopProcessingReadiness? readiness)
    {
        if (readiness is null)
        {
            ClearReadiness();
            return;
        }

        var components = readiness.Components;
        var gpu = ComponentStatus(components, "cuda");
        var worker = ComponentStatus(components, "workers", "gpu-worker");
        var summary = ComponentStatus(components, "qwen");
        GpuStatus = $"GPU: {TranslateReadiness(gpu)} · worker: {TranslateReadiness(worker)}";
        SummaryStatus = $"Воркер саммари: {TranslateReadiness(summary)}";
        ProcessingStatus = readiness.Ready
            ? "WhisperX готов к обработке"
            : "WhisperX требует внимания — откройте диагностику компонентов";
    }

    private void ClearReadiness()
    {
        ProcessingStatus = "WhisperX: нет данных о готовности";
        GpuStatus = "GPU: нет данных";
        SummaryStatus = "Воркер саммари: нет данных";
    }

    private static string ComponentStatus(System.Text.Json.JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var part in path)
        {
            if (current.ValueKind != System.Text.Json.JsonValueKind.Object || !current.TryGetProperty(part, out current)) return "UNKNOWN";
        }
        if (current.ValueKind == System.Text.Json.JsonValueKind.Object && current.TryGetProperty("status", out var status) && status.ValueKind == System.Text.Json.JsonValueKind.String)
            return status.GetString() ?? "UNKNOWN";
        return current.ValueKind == System.Text.Json.JsonValueKind.String ? current.GetString() ?? "UNKNOWN" : "UNKNOWN";
    }

    private static string TranslateReadiness(string value) => value.ToUpperInvariant() switch
    {
        "READY" => "готов",
        "BUSY" => "занят",
        "DEGRADED" => "ограничен",
        "DISABLED" => "отключён",
        "UNAVAILABLE" => "недоступен",
        _ => "неизвестно"
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

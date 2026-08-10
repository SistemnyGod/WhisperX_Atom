using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class RecordingViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private RecordingState _state = RecordingState.Checking;
    private string _title = "Новая запись";
    private string? _sessionId;
    private Guid? _meetingId;
    private long? _mediaTimeMs;
    private string _statusMessage = "Проверяю Recorder Agent и устройства…";
    private string _errorMessage = string.Empty;
    private string _archiveRoot;
    private int _pendingUploads;
    private string? _microphoneDeviceId;
    private string? _systemAudioDeviceId;

    public RecordingViewModel(FrontendServices services)
    {
        _services = services;
        var settings = services.Settings.Load();
        _archiveRoot = string.IsNullOrWhiteSpace(settings.ArchiveRoot) ? DesktopSettings.DefaultArchiveRoot() : settings.ArchiveRoot!;
        _microphoneDeviceId = settings.MicrophoneDeviceId;
        _systemAudioDeviceId = settings.SystemAudioDeviceId;
    }

    public ObservableCollection<AudioDeviceOption> Microphones { get; } = [];
    public ObservableCollection<AudioDeviceOption> SystemAudioDevices { get; } = [];
    public RecordingState State { get => _state; private set { if (SetProperty(ref _state, value)) NotifyCommands(); } }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string? SessionId { get => _sessionId; private set => SetProperty(ref _sessionId, value); }
    public Guid? MeetingId { get => _meetingId; private set => SetProperty(ref _meetingId, value); }
    public long? MediaTimeMs { get => _mediaTimeMs; private set { if (SetProperty(ref _mediaTimeMs, value)) OnPropertyChanged(nameof(MediaTimeLabel)); } }
    public string MediaTimeLabel => MediaTimeMs is long value ? FormatMediaTime(value) : "00:00:00";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string ArchiveRoot { get => _archiveRoot; private set => SetProperty(ref _archiveRoot, value); }
    public string? SelectedMicrophoneId => _microphoneDeviceId;
    public string? SelectedSystemAudioId => _systemAudioDeviceId;
    public string PendingUploadsLabel => _pendingUploads == 0 ? "Нет ожидающих отправки" : $"В очереди отправки: {_pendingUploads}";
    public string MicrophoneStatus { get; private set; } = "Микрофон: ожидает проверки";
    public string SystemAudioStatus { get; private set; } = "Системный звук: ожидает проверки";
    public string AgentStatus => State == RecordingState.Unavailable ? "Recorder Agent недоступен" : "Recorder Agent подключён";
    public string StateTitle => State switch
    {
        RecordingState.Checking => "Проверка устройств",
        RecordingState.Recording => "Идёт запись",
        RecordingState.Paused => "Запись приостановлена",
        RecordingState.Finalizing => "Сохранение записи",
        RecordingState.Unavailable => "Recorder Agent недоступен",
        RecordingState.Error => "Ошибка записи",
        _ => "Готово к записи"
    };
    public bool CanStart => State is RecordingState.Idle or RecordingState.Error;
    public bool CanPause => State == RecordingState.Recording;
    public bool CanResume => State == RecordingState.Paused;
    public bool CanMark => State is RecordingState.Recording or RecordingState.Paused;
    public bool CanStop => State is RecordingState.Recording or RecordingState.Paused;
    public bool CanSelectDevices => State is RecordingState.Idle or RecordingState.Checking or RecordingState.Error;

    public async Task StartPollingAsync()
    {
        await RefreshAsync();
        if (_pollCts is not null) return;
        _pollCts = new CancellationTokenSource();
        _pollTask = PollLoopAsync(_pollCts.Token);
    }

    public async Task StopPollingAsync()
    {
        if (_pollCts is null) return;
        _pollCts.Cancel();
        try { if (_pollTask is not null) await _pollTask; } catch (OperationCanceledException) { }
        _pollTask = null;
        _pollCts.Dispose();
        _pollCts = null;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplyResponse(await _services.Recorder.GetHealthAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            State = RecordingState.Unavailable;
            StatusMessage = "Подключите Recorder Agent и повторите проверку устройств.";
            ErrorMessage = SafeError(ex);
            OnPropertyChanged(nameof(AgentStatus));
        }
    }

    public async Task<bool> StartRecordingAsync()
    {
        if (!CanStart) return false;
        ErrorMessage = string.Empty;
        State = RecordingState.Checking;
        StatusMessage = "Синхронизирую настройки и запускаю запись…";
        try
        {
            await SyncConfigurationAsync();
            var response = await _services.Recorder.StartAsync(string.IsNullOrWhiteSpace(Title) ? "Новая запись" : Title.Trim());
            ApplyResponse(response);
            if (!response.Ok) ErrorMessage = response.Error ?? "Recorder Agent не запустил запись.";
            return response.Ok;
        }
        catch (Exception ex)
        {
            State = RecordingState.Error;
            StatusMessage = "Проверьте устройства и сообщение об ошибке.";
            ErrorMessage = SafeError(ex);
            return false;
        }
    }

    public Task<bool> PauseAsync() => ExecuteCommandAsync(_services.Recorder.PauseAsync);
    public Task<bool> ResumeAsync() => ExecuteCommandAsync(_services.Recorder.ResumeAsync);
    public Task<bool> AddMarkerAsync() => ExecuteCommandAsync(_services.Recorder.AddMarkerAsync);

    public async Task<bool> StopRecordingAsync()
    {
        if (!CanStop) return false;
        try
        {
            ErrorMessage = string.Empty;
            State = RecordingState.Finalizing;
            StatusMessage = "Сохраняю локальный архив и запускаю доставку…";
            var response = await _services.Recorder.StopAsync();
            SessionId = response.SessionId;
            MeetingId = response.MeetingId;
            if (!response.Ok)
            {
                State = RecordingState.Error;
                ErrorMessage = response.Error ?? "Не удалось завершить запись.";
                return false;
            }
            ApplyResponse(response);
            return true;
        }
        catch (Exception ex)
        {
            State = RecordingState.Error;
            ErrorMessage = SafeError(ex);
            return false;
        }
    }

    public async Task<bool> RetryUploadAsync()
    {
        if (string.IsNullOrWhiteSpace(SessionId)) return false;
        try
        {
            var response = await _services.Recorder.RetryUploadAsync(SessionId);
            ErrorMessage = response.Ok ? string.Empty : response.Error ?? "Повторная отправка ещё не завершена.";
            await RefreshAsync();
            return response.Ok;
        }
        catch (Exception ex) { ErrorMessage = SafeError(ex); return false; }
    }

    public async Task SetMicrophoneAsync(string? id)
    {
        if (!CanSelectDevices || string.Equals(_microphoneDeviceId, id, StringComparison.OrdinalIgnoreCase)) return;
        _microphoneDeviceId = NormalizeDeviceId(id);
        OnPropertyChanged(nameof(SelectedMicrophoneId));
        await SaveAndSyncDevicesAsync();
    }

    public async Task SetSystemAudioAsync(string? id)
    {
        if (!CanSelectDevices || string.Equals(_systemAudioDeviceId, id, StringComparison.OrdinalIgnoreCase)) return;
        _systemAudioDeviceId = NormalizeDeviceId(id);
        OnPropertyChanged(nameof(SelectedSystemAudioId));
        await SaveAndSyncDevicesAsync();
    }

    public async Task SetArchiveRootAsync(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        Directory.CreateDirectory(fullPath);
        ArchiveRoot = fullPath;
        SaveSettings();
        try { var response = await _services.Recorder.SetArchiveRootAsync(fullPath); ErrorMessage = response.Ok ? string.Empty : response.Error ?? "Agent не подтвердил путь архива."; }
        catch (Exception ex) { ErrorMessage = $"Путь сохранён в Desktop, но Agent не синхронизирован: {SafeError(ex)}"; }
    }

    private async Task<bool> ExecuteCommandAsync(Func<CancellationToken, Task<AgentIpcResponse>> command)
    {
        try
        {
            var response = await command(CancellationToken.None);
            ApplyResponse(response);
            if (!response.Ok) ErrorMessage = response.Error ?? "Команда Recorder Agent не выполнена.";
            return response.Ok;
        }
        catch (Exception ex) { ErrorMessage = SafeError(ex); State = RecordingState.Error; return false; }
    }

    private async Task SyncConfigurationAsync()
    {
        await _services.Recorder.SetArchiveRootAsync(ArchiveRoot);
        await _services.Recorder.SetAudioDevicesAsync(_microphoneDeviceId, _systemAudioDeviceId);
        SaveSettings();
    }

    private async Task SaveAndSyncDevicesAsync()
    {
        SaveSettings();
        try { var response = await _services.Recorder.SetAudioDevicesAsync(_microphoneDeviceId, _systemAudioDeviceId); ErrorMessage = response.Ok ? string.Empty : response.Error ?? "Agent не подтвердил устройства."; await RefreshAsync(); }
        catch (Exception ex) { ErrorMessage = SafeError(ex); }
    }

    private void SaveSettings()
    {
        var current = _services.Settings.Load();
        _services.Settings.Save(current with { ArchiveRoot = ArchiveRoot, MicrophoneDeviceId = _microphoneDeviceId, SystemAudioDeviceId = _systemAudioDeviceId });
    }

    private void ApplyResponse(AgentIpcResponse response)
    {
        ErrorMessage = response.Ok ? string.Empty : response.Error ?? "Recorder Agent сообщил об ошибке.";
        State = response.Ok ? ParseState(response.State) : RecordingState.Error;
        SessionId = response.SessionId ?? SessionId;
        MeetingId = response.MeetingId ?? MeetingId;
        MediaTimeMs = response.MediaTimeMs;
        if (response.Health is { } health)
        {
            _pendingUploads = health.PendingUploadSessions;
            OnPropertyChanged(nameof(PendingUploadsLabel));
            ArchiveRoot = string.IsNullOrWhiteSpace(health.ArchiveRoot) ? ArchiveRoot : health.ArchiveRoot!;
            _microphoneDeviceId ??= health.SelectedMicrophoneDeviceId;
            _systemAudioDeviceId ??= health.SelectedSystemAudioDeviceId;
            UpdateDevices(health.CaptureDevices, Microphones, _microphoneDeviceId, "Микрофон не найден");
            UpdateDevices(health.RenderDevices, SystemAudioDevices, _systemAudioDeviceId, "Источник системного звука не найден");
            MicrophoneStatus = health.Microphone ? $"Микрофон готов · устройств: {health.CaptureDeviceCount}" : "Микрофон не найден";
            SystemAudioStatus = health.SystemAudio ? $"Системный звук готов · устройств: {health.RenderDeviceCount}" : "Системный звук не найден";
            OnPropertyChanged(nameof(MicrophoneStatus));
            OnPropertyChanged(nameof(SystemAudioStatus));
        }
        StatusMessage = State switch
        {
            RecordingState.Recording => "Запись идёт. Метки сохраняются в локальном архиве.",
            RecordingState.Paused => "Запись приостановлена. Можно продолжить или завершить.",
            RecordingState.Finalizing => "Локальная копия сохраняется, затем Agent повторит отправку.",
            RecordingState.Error => "Проверьте сообщение об ошибке и повторите действие.",
            RecordingState.Unavailable => "Подключите Recorder Agent и повторите проверку.",
            _ => "Устройства готовы. Можно начать новую запись."
        };
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(AgentStatus));
    }

    private static void UpdateDevices(IReadOnlyList<AgentIpcAudioDevice>? source, ObservableCollection<AudioDeviceOption> target, string? selectedId, string unavailableLabel)
    {
        target.Clear();
        target.Add(new AudioDeviceOption(string.Empty, "Windows по умолчанию", true, "Active"));
        if (source is null) return;
        if (!string.IsNullOrWhiteSpace(selectedId) && source.All(x => !string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
            target.Add(new AudioDeviceOption(selectedId, unavailableLabel, false, "Unavailable"));
        foreach (var device in source) target.Add(new AudioDeviceOption(device.Id, device.Name, device.IsDefault, device.State));
    }

    private static RecordingState ParseState(string? state) => state?.ToUpperInvariant() switch
    {
        "RECORDING" => RecordingState.Recording,
        "PAUSED" => RecordingState.Paused,
        "FINALIZING" => RecordingState.Finalizing,
        "ERROR" => RecordingState.Error,
        _ => RecordingState.Idle
    };

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanMark));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSelectDevices));
        OnPropertyChanged(nameof(AgentStatus));
    }

    private static string FormatMediaTime(long milliseconds) => TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss");
    private static string? NormalizeDeviceId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    private static string SafeError(Exception ex) => ex is TimeoutException ? "Recorder Agent не ответил вовремя" : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}

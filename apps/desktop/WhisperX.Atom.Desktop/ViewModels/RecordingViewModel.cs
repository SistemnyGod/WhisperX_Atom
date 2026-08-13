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
    private string? _confirmedMicrophoneDeviceId;
    private string? _confirmedSystemAudioDeviceId;
    private string _recordingProfile = "ROOM";
    private bool _recordingProfileManaged;
    private CancellationTokenSource? _processingCts;
    private Task? _processingTask;
    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private bool _serverProcessingExpected;
    private bool _hasAudioSource;
    private bool _isProcessing;
    private int _processingProgress;
    private int _rawChunksPending;
    private int _rawChunksFailed;
    private long _rawChunksBytes;
    private double? _microphoneDb;
    private double? _systemAudioDb;
    private double? _microphoneRmsDb;
    private double? _systemAudioRmsDb;
    private bool? _microphoneClipping;
    private bool? _systemAudioClipping;
    private bool _microphoneTelemetryStale = true;
    private bool _systemAudioTelemetryStale = true;
    private IReadOnlyList<double> _microphoneWaveform = Array.Empty<double>();
    private IReadOnlyList<double> _systemAudioWaveform = Array.Empty<double>();
    private string _microphoneTestStatus = "Микрофон ещё не проверен.";
    private string _systemAudioTestStatus = "Системный звук ещё не проверен.";
    private bool _deviceListRefreshInProgress;
    private string _processingStatus = "После остановки здесь появится статус WhisperX.";
    private string _transcriptStatus = "Стенограмма ещё не запущена.";
    private string _processingError = string.Empty;
    private string _warningMessage = string.Empty;
    private AgentIpcResponse? _lastAgentResponse;
    private string? _archivePath;
    private string _localFinalizeState = "PENDING";
    private string _deliveryState = "NOT_STARTED";
    private string? _sessionErrorCode;
    private bool _sessionRetryable;
    private int _chunksTotal;
    private int _chunksConfirmed;
    private int _chunksReady;
    private int _chunksUploading;
    private int _chunksFailed;
    private long _bytesPending;
    private double? _oldestPendingAgeSeconds;
    private int _backgroundPendingSessions;
    private int _backgroundFailedSessions;
    private int _rawChunksReady;
    private int _rawChunksReadyForUpload;
    private string _storageWatermarkState = "NORMAL";
    private double _storageFreePercent;

    public RecordingViewModel(FrontendServices services)
    {
        _services = services;
        var settings = services.Settings.Load();
        _archiveRoot = string.IsNullOrWhiteSpace(settings.ArchiveRoot) ? DesktopSettings.DefaultArchiveRoot() : settings.ArchiveRoot!;
        _microphoneDeviceId = settings.MicrophoneDeviceId;
        _systemAudioDeviceId = settings.SystemAudioDeviceId;
        _confirmedMicrophoneDeviceId = settings.MicrophoneDeviceId;
        _confirmedSystemAudioDeviceId = settings.SystemAudioDeviceId;
        _recordingProfile = NormalizeRecordingProfile(settings.RecordingProfile);
    }

    public ObservableCollection<AudioDeviceOption> Microphones { get; } = [];
    public ObservableCollection<AudioDeviceOption> SystemAudioDevices { get; } = [];
    public IReadOnlyList<RecordingProfileOption> RecordingProfiles { get; } =
    [
        new("ROOM", "Комната — микрофон"),
        new("ONLINE", "Онлайн — микрофон + система"),
        new("MIC_ONLY", "Только микрофон"),
        new("SYSTEM_ONLY", "Только системный звук")
    ];
    public RecordingState State { get => _state; private set { if (SetProperty(ref _state, value)) { NotifyCommands(); OnPropertyChanged(nameof(CanTestAudio)); } } }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string? SessionId
    {
        get => _sessionId;
        private set
        {
            if (SetProperty(ref _sessionId, value)) OnPropertyChanged(nameof(CanRetryUpload));
        }
    }
    public Guid? MeetingId { get => _meetingId; private set => SetProperty(ref _meetingId, value); }
    public long? MediaTimeMs { get => _mediaTimeMs; private set { if (SetProperty(ref _mediaTimeMs, value)) OnPropertyChanged(nameof(MediaTimeLabel)); } }
    public string MediaTimeLabel => MediaTimeMs is long value ? FormatMediaTime(value) : "00:00:00";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string WarningMessage { get => _warningMessage; private set { if (SetProperty(ref _warningMessage, value)) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);
    public string ArchiveRoot { get => _archiveRoot; private set => SetProperty(ref _archiveRoot, value); }
    public string? ArchivePath { get => _archivePath; private set { if (SetProperty(ref _archivePath, value)) OnPropertyChanged(nameof(CanOpenLocalArchive)); } }
    public string LocalFinalizeState { get => _localFinalizeState; private set { if (SetProperty(ref _localFinalizeState, value)) OnPropertyChanged(nameof(LocalFinalizeStatusLabel)); } }
    public string DeliveryState { get => _deliveryState; private set { if (SetProperty(ref _deliveryState, value)) OnPropertyChanged(nameof(DeliveryStatusLabel)); } }
    public string LocalFinalizeStatusLabel => _localFinalizeState.ToUpperInvariant() switch
    {
        "FINALIZING_LOCAL" => "Локальный master собирается",
        "LOCAL_READY" => "Локальный master сохранён",
        "LOCAL_FAILED" => "Локальная сборка не завершена",
        _ => "Локальное сохранение ожидает"
    };
    public string DeliveryStatusLabel => DisplayDeliveryState(_deliveryState);
    public string ChunkSyncLabel => _chunksTotal == 0
        ? "Чанки: пока не созданы"
        : $"Чанки: {_chunksConfirmed} / {_chunksTotal} подтверждено · ожидают: {_chunksReady + _chunksUploading + _chunksFailed}";
    public string PendingBytesLabel => _bytesPending <= 0
        ? "Ожидающих данных: нет"
        : $"Ожидают отправки: {FormatBytes(_bytesPending)}";
    public string PendingAgeLabel => _oldestPendingAgeSeconds is not double age
        ? "Возраст очереди: —"
        : $"Старейший ожидающий чанк: {TimeSpan.FromSeconds(age):g}";
    public string BackgroundDeliveryLabel => _backgroundPendingSessions == 0 && _backgroundFailedSessions == 0
        ? "Фоновых сессий доставки нет"
        : $"Фоновые сессии: {_backgroundPendingSessions} в доставке · {_backgroundFailedSessions} с ошибками";
    public string RawReadyLabel => _rawChunksReadyForUpload == 0
        ? "Закодированные чанки: нет ожидающих отправки"
        : $"Закодированные чанки: {_rawChunksReadyForUpload} ожидают отправки";
    public string RawEncoderReadyLabel => _rawChunksReady == 0
        ? "Сырой PCM: очередь на кодирование пуста"
        : $"Сырой PCM: {_rawChunksReady} чанков готовы к кодированию";
    public bool CanOpenLocalArchive => !string.IsNullOrWhiteSpace(ArchivePath) && Directory.Exists(ArchivePath);
    public string? SelectedMicrophoneId => _microphoneDeviceId;
    public string? SelectedSystemAudioId => _systemAudioDeviceId;
    public string PendingUploadsLabel => _pendingUploads == 0 ? "Нет ожидающих отправки" : $"В очереди отправки: {_pendingUploads}";
    public string EncoderBacklogLabel => _rawChunksPending == 0
        ? "Кодирование аудио: очередь пуста"
        : _rawChunksFailed > 0
            ? $"Кодирование аудио: {_rawChunksPending} чанк(ов) ожидают · ошибок: {_rawChunksFailed}"
            : $"Кодирование аудио: {_rawChunksPending} чанк(ов) ожидают · {FormatBytes(_rawChunksBytes)}";
    public string StorageWatermarkLabel => _storageWatermarkState switch
    {
        "BLOCK_RECORDING" => $"Хранилище: запись заблокирована · свободно {_storageFreePercent:F1}%",
        "CRITICAL" => $"Хранилище: критический уровень · свободно {_storageFreePercent:F1}%",
        "WARNING" => $"Хранилище: внимание · свободно {_storageFreePercent:F1}%",
        _ => $"Хранилище: нормально · свободно {_storageFreePercent:F1}%"
    };
    public ObservableCollection<DesktopTranscriptSegment> TranscriptSegments { get; } = [];
    public bool IsProcessing { get => _isProcessing; private set => SetProperty(ref _isProcessing, value); }
    public int ProcessingProgress { get => _processingProgress; private set => SetProperty(ref _processingProgress, value); }
    public string ProcessingStatus
    {
        get => _processingStatus;
        private set
        {
            if (!SetProperty(ref _processingStatus, value)) return;
            OnPropertyChanged(nameof(ProcessingStageIndex));
        }
    }
    public IReadOnlyList<string> ProcessingSteps { get; } = ["ASR", "Выравнивание", "Диаризация", "Стенограмма"];
    public int ProcessingStageIndex => ProcessingStatus.Contains("стенограм", StringComparison.OrdinalIgnoreCase) ? 3
        : ProcessingStatus.Contains("диариз", StringComparison.OrdinalIgnoreCase) ? 2
        : ProcessingStatus.Contains("выравн", StringComparison.OrdinalIgnoreCase) ? 1
        : ProcessingStatus.Contains("ASR", StringComparison.OrdinalIgnoreCase) ? 0
        : -1;
    public string TranscriptStatus { get => _transcriptStatus; private set => SetProperty(ref _transcriptStatus, value); }
    public string ProcessingError { get => _processingError; private set => SetProperty(ref _processingError, value); }
    public bool HasTranscript => TranscriptSegments.Count > 0;
    public bool CanOpenTranscript => MeetingId is Guid && HasTranscript;
    public string TranscriptPreview => string.Join(" ", TranscriptSegments.Take(3).Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
    public string MicrophoneStatus { get; private set; } = "Микрофон: ожидает проверки";
    public string SystemAudioStatus { get; private set; } = "Системный звук: ожидает проверки";
    public double MicrophoneLevel => ToLevel(_microphoneDb);
    public double SystemAudioLevel => ToLevel(_systemAudioDb);
    public string MicrophoneDbLabel => FormatDb(_microphoneDb);
    public string SystemAudioDbLabel => FormatDb(_systemAudioDb);
    public string MicrophoneTelemetryLabel => FormatTelemetry(_microphoneRmsDb, _microphoneClipping, _microphoneTelemetryStale);
    public string SystemAudioTelemetryLabel => FormatTelemetry(_systemAudioRmsDb, _systemAudioClipping, _systemAudioTelemetryStale);
    public IReadOnlyList<double> MicrophoneWaveform => _microphoneWaveform;
    public IReadOnlyList<double> SystemAudioWaveform => _systemAudioWaveform;
    public bool MicrophoneTelemetryStale => _microphoneTelemetryStale;
    public bool SystemAudioTelemetryStale => _systemAudioTelemetryStale;
    public string MicrophoneTestStatus { get => _microphoneTestStatus; private set => SetProperty(ref _microphoneTestStatus, value); }
    public string SystemAudioTestStatus { get => _systemAudioTestStatus; private set => SetProperty(ref _systemAudioTestStatus, value); }
    public bool IsDeviceListRefreshInProgress => _deviceListRefreshInProgress;
    public bool CanTestAudio => State is not (RecordingState.Starting or RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing);
    // A previously confirmed Agent/owner pair may continue local-first
    // capture while the LAN server is offline. It is still not advertised as
    // server-ready, but the persisted owner makes the local path safe.
    public bool AgentReady => _services.AgentBootstrap.IsReady || _services.AgentBootstrap.LastStatus.OfflineEligible;
    public string AgentStatus => _lastAgentResponse is { } response
        ? AgentStatusFormatter.Format(response)
        : State == RecordingState.Unavailable ? "Recorder Agent недоступен" : "Проверка Recorder Agent…";
    public string StateTitle => State switch
    {
        RecordingState.Checking => "Проверка устройств",
        RecordingState.Starting => "Запуск захвата",
        RecordingState.Recording => "Идёт запись",
        RecordingState.Paused => "Запись приостановлена",
        RecordingState.Finalizing => "Сохранение записи",
        RecordingState.Unavailable => "Recorder Agent недоступен",
        RecordingState.Error => "Ошибка записи",
        _ => "Готово к записи"
    };
    public bool CanStart => (State is RecordingState.Idle or RecordingState.Error) && _hasAudioSource && AgentReady;
    public bool CanPause => State == RecordingState.Recording;
    public bool CanResume => State == RecordingState.Paused;
    public bool CanMark => State is RecordingState.Recording or RecordingState.Paused;
    public bool CanStop => State is RecordingState.Recording or RecordingState.Paused;
    public bool CanRetryUpload => _sessionRetryable && !string.IsNullOrWhiteSpace(SessionId) && State is (RecordingState.Idle or RecordingState.Error or RecordingState.Finalizing);
    // Local capture has already stopped in Finalizing; encoding and delivery run in the
    // background and must not prevent configuring the next recording.
    public bool CanSelectDevices => State is not (RecordingState.Starting or RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing);
    public string RecordingProfile { get => _recordingProfile; private set => SetProperty(ref _recordingProfile, value); }
    public bool RecordingProfileManaged { get => _recordingProfileManaged; private set => SetProperty(ref _recordingProfileManaged, value); }
    public bool CanSelectRecordingProfile => !_recordingProfileManaged && State is not (RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing);

    public async Task StartPollingAsync()
    {
        await RefreshAsync();
        if (_pollCts is not null) return;
        _pollCts = new CancellationTokenSource();
        _pollTask = PollLoopAsync(_pollCts.Token);
    }

    public async Task StopPollingAsync()
    {
        await StopSessionTrackingAsync();
        if (_pollCts is not null)
        {
            _pollCts.Cancel();
            try { if (_pollTask is not null) await _pollTask; } catch (OperationCanceledException) { }
            _pollTask = null;
            _pollCts.Dispose();
            _pollCts = null;
        }
        await StopProcessingPollingAsync();
    }

    private async Task StopSessionTrackingAsync()
    {
        if (_sessionCts is null) return;
        _sessionCts.Cancel();
        try { if (_sessionTask is not null) await _sessionTask; } catch (OperationCanceledException) { }
        _sessionTask = null;
        _sessionCts.Dispose();
        _sessionCts = null;
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
            OnPropertyChanged(nameof(AgentReady));
            OnPropertyChanged(nameof(CanStart));
            if (_services.Backend.HasSession && !AgentReady && State is (RecordingState.Idle or RecordingState.Error))
            {
                StatusMessage = _services.AgentBootstrap.LastStatus.Message;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (RecorderIpcException ex) when (ex.Transient && (State is RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing))
        {
            WarningMessage = "Recorder Agent занят другой операцией; текущая запись продолжается.";
        }
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
        // A new recording reuses this ViewModel. Stop observers for the
        // previous session before clearing its UI state; the Agent continues
        // delivering that session in the background, but its tracker must not
        // overwrite the new recording's status or transcript.
        await StopSessionTrackingAsync();
        await StopProcessingPollingAsync();
        ErrorMessage = string.Empty;
        WarningMessage = string.Empty;
        ArchivePath = null;
        LocalFinalizeState = "PENDING";
        DeliveryState = "NOT_STARTED";
        _sessionErrorCode = null;
        _sessionRetryable = false;
        ProcessingError = string.Empty;
        TranscriptStatus = "Стенограмма ещё не запущена.";
        TranscriptSegments.Clear();
        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(TranscriptPreview));
        OnPropertyChanged(nameof(CanOpenTranscript));
        State = RecordingState.Checking;
        StatusMessage = "Проверяю Recorder Agent и запускаю запись…";
        try
        {
            // Device/profile/archive changes are persisted and acknowledged
            // when changed and during Agent bootstrap. Replaying all commands
            // here serialized START behind IPC/SQLite round trips and caused
            // false timeout errors.
            await RefreshAsync();
            var preflight = await _services.Recorder.PreflightAsync();
            if (!preflight.Ok || preflight.Preflight is null || !preflight.Preflight.Ready)
            {
                State = RecordingState.Error;
                ErrorMessage = preflight.Preflight is { Errors.Count: > 0 }
                    ? string.Join("; ", preflight.Preflight.Errors.Select(MapRecordingError))
                    : MapRecordingError(preflight.Error ?? "Проверка перед записью не пройдена.");
                WarningMessage = preflight.Preflight is { Warnings.Count: > 0 }
                    ? string.Join("; ", preflight.Preflight.Warnings.Select(MapRecordingError))
                    : string.Empty;
                return false;
            }
            if (!_hasAudioSource)
            {
                State = RecordingState.Error;
                ErrorMessage = "Не найден ни один доступный источник аудио.";
                return false;
            }

            var title = string.IsNullOrWhiteSpace(Title) ? "Новая запись" : Title.Trim();
            Guid? serverMeetingId = null;
            var bootstrap = _services.AgentBootstrap.LastStatus;
            if (!bootstrap.Ready && !bootstrap.OfflineEligible)
            {
                State = RecordingState.Error;
                ErrorMessage = MapRecordingError(bootstrap.Code);
                StatusMessage = bootstrap.Message;
                return false;
            }
            if (!bootstrap.Ready)
            {
                WarningMessage = bootstrap.Message;
            }

            var ownerUserId = _services.Settings.Load().OwnerUserId;

            // Online intent gets a real server Meeting before START. The Agent
            // only receives this server-issued id; it never invents one. If the
            // server is unavailable, retain the local-first path with a null id.
            if (bootstrap.Ready && bootstrap.ServerConnected && _services.Backend.HasSession)
            {
                try
                {
                    var meeting = await _services.Backend.CreateMeetingAsync(title, cancellationToken: CancellationToken.None);
                    if (Guid.TryParse(meeting.Id, out var createdMeetingId))
                    {
                        serverMeetingId = createdMeetingId;
                        MeetingId = createdMeetingId;
                        _serverProcessingExpected = true;
                    }
                    else
                    {
                        WarningMessage = "Сервер не вернул корректный MeetingId. Запись продолжится локально.";
                    }
                }
                catch (Exception ex)
                {
                    if (!bootstrap.OfflineEligible)
                    {
                        State = RecordingState.Error;
                        ErrorMessage = MapRecordingError("SERVER_UNAVAILABLE");
                        StatusMessage = SafeError(ex);
                        return false;
                    }
                    WarningMessage = "Сервер недоступен. Запись сохранится локально и будет отправлена после восстановления связи.";
                }
            }

            var response = await _services.Recorder.StartAsync(title, serverMeetingId, ownerUserId);
            ApplyResponse(response);
            if (!response.Ok) ErrorMessage = MapRecordingError(response.Error ?? "Recorder Agent не запустил запись.");
            if (!response.Ok && serverMeetingId.HasValue)
            {
                try { await _services.Backend.CancelMeetingAsync(serverMeetingId.Value, CancellationToken.None); } catch { }
                MeetingId = null;
            }
            if (response.Ok && MeetingId is null) MeetingId = serverMeetingId;
            if (response.Ok && response.MeetingId is Guid agentMeetingId)
            {
                MeetingId ??= agentMeetingId;
                _serverProcessingExpected = _services.Backend.HasSession;
            }
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
    public Task<bool> AddMarkerAsync(string eventType = "MARKER") =>
        ExecuteCommandAsync(cancellationToken => _services.Recorder.AddMarkerAsync(eventType, cancellationToken));

    public async Task<bool> StopRecordingAsync()
    {
        if (!CanStop) return false;
        try
        {
            ErrorMessage = string.Empty;
            State = RecordingState.Finalizing;
            StatusMessage = "Сохраняю локальный архив и запускаю доставку…";
            var response = await _services.Recorder.StopAsync();
            SessionId = response.SessionId ?? SessionId;
            MeetingId = response.MeetingId ?? MeetingId;
            if (!response.Ok)
            {
                State = RecordingState.Error;
                ErrorMessage = MapRecordingError(response.Error ?? "Не удалось завершить запись.");
                return false;
            }
            ApplyResponse(response);
            if (!string.IsNullOrWhiteSpace(SessionId))
                StartSessionTracking(SessionId);
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
            ApplyResponse(response);
            if (!response.Ok && response.SessionStatus is null)
                ErrorMessage = MapRecordingError(response.Error);
            if (!string.IsNullOrWhiteSpace(SessionId)) StartSessionTracking(SessionId);
            return response.Ok;
        }
        catch (Exception ex) { ErrorMessage = SafeError(ex); return false; }
    }

    public async Task SetMicrophoneAsync(string? id)
    {
        if (!CanSelectDevices || string.Equals(_microphoneDeviceId, id, StringComparison.OrdinalIgnoreCase)) return;
        var previous = _confirmedMicrophoneDeviceId;
        _microphoneDeviceId = NormalizeDeviceId(id);
        OnPropertyChanged(nameof(SelectedMicrophoneId));
        await SaveAndSyncDevicesAsync(previous, _confirmedSystemAudioDeviceId, microphoneChanged: true, systemChanged: false);
    }

    public async Task SetSystemAudioAsync(string? id)
    {
        if (!CanSelectDevices || string.Equals(_systemAudioDeviceId, id, StringComparison.OrdinalIgnoreCase)) return;
        var previous = _confirmedSystemAudioDeviceId;
        _systemAudioDeviceId = NormalizeDeviceId(id);
        OnPropertyChanged(nameof(SelectedSystemAudioId));
        await SaveAndSyncDevicesAsync(_confirmedMicrophoneDeviceId, previous, microphoneChanged: false, systemChanged: true);
    }

    public async Task SetRecordingProfileAsync(string? profile)
    {
        if (!CanSelectRecordingProfile) return;
        var normalized = NormalizeRecordingProfile(profile);
        if (string.Equals(_recordingProfile, normalized, StringComparison.Ordinal)) return;
        try
        {
            var response = await _services.Recorder.SetRecordingProfileAsync(normalized);
            if (!response.Ok)
            {
                ErrorMessage = MapRecordingError(response.Error ?? "Не удалось сохранить профиль записи.");
                return;
            }
            RecordingProfile = normalized;
            SaveSettings();
            ApplyResponse(response);
        }
        catch (Exception ex) { ErrorMessage = SafeError(ex); }
    }

    public async Task TestMicrophoneAsync()
    {
        if (!CanTestAudio) return;
        MicrophoneTestStatus = "Проверяю микрофон…";
        try
        {
            var response = await _services.Recorder.TestAudioSourceAsync(_microphoneDeviceId);
            var result = response.AudioSourceTest;
            MicrophoneTestStatus = result is null
                ? "Не удалось получить результат проверки."
                : !result.Success
                    ? $"Аудиопакеты не получены: {result.ErrorCode ?? "AUDIO_TEST_FAILED"}."
                    : !result.SignalDetected
                        ? "Сигнал не обнаружен."
                        : result.Clipping
                            ? $"Сигнал обнаружен, но есть clipping. Пик: {result.PeakDb:0} dB."
                            : $"Микрофон работает. Средний уровень: {result.AverageRmsDb:0} dB, пик: {result.PeakDb:0} dB.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MicrophoneTestStatus = $"Проверка не выполнена: {SafeError(ex)}";
        }
    }

    public async Task TestSystemAudioAsync()
    {
        if (!CanTestAudio) return;
        SystemAudioTestStatus = "Проверяю системный звук…";
        try
        {
            var response = await _services.Recorder.TestAudioSourceAsync(_systemAudioDeviceId, systemAudio: true);
            var result = response.AudioSourceTest;
            SystemAudioTestStatus = result is null
                ? "Не удалось получить результат проверки."
                : !result.Success
                    ? $"Аудиопакеты не получены: {result.ErrorCode ?? "AUDIO_TEST_FAILED"}."
                    : !result.SignalDetected
                        ? $"Поток открыт: пакетов {result.PacketCount}, но сигнал пока не обнаружен."
                        : $"Системный звук работает. Пик: {result.PeakDb:0} dB.";
            await RefreshAsync();
        }
        catch (Exception ex) { SystemAudioTestStatus = $"Проверка не выполнена: {SafeError(ex)}"; }
    }

    public async Task SetArchiveRootAsync(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        Directory.CreateDirectory(fullPath);
        ArchiveRoot = fullPath;
        SaveSettings();
        try { var response = await _services.Recorder.SetArchiveRootAsync(fullPath); ErrorMessage = response.Ok ? string.Empty : MapRecordingError(response.Error ?? "Agent не подтвердил путь архива."); }
        catch (Exception ex) { ErrorMessage = $"Путь сохранён в Desktop, но Agent не синхронизирован: {SafeError(ex)}"; }
    }

    private async Task<bool> ExecuteCommandAsync(Func<CancellationToken, Task<AgentIpcResponse>> command)
    {
        try
        {
            var response = await command(CancellationToken.None);
            ApplyResponse(response);
            if (!response.Ok) ErrorMessage = MapRecordingError(response.Error ?? "Команда Recorder Agent не выполнена.");
            return response.Ok;
        }
        catch (Exception ex) { ErrorMessage = SafeError(ex); State = RecordingState.Error; return false; }
    }

    private async Task SaveAndSyncDevicesAsync(string? previousMicrophoneId, string? previousSystemAudioId, bool microphoneChanged, bool systemChanged)
    {
        try
        {
            var response = await _services.Recorder.SetAudioDevicesAsync(_microphoneDeviceId, _systemAudioDeviceId);
            if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Agent не подтвердил устройства.");
            ApplyResponse(response);
            await RefreshAsync();
            var confirmed = response.Health ?? _lastAgentResponse?.Health;
            var microphoneConfirmed = !microphoneChanged || string.Equals(
                NormalizeDeviceId(confirmed?.SelectedMicrophoneDeviceId), _microphoneDeviceId, StringComparison.OrdinalIgnoreCase);
            var systemConfirmed = !systemChanged || string.Equals(
                NormalizeDeviceId(confirmed?.SelectedSystemAudioDeviceId), _systemAudioDeviceId, StringComparison.OrdinalIgnoreCase);
            if (!microphoneConfirmed || !systemConfirmed)
                throw new InvalidOperationException("DEVICE_SELECTION_NOT_CONFIRMED");
            _confirmedMicrophoneDeviceId = _microphoneDeviceId;
            _confirmedSystemAudioDeviceId = _systemAudioDeviceId;
            SaveSettings();
            ErrorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _microphoneDeviceId = _confirmedMicrophoneDeviceId = previousMicrophoneId;
            _systemAudioDeviceId = _confirmedSystemAudioDeviceId = previousSystemAudioId;
            OnPropertyChanged(nameof(SelectedMicrophoneId));
            OnPropertyChanged(nameof(SelectedSystemAudioId));
            ErrorMessage = ex is InvalidOperationException { Message: "DEVICE_SELECTION_NOT_CONFIRMED" }
                ? "Recorder Agent не подтвердил выбранное устройство. Возвращено предыдущее значение."
                : SafeError(ex);
            try { await _services.Recorder.SetAudioDevicesAsync(previousMicrophoneId, previousSystemAudioId); } catch { }
        }
    }

    private async Task StartProcessingPollingAsync(Guid meetingId)
    {
        if (IsProcessing) return;
        await StopProcessingPollingAsync();
        ProcessingError = string.Empty;
        ProcessingProgress = 0;
        ProcessingStatus = "Ожидаю подтверждение записи и постановку WhisperX в очередь…";
        TranscriptStatus = "Стенограмма ожидает обработки.";
        IsProcessing = true;
        _processingCts = new CancellationTokenSource();
        _processingTask = PollProcessingAsync(meetingId, _processingCts.Token);
    }

    private async Task StopProcessingPollingAsync()
    {
        if (_processingCts is null) return;
        _processingCts.Cancel();
        try { if (_processingTask is not null) await _processingTask; } catch (OperationCanceledException) { }
        _processingTask = null;
        _processingCts.Dispose();
        _processingCts = null;
        IsProcessing = false;
    }

    private void StartSessionTracking(string sessionId)
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();
        _sessionTask = TrackSessionAsync(sessionId, _sessionCts.Token);
    }

    private async Task TrackSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(24);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (DateTimeOffset.UtcNow < deadline && await timer.WaitForNextTickAsync(cancellationToken))
            {
                var response = await _services.Recorder.GetSessionStatusAsync(sessionId, cancellationToken);
                if (!response.Ok || response.SessionStatus is null) continue;
                var session = response.SessionStatus;
                ApplySessionStatus(session);
                if (session.MeetingId is Guid meetingId)
                {
                    MeetingId = meetingId;
                    _serverProcessingExpected = true;
                    var processingReady = session.ProcessingJobId is Guid
                        || session.DeliveryState is "WAITING_SERVER_ASSEMBLY" or "CONFIRMED" or "COMPLETED";
                    if (processingReady && !IsProcessing)
                    {
                        StatusMessage = "Запись принята сервером. Ожидаю обработку WhisperX…";
                        await StartProcessingPollingAsync(meetingId);
                    }
                }
                // Delivery tracking is independent from processing tracking. A
                // meeting id only proves binding; it does not prove upload or
                // media assembly. Stop this loop only at a delivery terminal state.
                if (session.DeliveryState is "COMPLETED" or "CONFIRMED") return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            WarningMessage = $"Локальная запись сохранена, доставка продолжится автоматически: {SafeError(ex)}";
        }
    }

    private static string DisplayDeliveryState(string state) => state.ToUpperInvariant() switch
    {
        "WAITING_FOR_API" => "ожидание API",
        "BINDING" => "привязка к совещанию",
        "SYNCING" => "синхронизация чанков",
        "WAITING_SERVER_ASSEMBLY" => "сборка на сервере",
        "WAITING_SERVER" => "ожидание обработки на сервере",
        "FINALIZING_SERVER" => "завершение на сервере",
        "RECONCILING" => "проверка чанков",
        "COMPLETED" => "доставлено",
        "DELIVERY_ERROR" or "DELIVERY_FAILED" => "ошибка доставки",
        "NOT_STARTED" => "ожидание отправки",
        _ => "ожидание восстановления"
    };

    private void ApplySessionStatus(RecordingSessionStatus session)
    {
        SessionId = session.SessionId;
        MeetingId = session.MeetingId ?? MeetingId;
        LocalFinalizeState = session.LocalFinalizeState;
        DeliveryState = session.DeliveryState;
        ArchivePath = session.ArchivePath;
        _sessionErrorCode = session.ErrorCode;
        _sessionRetryable = session.Retryable;
        if (string.Equals(session.LocalFinalizeState, "LOCAL_FAILED", StringComparison.OrdinalIgnoreCase)
            && !HasServerAcceptedRecording(session))
        {
            State = RecordingState.Error;
            ErrorMessage = MapRecordingError(session.ErrorCode ?? session.Error);
            WarningMessage = "Исходные аудиочанки сохранены. Исправьте Agent и повторите отправку.";
            StatusMessage = "Не удалось собрать локальный master-файл. Исходные аудиочанки сохранены.";
        }
        else if (session.DeliveryState is "DELIVERY_ERROR" or "DELIVERY_FAILED")
        {
            State = RecordingState.Idle;
            ErrorMessage = string.Empty;
            WarningMessage = MapRecordingError(session.ErrorCode ?? session.Error);
            StatusMessage = "Запись сохранена локально. Сервер пока не подтвердил получение.";
        }
        else if (session.ErrorCode is "AUDIO_SOURCE_FAILED" or "STORAGE_WRITE_FAILED" or "ENCODER_FAILED")
        {
            State = RecordingState.Error;
            ErrorMessage = MapRecordingError(session.ErrorCode);
            WarningMessage = "Запись остановлена безопасно. Уже сохранённые аудиоданные останутся в локальном архиве и будут обработаны recovery-механизмом Agent.";
            StatusMessage = "Запись остановлена из-за ошибки аудиопути.";
        }
        else if (string.Equals(session.LocalFinalizeState, "LOCAL_READY", StringComparison.OrdinalIgnoreCase))
        {
            if (State is RecordingState.Finalizing or RecordingState.Error) State = RecordingState.Idle;
            ErrorMessage = string.Empty;
            WarningMessage = session.DeliveryState is "CONFIRMED" or "COMPLETED"
                ? string.Empty
                : "Локальный master сохранён. Agent продолжает доставку на сервер.";
            ProcessingStatus = $"Доставка записи: {DisplayDeliveryState(session.DeliveryState)}";
            StatusMessage = session.DeliveryState is "CONFIRMED" or "COMPLETED"
                ? "Аудио сохранено и подтверждено сервером."
                : "Локальная запись сохранена; серверная доставка продолжится автоматически.";
        }
        OnPropertyChanged(nameof(CanRetryUpload));
        OnPropertyChanged(nameof(CanOpenLocalArchive));
        _chunksTotal = session.LocalChunkCount;
        _chunksConfirmed = session.ConfirmedChunkCount;
        _chunksReady = session.ChunksReady;
        _chunksUploading = session.ChunksUploading;
        _chunksFailed = session.ChunksFailed;
        _bytesPending = session.BytesPending;
        _oldestPendingAgeSeconds = session.OldestPendingAgeSeconds;
        OnPropertyChanged(nameof(ChunkSyncLabel));
        OnPropertyChanged(nameof(PendingBytesLabel));
        OnPropertyChanged(nameof(PendingAgeLabel));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(AgentStatus));
    }

    private static bool HasServerAcceptedRecording(RecordingSessionStatus session)
        => session.ProcessingJobId is Guid
            || session.DeliveryState is "WAITING_SERVER_ASSEMBLY" or "WAITING_SERVER" or "CONFIRMED" or "COMPLETED";

    private async Task PollProcessingAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(45);
        try
        {
            while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                if (await RefreshProcessingAsync(meetingId, cancellationToken)) return;
                try
                {
                    var jobs = await _services.Backend.GetJobsAsync(meetingId, cancellationToken);
                    var job = jobs.Where(IsTranscriptJob)
                        .OrderByDescending(item => item.Attempt).ThenByDescending(item => item.Progress).FirstOrDefault();
                    if (job is not null && !string.Equals(job.Status, "READY", StringComparison.OrdinalIgnoreCase) && !string.Equals(job.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
                        await _services.Backend.WaitForJobEventsAsync(Guid.Parse(job.Id), cancellationToken);
                }
                catch (DesktopApiException) { /* polling below is the SSE fallback */ }
                catch (HttpRequestException) { /* polling below is the SSE fallback */ }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                ProcessingError = "Время ожидания WhisperX истекло. Откройте совещание для повторной проверки или перезапустите job.";
                ProcessingStatus = "Обработка не подтверждена вовремя";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ProcessingError = $"Не удалось получить состояние обработки: {SafeError(ex)}";
            ProcessingStatus = "Ошибка отслеживания обработки";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsProcessing = false;
        }
    }

    private async Task<bool> RefreshProcessingAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        if (!_services.Backend.HasSession)
        {
            ProcessingError = "Войдите в API, чтобы получить стенограмму после локальной записи.";
            ProcessingStatus = "Запись сохранена локально";
            return true;
        }

        var jobsTask = _services.Backend.GetJobsAsync(meetingId, cancellationToken);
        var transcriptTask = _services.Backend.GetTranscriptAsync(meetingId, cancellationToken);
        await Task.WhenAll(jobsTask, transcriptTask);
        var jobs = await jobsTask;
        var job = jobs.Where(IsTranscriptJob)
            .OrderByDescending(item => item.Attempt)
            .ThenByDescending(item => item.Progress)
            .FirstOrDefault();
        var transcript = await transcriptTask;

        if (job is not null)
        {
            ProcessingProgress = Math.Clamp(job.Progress, 0, 100);
            ProcessingStatus = $"{DisplayStatus(job.Status)} · {DisplayStage(job.Stage)}";
            if (!string.IsNullOrWhiteSpace(job.Error)) ProcessingError = MapProcessingError(job.Error);
            if (string.Equals(job.Status, "FAILED", StringComparison.OrdinalIgnoreCase)) return true;
        }
        else
        {
            ProcessingStatus = "Ожидаю job транскрибации после финализации медиа…";
        }

        if (transcript is not null)
        {
            TranscriptStatus = DisplayTranscriptStatus(transcript.Status);
            if (transcript.Warnings is { } warningDocument && warningDocument.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                WarningMessage = string.Join("; ", warningDocument.RootElement.EnumerateArray().Select(item => MapProcessingError(item.GetString() ?? string.Empty)));
            TranscriptSegments.Clear();
            foreach (var segment in transcript.Segments.OrderBy(item => item.Ordinal)) TranscriptSegments.Add(segment);
            OnPropertyChanged(nameof(HasTranscript));
            OnPropertyChanged(nameof(TranscriptPreview));
            OnPropertyChanged(nameof(CanOpenTranscript));
            if ((transcript.Status is "READY" or "PARTIAL_READY") && HasTranscript)
            {
                ProcessingProgress = 100;
                ProcessingStatus = transcript.IsPartial ? "Стенограмма готова частично" : "Стенограмма готова";
                return true;
            }
        }

        return false;
    }

    private static bool IsTranscriptJob(DesktopJob job) =>
        string.Equals(job.Type, "TRANSCRIBE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(job.Type, "TRANSCRIBE_REPROCESS", StringComparison.OrdinalIgnoreCase);

    private static string DisplayStatus(string status) => status.ToUpperInvariant() switch
    {
        "QUEUED" => "В очереди",
        "RUNNING" => "Выполняется",
        "READY" => "Готово",
        "FAILED" => "Ошибка",
        _ => status
    };

    private static string DisplayStage(string stage) => stage.ToUpperInvariant() switch
    {
        "INGEST" => "подготовка медиа",
        "NORMALIZING" => "нормализация аудио",
        "TRANSCRIBING" => "WhisperX ASR",
        "ALIGNING" => "выравнивание таймкодов",
        "DIARIZING" => "диаризация спикеров",
        "QUALITY_CHECK" => "проверка качества",
        "PERSISTING" => "сохранение стенограммы",
        "READY" => "готово",
        "FAILED" => "ошибка",
        _ => stage
    };

    private static string DisplayTranscriptStatus(string status) => status.ToUpperInvariant() switch
    {
        "PENDING" => "Стенограмма ожидает обработки",
        "RUNNING" => "Стенограмма формируется",
        "READY" => "Стенограмма готова",
        "PARTIAL_READY" => "Готова частичная стенограмма",
        "FAILED" => "Стенограмма не создана",
        _ => status
    };

    private static string MapProcessingError(string value)
    {
        var code = value.ToUpperInvariant();
        if (code == "TRANSCRIPT_EMPTY")
            return "WhisperX did not detect speech. Check the selected microphone and its input level, then record again.";
        return code switch
        {
            "UPLOAD_CONNECTION_LOST" => "Соединение с сервером загрузки потеряно. Загрузка продолжится с последнего подтверждённого блока.",
            "MEDIA_NO_AUDIO" => "В файле не найден аудиосигнал.",
            "MODEL_ACCESS_ERROR" => "Модель WhisperX недоступна. Проверьте HF-токен и права доступа к модели.",
            "CUDA_UNAVAILABLE" => "GPU CUDA недоступна. Проверьте драйвер NVIDIA и конфигурацию workers.",
            "CUDA_OOM" => "На GPU недостаточно видеопамяти для этой модели WhisperX.",
            "TRANSCRIPT_EMPTY" => "WhisperX не получил текст из аудиозаписи.",
            "TRANSCRIPT_OUTSIDE_MEDIA" => "Таймкоды стенограммы выходят за длительность аудио.",
            "ALIGNMENT_FAILED" => "Выравнивание таймкодов не выполнено; доступен частичный текст.",
            "DIARIZATION_FAILED" => "Диаризация не выполнена; спикеры отмечены как «Не определён».",
            "DIARIZATION_DISABLED" => "Диаризация отключена настройками обработки.",
            _ => value
        };
    }

    private void SaveSettings()
    {
        var current = _services.Settings.Load();
        _services.Settings.Save(current with { ArchiveRoot = ArchiveRoot, MicrophoneDeviceId = _microphoneDeviceId, SystemAudioDeviceId = _systemAudioDeviceId, RecordingProfile = _recordingProfile });
    }

    private void ApplyResponse(AgentIpcResponse response)
    {
        _lastAgentResponse = response;
        var parsedState = response.Ok ? ParseState(response.State) : RecordingState.Error;
        ErrorMessage = !response.Ok || parsedState == RecordingState.Error
            ? MapRecordingError(response.Error)
            : string.Empty;
        WarningMessage = response.Ok && parsedState != RecordingState.Error && !string.IsNullOrWhiteSpace(response.Error)
            ? MapRecordingError(response.Error)
            : string.Empty;
        State = response.Ok ? parsedState : RecordingState.Error;
        SessionId = response.SessionId ?? SessionId;
        MeetingId = response.MeetingId ?? MeetingId;
        MediaTimeMs = response.MediaTimeMs;
            if (response.Health is { } health)
        {
            _pendingUploads = health.PendingUploadSessions;
            _hasAudioSource = IsRequiredAudioReady(health, RecordingProfile);
            _rawChunksPending = health.RawChunksPending;
            _rawChunksFailed = health.RawChunksFailed;
            _rawChunksBytes = health.RawChunksBytes;
            _backgroundPendingSessions = health.BackgroundPendingSessions;
            _backgroundFailedSessions = health.BackgroundFailedSessions;
            _rawChunksReady = health.RawChunksReady;
            _rawChunksReadyForUpload = health.RawChunksReadyForUpload;
            _storageWatermarkState = health.StorageWatermarkState;
            _storageFreePercent = health.StorageFreePercent;
            RecordingProfileManaged = health.RecordingProfileManaged;
            RecordingProfile = NormalizeRecordingProfile(health.RecordingProfile);
            _microphoneDb = health.MicrophoneDb;
            _systemAudioDb = health.SystemAudioDb;
            _microphoneRmsDb = health.MicrophoneRmsDb;
            _systemAudioRmsDb = health.SystemAudioRmsDb;
            _microphoneClipping = health.MicrophoneClipping;
            _systemAudioClipping = health.SystemAudioClipping;
            _microphoneTelemetryStale = health.MicrophoneTelemetryStale;
            _systemAudioTelemetryStale = health.SystemAudioTelemetryStale;
            _microphoneWaveform = AppendWaveformSample(_microphoneWaveform, health.MicrophonePeak);
            _systemAudioWaveform = AppendWaveformSample(_systemAudioWaveform, health.SystemAudioPeak);
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(PendingUploadsLabel));
            OnPropertyChanged(nameof(EncoderBacklogLabel));
            OnPropertyChanged(nameof(BackgroundDeliveryLabel));
            OnPropertyChanged(nameof(RawReadyLabel));
            OnPropertyChanged(nameof(RawEncoderReadyLabel));
            OnPropertyChanged(nameof(StorageWatermarkLabel));
            ArchiveRoot = string.IsNullOrWhiteSpace(health.ArchiveRoot) ? ArchiveRoot : health.ArchiveRoot!;
            _microphoneDeviceId ??= health.SelectedMicrophoneDeviceId;
            _systemAudioDeviceId ??= health.SelectedSystemAudioDeviceId;
            if (_confirmedMicrophoneDeviceId is null) _confirmedMicrophoneDeviceId = health.SelectedMicrophoneDeviceId;
            if (_confirmedSystemAudioDeviceId is null) _confirmedSystemAudioDeviceId = health.SelectedSystemAudioDeviceId;
            _deviceListRefreshInProgress = true;
            OnPropertyChanged(nameof(IsDeviceListRefreshInProgress));
            try
            {
                if (UpdateDevices(health.CaptureDevices, Microphones, _microphoneDeviceId, "Микрофон не найден"))
                    OnPropertyChanged(nameof(Microphones));
                if (UpdateDevices(health.RenderDevices, SystemAudioDevices, _systemAudioDeviceId, "Источник системного звука не найден"))
                    OnPropertyChanged(nameof(SystemAudioDevices));
            }
            finally
            {
                _deviceListRefreshInProgress = false;
                OnPropertyChanged(nameof(IsDeviceListRefreshInProgress));
            }
            var micState = health.MicrophoneCaptureState ?? (health.Microphone ? "DISCOVERED" : "DEVICE_LOST");
            var systemState = health.SystemAudioCaptureState ?? (health.SystemAudio ? "DISCOVERED" : "DEVICE_LOST");
            MicrophoneStatus = health.Microphone ? $"Микрофон: {micState} · устройств: {health.CaptureDeviceCount}" : "Микрофон не найден";
            SystemAudioStatus = health.SystemAudio ? $"Системный звук: {systemState} · устройств: {health.RenderDeviceCount}" : "Системный звук не найден";
            OnPropertyChanged(nameof(MicrophoneStatus));
            OnPropertyChanged(nameof(SystemAudioStatus));
            OnPropertyChanged(nameof(MicrophoneLevel));
            OnPropertyChanged(nameof(SystemAudioLevel));
            OnPropertyChanged(nameof(MicrophoneDbLabel));
            OnPropertyChanged(nameof(SystemAudioDbLabel));
            OnPropertyChanged(nameof(MicrophoneTelemetryLabel));
            OnPropertyChanged(nameof(SystemAudioTelemetryLabel));
            OnPropertyChanged(nameof(MicrophoneWaveform));
            OnPropertyChanged(nameof(SystemAudioWaveform));
            OnPropertyChanged(nameof(MicrophoneTelemetryStale));
            OnPropertyChanged(nameof(SystemAudioTelemetryStale));
            }
            OnPropertyChanged(nameof(AgentReady));
            OnPropertyChanged(nameof(CanStart));
        StatusMessage = State switch
        {
            RecordingState.Starting => "Открываю аудиопоток и жду первый принятый буфер…",
            RecordingState.Recording => "Запись идёт. Метки сохраняются в локальном архиве.",
            RecordingState.Paused => "Запись приостановлена. Можно продолжить или завершить.",
            RecordingState.Finalizing => "Локальная копия сохраняется, затем Agent повторит отправку.",
            RecordingState.Error when !string.IsNullOrWhiteSpace(WarningMessage) => "Локальная запись сохранена, серверную доставку можно повторить.",
            RecordingState.Error when string.Equals(_sessionErrorCode, "SERVER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) => "Запись сохранена локально; серверная доставка будет повторена автоматически.",
            RecordingState.Error => "Проверьте сообщение об ошибке и повторите действие.",
            RecordingState.Unavailable => "Подключите Recorder Agent и повторите проверку.",
            _ => "Устройства готовы. Можно начать новую запись."
        };
        if (_services.Backend.HasSession && !AgentReady && State is (RecordingState.Idle or RecordingState.Error))
            StatusMessage = _services.AgentBootstrap.LastStatus.Message;
        if (response.SessionStatus is { } sessionStatus) ApplySessionStatus(sessionStatus);
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(CanRetryUpload));
        OnPropertyChanged(nameof(AgentStatus));
    }

    private static bool UpdateDevices(IReadOnlyList<AgentIpcAudioDevice>? source, ObservableCollection<AudioDeviceOption> target, string? selectedId, string unavailableLabel)
    {
        var desired = new List<AudioDeviceOption> { new(string.Empty, "Windows по умолчанию", true, "Active") };
        if (source is null) source = Array.Empty<AgentIpcAudioDevice>();
        if (!string.IsNullOrWhiteSpace(selectedId) && source.All(x => !string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
            desired.Add(new AudioDeviceOption(selectedId, unavailableLabel, false, "Unavailable"));
        desired.AddRange(source.Select(device => new AudioDeviceOption(device.Id, device.Name, device.IsDefault, device.State)));
        var changed = false;
        for (var index = 0; index < desired.Count; index++)
        {
            var wanted = desired[index];
            var existingIndex = -1;
            for (var candidateIndex = 0; candidateIndex < target.Count; candidateIndex++)
            {
                if (!string.Equals(target[candidateIndex].Id, wanted.Id, StringComparison.OrdinalIgnoreCase)) continue;
                existingIndex = candidateIndex;
                break;
            }
            if (existingIndex >= 0 && existingIndex != index)
            {
                var existing = target[existingIndex];
                target.RemoveAt(existingIndex);
                target.Insert(index, existing == wanted ? existing : wanted);
                changed = true;
            }
            else if (index >= target.Count)
            {
                target.Add(wanted);
                changed = true;
            }
            else if (target[index] != wanted)
            {
                target[index] = wanted;
                changed = true;
            }
        }
        while (target.Count > desired.Count) { target.RemoveAt(target.Count - 1); changed = true; }
        return changed;
    }

    private static bool IsRequiredAudioReady(AgentIpcHealth health, string profile) => profile switch
    {
        "SYSTEM_ONLY" => health.SystemAudioCaptureReady == true,
        "ONLINE" => health.MicrophoneCaptureReady == true && health.SystemAudioCaptureReady == true,
        _ => health.MicrophoneCaptureReady == true
    };

    private static RecordingState ParseState(string? state) => state?.ToUpperInvariant() switch
    {
        "RECORDING" => RecordingState.Recording,
        "STARTING" => RecordingState.Starting,
        "PAUSED" => RecordingState.Paused,
        "FINALIZING" => RecordingState.Finalizing,
        "ERROR" => RecordingState.Error,
        _ => RecordingState.Idle
    };

    private static string MapRecordingError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Recorder Agent сообщил об ошибке.";
        var code = value.Trim().ToUpperInvariant();
        return code switch
        {
            "AUDIO_SOURCE_FAILED" => "Источник аудио остановился. Проверьте подключение микрофона или системного звука.",
            "MICROPHONE_PROBE_REQUIRED" or "microphone_probe_required" => "Микрофон найден, но ещё не подтверждён реальным захватом. Нажмите «Проверить микрофон».",
            "SYSTEM_AUDIO_PROBE_REQUIRED" or "system_audio_probe_required" => "Системный аудиопоток ещё не подтверждён реальным захватом. Нажмите «Проверить системный звук».",
            "AUDIO_NO_DATA" => "Поток открылся, но за время проверки не пришёл ни один аудиопакет.",
            "AUDIO_CALLBACK_TIMEOUT" => "Аудиопоток открылся, но первый буфер не пришёл вовремя. Проверьте устройство и разрешения Windows.",
            "AUDIO_DEVICE_ACCESS_DENIED" => "Windows не разрешила доступ к аудиоустройству. Проверьте разрешение микрофона и классических приложений.",
            "AUDIO_FORMAT_UNSUPPORTED" => "Формат аудиоустройства не поддерживается Recorder Agent.",
            "STORAGE_WRITE_FAILED" => "Не удалось сохранить аудио на диск. Проверьте свободное место и доступ к архиву.",
            "ENCODER_FAILED" => "Не удалось закодировать аудиочанк. Локальные данные сохранены для восстановления.",
            "LOCAL_ENCODING_FAILED" => "Не удалось завершить кодирование локального аудио.",
            "LOCAL_ARCHIVE_FAILED" or "RECORDING_FINALIZE_FAILED" => "Не удалось собрать локальный master-файл. Исходные аудиочанки сохранены.",
            "LOCAL_CHUNK_MISSING" => "В локальном архиве отсутствует аудиочанк. Исходные файлы сохранены для диагностики.",
            "LOCAL_CHUNK_INVALID" => "Локальный аудиочанк повреждён или не прошёл проверку контрольной суммы.",
            "SERVER_BINDING_FAILED" or "SERVER_FINALIZE_REJECTED" => "Сервер не подтвердил привязку или завершение записи. Повторная отправка будет выполнена автоматически.",
            "CHUNK_UPLOAD_FAILED" or "SERVER_UNAVAILABLE" or "SERVER_FINALIZE_PENDING" or "SERVER_FINALIZE_FAILED" or "UPLOAD_PENDING" => "Запись сохранена локально. Сервер пока не подтвердил получение; повторная отправка выполняется автоматически.",
            "AGENT_AUTH_REJECTED" => "Сервер отклонил авторизацию Recorder Agent. Переподключите Agent в настройках.",
            "RECORDING_ARCHIVE_ACCESS_DENIED" => "Нет доступа к папке локального архива.",
            "FFMPEG_UNAVAILABLE" => "Не найден FFmpeg для локальной сборки аудио.",
            "SESSION_REQUIRED" => "Не найдена локальная сессия записи для повторной отправки.",
            "AGENT_USER_LINK_REQUIRED" => "Recorder Agent ещё не привязан к текущему пользователю. Повторите вход или обратитесь к администратору.",
            "DEVICE_SELECTION_NOT_CONFIRMED" => "Recorder Agent не подтвердил выбранное устройство. Предыдущее устройство восстановлено.",
            "AGENT_HEARTBEAT_STALE" => "Recorder Agent не подтвердил свежее подключение к серверу.",
            "AGENT_SERVER_UNAVAILABLE" => "Recorder Agent не подключён к LAN-серверу. Локальная запись останется доступной после подтверждённой привязки.",
            "OWNER_REQUIRED" => "Для серверной записи не определён владелец. Выполните вход в приложение.",
            "OWNER_AUTHORIZATION_REJECTED" => "Пользователь больше не может отправлять эту запись. Локальная копия сохранена.",
            "MEETING_OWNER_MISMATCH" => "Запись принадлежит другому пользователю и не может быть отправлена из этого сеанса.",
            "REENROLL_REQUIRED" => "Recorder Agent отозван. Требуется повторная регистрация устройства.",
            _ when code.Contains("401", StringComparison.OrdinalIgnoreCase) || code.Contains("403", StringComparison.OrdinalIgnoreCase) => "Сервер отклонил авторизацию Recorder Agent. Переподключите Agent в настройках.",
            _ when code.Contains("RECORDING_FINALIZE_FAILED", StringComparison.OrdinalIgnoreCase) => "Не удалось собрать локальный master-файл. Исходные аудиочанки сохранены.",
            _ => "Recorder Agent не завершил операцию. Локальные данные сохранены; откройте диагностику Agent."
        };
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanMark));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRetryUpload));
        OnPropertyChanged(nameof(CanSelectDevices));
        OnPropertyChanged(nameof(CanSelectRecordingProfile));
        OnPropertyChanged(nameof(AgentStatus));
    }

    private static string FormatMediaTime(long milliseconds) => TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss");
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.0} МБ",
        _ => $"{bytes / (1024d * 1024 * 1024):0.0} ГБ"
    };
    private static double ToLevel(double? db) => db is double value ? Math.Clamp((value + 60d) / 60d * 100d, 0d, 100d) : 0d;
    private static string FormatDb(double? db) => db is double value ? $"{value:0} dB peak" : "Нет измерения";
    private static string FormatTelemetry(double? rmsDb, bool? clipping, bool stale)
    {
        if (stale) return "Телеметрия устарела";
        if (rmsDb is not double value) return "Сигнал не обнаружен";
        return clipping == true ? $"RMS {value:0} dB · клиппинг" : $"RMS {value:0} dB · сигнал стабилен";
    }
    private static IReadOnlyList<double> AppendWaveformSample(IReadOnlyList<double> current, double? peak)
    {
        var updated = current.Count >= 64 ? current.Skip(1).ToList() : current.ToList();
        updated.Add(peak is double value ? Math.Clamp(value, 0d, 1d) : 0d);
        return updated;
    }
    private static string? NormalizeDeviceId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    private static string NormalizeRecordingProfile(string? profile)
    {
        var normalized = string.IsNullOrWhiteSpace(profile) ? "ROOM" : profile.Trim().ToUpperInvariant();
        return normalized is "ROOM" or "ONLINE" or "MIC_ONLY" or "SYSTEM_ONLY" ? normalized : "ROOM";
    }
    private static string SafeError(Exception ex) => UiErrorFormatter.Format(ex, "Recorder Agent не ответил. Проверьте локальный сервис.");
}

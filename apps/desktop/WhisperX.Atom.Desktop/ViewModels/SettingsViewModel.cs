using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly FrontendServices _services;
    private string _apiUrl;
    private string _username;
    private string _archiveRoot;
    private string _agentName = "WhisperX Atom Desktop";
    private string _statusText = string.Empty;
    private bool _isBusy;
    private string _diagnosticsPath = string.Empty;
    private string _recorderRuntimeState = "Проверка Host…";
    private string _recorderRuntimePath = "—";
    private string _recorderRuntimeBuild = "—";
    private string _recorderRuntimeIdentity = "—";
    private string _recorderRuntimeOrigin = "—";
    private string _recorderRuntimeError = "—";
    private string _recorderRuntimeProcess = "—";
    private bool _voiceAlwaysListening;
    private bool _voiceQuietMode;
    private string _voiceSensitivity = "balanced";
    private string _voiceName = "Microsoft Irina";
    private int _voiceRate;
    private int _voiceVolume = 90;
    private string _ttsEngine = "SILERO";
    private string _ttsVoice = "aidar";
    private string _ttsVoiceProfile = "MIFODIY_TECH";
    private int _ttsSampleRate = 48000;
    private int _ttsCpuThreads = 4;
    private bool _ttsFallbackEnabled = true;
    private string _windowsFallbackVoice = "Microsoft Irina";
    private int _voiceProcessingGainDb;
    private string _voiceStatus = "Проверка Мифодия…";
    private string _voiceLastRecognition = "—";
    private string _voiceErrorCode = "—";
    private string _voiceMicrophone = "—";
    private string _voiceLevel = "—";
    private double _voiceLevelNormalized;
    private string _voiceSignalState = "Ожидание аудиокадров";
    private string _voiceAcousticDiagnostics = "Автокалибровка: ожидание аудио";
    private string _voiceWakeWordMode = "—";
    private string _voiceRuntimeBuild = "—";
    private string _voiceRuntimeProcess = "—";
    private string _voiceLastTraceId = "—";
    private string _voiceLastCommandId = "—";
    private string _voiceRequestedMicrophone = "—";
    private string _voiceEffectiveMicrophone = "—";
    private string _voiceRuntimePath = "—";
    private string _voiceExpectedBuild = "—";
    private string _voiceObservedBuild = "—";
    private string _voiceDiagnosticsDetail = "—";
    private string _voiceEffectiveVoice = "—";
    private string _voiceTtsStatus = "Движок TTS: —";
    private string _voicePlaybackStatus = "Озвучка: проверяется";
    private bool _voiceFallbackUsed;
    private string _voiceSpeechQueue = "Очередь речи: 0";
    private string _voiceAssistantDelivery = "Доставка ответов: —";
    private string _voiceLiveMode = "Live-контекст: MIC_FALLBACK";
    private string _voiceLiveTracks = "Дорожки: микрофон — ожидание · система — ожидание";
    private string _voiceLiveStats = "Live ASR: опубликовано 0 · подавлено 0 · drops 0";
    private VoiceTelemetryUiState _voiceTelemetry = VoiceTelemetryUiState.Empty;
    private int _voiceRestartCountCache = -1;
    public ObservableCollection<string> VoiceOptions { get; } = new();
    public IReadOnlyList<string> TtsVoiceProfileOptions { get; } = new[] { "MIFODIY_TECH", "CLEAN", "AIDAR_CLEAN" };
    public bool ServerOriginManaged { get; }
    private bool _mustChangePassword;

    public SettingsViewModel(FrontendServices services)
    {
        _services = services;
        var settings = services.Settings.Load();
        var machineConfig = MachineServerConfig.Load();
        ServerOriginManaged = machineConfig?.Managed == true;
        _apiUrl = ServerOriginManaged ? machineConfig!.ServerOrigin : settings.ApiUrl;
        _username = settings.Username;
        _archiveRoot = string.IsNullOrWhiteSpace(settings.ArchiveRoot) ? DesktopSettings.DefaultArchiveRoot() : settings.ArchiveRoot!;
        _voiceAlwaysListening = settings.VoiceAlwaysListening;
        _voiceQuietMode = settings.VoiceQuietMode;
        _voiceSensitivity = settings.VoiceSensitivity;
        _voiceName = settings.VoiceName;
        _voiceRate = settings.VoiceRate;
        _voiceVolume = settings.VoiceVolume;
        _ttsEngine = settings.TtsEngine;
        _ttsVoice = settings.TtsVoice;
        _ttsVoiceProfile = settings.TtsVoiceProfile;
        _ttsSampleRate = settings.TtsSampleRate;
        _ttsCpuThreads = settings.TtsCpuThreads;
        _ttsFallbackEnabled = settings.TtsFallbackEnabled;
        _windowsFallbackVoice = settings.WindowsFallbackVoice;
        _voiceProcessingGainDb = settings.VoiceProcessingGainDb;
    }

    public string ApiUrl { get => _apiUrl; set { if (!ServerOriginManaged) SetProperty(ref _apiUrl, value); } }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string ArchiveRoot { get => _archiveRoot; private set => SetProperty(ref _archiveRoot, value); }
    public string AgentName { get => _agentName; set => SetProperty(ref _agentName, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanLogin));
            OnPropertyChanged(nameof(CanChangePassword));
            OnPropertyChanged(nameof(CanChangeServerOrigin));
            OnPropertyChanged(nameof(CanReconnectAgent));
            OnPropertyChanged(nameof(CanUseActions));
            OnPropertyChanged(nameof(CanLogout));
        }
    }
    public string DiagnosticsPath { get => _diagnosticsPath; private set => SetProperty(ref _diagnosticsPath, value); }
    public string RecorderRuntimeState { get => _recorderRuntimeState; private set => SetProperty(ref _recorderRuntimeState, value); }
    public string RecorderRuntimePath { get => _recorderRuntimePath; private set => SetProperty(ref _recorderRuntimePath, value); }
    public string RecorderRuntimeBuild { get => _recorderRuntimeBuild; private set => SetProperty(ref _recorderRuntimeBuild, value); }
    public string RecorderRuntimeIdentity { get => _recorderRuntimeIdentity; private set => SetProperty(ref _recorderRuntimeIdentity, value); }
    public string RecorderRuntimeOrigin { get => _recorderRuntimeOrigin; private set => SetProperty(ref _recorderRuntimeOrigin, value); }
    public string RecorderRuntimeError { get => _recorderRuntimeError; private set => SetProperty(ref _recorderRuntimeError, value); }
    public string RecorderRuntimeProcess { get => _recorderRuntimeProcess; private set => SetProperty(ref _recorderRuntimeProcess, value); }
    public bool VoiceAlwaysListening { get => _voiceAlwaysListening; set { if (SetProperty(ref _voiceAlwaysListening, value)) _ = ApplyVoiceSettingsAsync(); } }
    public bool VoiceQuietMode
    {
        get => _voiceQuietMode;
        set
        {
            if (!SetProperty(ref _voiceQuietMode, value)) return;
            OnPropertyChanged(nameof(CanEnableVoicePlayback));
            OnPropertyChanged(nameof(VoicePlaybackStatus));
            OnPropertyChanged(nameof(VoiceStatusDetail));
            _ = ApplyVoiceSettingsAsync();
        }
    }
    public string VoiceSensitivity { get => _voiceSensitivity; set { if (SetProperty(ref _voiceSensitivity, value)) _ = ApplyVoiceSettingsAsync(); } }
    public string VoiceName { get => _voiceName; set { if (SetProperty(ref _voiceName, value)) _ = ApplyVoiceSettingsAsync(); } }
    public int VoiceRate { get => _voiceRate; set { var valueToSet = Math.Clamp(value, -10, 10); if (SetProperty(ref _voiceRate, valueToSet)) _ = ApplyVoiceSettingsAsync(); } }
    public int VoiceVolume { get => _voiceVolume; set { var valueToSet = Math.Clamp(value, 0, 100); if (SetProperty(ref _voiceVolume, valueToSet)) _ = ApplyVoiceSettingsAsync(); } }
    public string TtsEngine { get => _ttsEngine; set { if (SetProperty(ref _ttsEngine, value)) _ = ApplyVoiceSettingsAsync(); } }
    public string TtsVoice { get => _ttsVoice; set { if (SetProperty(ref _ttsVoice, value)) _ = ApplyVoiceSettingsAsync(); } }
    public string TtsVoiceProfile { get => _ttsVoiceProfile; set { var normalized = string.Equals(value, "AIDAR_CLEAN", StringComparison.OrdinalIgnoreCase) ? "AIDAR_CLEAN" : string.Equals(value, "CLEAN", StringComparison.OrdinalIgnoreCase) ? "CLEAN" : "MIFODIY_TECH"; if (SetProperty(ref _ttsVoiceProfile, normalized)) _ = ApplyVoiceSettingsAsync(); } }
    public int TtsSampleRate { get => _ttsSampleRate; set { var v = value is 24000 or 48000 ? value : 48000; if (SetProperty(ref _ttsSampleRate, v)) _ = ApplyVoiceSettingsAsync(); } }
    public int TtsCpuThreads { get => _ttsCpuThreads; set { var v = Math.Clamp(value, 1, 32); if (SetProperty(ref _ttsCpuThreads, v)) _ = ApplyVoiceSettingsAsync(); } }
    public bool TtsFallbackEnabled { get => _ttsFallbackEnabled; set { if (SetProperty(ref _ttsFallbackEnabled, value)) _ = ApplyVoiceSettingsAsync(); } }
    public string WindowsFallbackVoice { get => _windowsFallbackVoice; set { if (SetProperty(ref _windowsFallbackVoice, value)) _ = ApplyVoiceSettingsAsync(); } }
    public int VoiceProcessingGainDb
    {
        get => _voiceProcessingGainDb;
        set
        {
            var v = Math.Clamp(value, 0, 18);
            if (!SetProperty(ref _voiceProcessingGainDb, v)) return;
            OnPropertyChanged(nameof(VoiceProcessingGainText));
            _ = ApplyVoiceSettingsAsync();
        }
    }
    public string VoiceProcessingGainText => VoiceProcessingGainDb == 0 ? "Без усиления" : $"+{VoiceProcessingGainDb} дБ";
    public string VoiceEffectiveVoice { get => _voiceEffectiveVoice; private set { if (SetProperty(ref _voiceEffectiveVoice, value)) OnPropertyChanged(nameof(VoiceVoiceStatus)); } }
    public string VoiceTtsStatus
    {
        get => _voiceTtsStatus;
        private set
        {
            if (!SetProperty(ref _voiceTtsStatus, value)) return;
            OnPropertyChanged(nameof(VoicePlaybackStatus));
            OnPropertyChanged(nameof(VoiceStatusDetail));
        }
    }
    public string VoicePlaybackStatus { get => _voicePlaybackStatus; private set => SetProperty(ref _voicePlaybackStatus, value); }
    public bool CanEnableVoicePlayback => VoiceQuietMode;
    public bool VoiceFallbackUsed { get => _voiceFallbackUsed; private set { if (SetProperty(ref _voiceFallbackUsed, value)) OnPropertyChanged(nameof(VoiceVoiceStatus)); } }
    public string VoiceSpeechQueue { get => _voiceSpeechQueue; private set => SetProperty(ref _voiceSpeechQueue, value); }
    public string VoiceAssistantDelivery { get => _voiceAssistantDelivery; private set => SetProperty(ref _voiceAssistantDelivery, value); }
    public string VoiceLiveMode { get => _voiceLiveMode; private set => SetProperty(ref _voiceLiveMode, value); }
    public string VoiceLiveTracks { get => _voiceLiveTracks; private set => SetProperty(ref _voiceLiveTracks, value); }
    public string VoiceLiveStats { get => _voiceLiveStats; private set => SetProperty(ref _voiceLiveStats, value); }
    public string VoiceVoiceStatus => VoiceFallbackUsed ? $"Используется fallback: {VoiceEffectiveVoice}" : $"Используется: {VoiceEffectiveVoice}";
    public string VoiceStatus
    {
        get => _voiceStatus;
        private set
        {
            if (!SetProperty(ref _voiceStatus, value)) return;
            OnPropertyChanged(nameof(VoiceStatusLabel));
            OnPropertyChanged(nameof(VoiceStatusDetail));
        }
    }
    public string VoiceStatusLabel => ToVoiceStatusLabel(VoiceStatus);
    public string VoiceLastRecognition { get => _voiceLastRecognition; private set => SetProperty(ref _voiceLastRecognition, value); }
    public string VoiceErrorCode
    {
        get => _voiceErrorCode;
        private set
        {
            if (!SetProperty(ref _voiceErrorCode, value)) return;
            OnPropertyChanged(nameof(VoiceStatusDetail));
        }
    }
    public string VoiceMicrophone { get => _voiceMicrophone; private set => SetProperty(ref _voiceMicrophone, value); }
    public string VoiceLevel { get => _voiceLevel; private set => SetProperty(ref _voiceLevel, value); }
    public double VoiceLevelNormalized { get => _voiceLevelNormalized; private set => SetProperty(ref _voiceLevelNormalized, value); }
    public string VoiceSignalState { get => _voiceSignalState; private set => SetProperty(ref _voiceSignalState, value); }
    public string VoiceAcousticDiagnostics { get => _voiceAcousticDiagnostics; private set => SetProperty(ref _voiceAcousticDiagnostics, value); }
    public string VoiceWakeWordMode { get => _voiceWakeWordMode; private set => SetProperty(ref _voiceWakeWordMode, value); }
    public string VoiceRuntimeBuild { get => _voiceRuntimeBuild; private set => SetProperty(ref _voiceRuntimeBuild, value); }
    public string VoiceRuntimeProcess { get => _voiceRuntimeProcess; private set => SetProperty(ref _voiceRuntimeProcess, value); }
    public string VoiceLastTraceId { get => _voiceLastTraceId; private set => SetProperty(ref _voiceLastTraceId, value); }
    public string VoiceLastCommandId { get => _voiceLastCommandId; private set => SetProperty(ref _voiceLastCommandId, value); }
    public string VoiceRequestedMicrophone { get => _voiceRequestedMicrophone; private set => SetProperty(ref _voiceRequestedMicrophone, value); }
    public string VoiceEffectiveMicrophone { get => _voiceEffectiveMicrophone; private set => SetProperty(ref _voiceEffectiveMicrophone, value); }
    public string VoiceRuntimePath { get => _voiceRuntimePath; private set => SetProperty(ref _voiceRuntimePath, value); }
    public string VoiceExpectedBuild { get => _voiceExpectedBuild; private set => SetProperty(ref _voiceExpectedBuild, value); }
    public string VoiceObservedBuild { get => _voiceObservedBuild; private set => SetProperty(ref _voiceObservedBuild, value); }
    public string VoiceDiagnosticsDetail { get => _voiceDiagnosticsDetail; private set => SetProperty(ref _voiceDiagnosticsDetail, value); }
    public int VoiceRestartCount => _services.VoiceHost.RestartCount;
    public bool IsLoggedIn => _services.Backend.HasSession;
    public bool IsSessionExpired => _services.Backend.AuthState == DesktopAuthState.LoginRequired;
    public string ApiStatusShort => _services.Backend.AuthState switch
    {
        DesktopAuthState.Authenticated => "Подключено",
        DesktopAuthState.Offline => "Сервер офлайн",
        DesktopAuthState.LoginRequired => "Требуется вход",
        DesktopAuthState.Refreshing => "Проверяется…",
        _ => "Не проверено"
    };
    public bool CanLogin => !IsBusy && !IsLoggedIn;
    public bool CanChangeServerOrigin => !IsBusy && !ServerOriginManaged;
    public bool CanChangePassword => !IsBusy && IsLoggedIn;
    public bool CanReconnectAgent => !IsBusy && IsLoggedIn;
    public bool CanUseActions => !IsBusy;
    public bool CanLogout => !IsBusy && IsLoggedIn;
    public bool MustChangePassword { get => _mustChangePassword; private set => SetProperty(ref _mustChangePassword, value); }
    public string SessionExpiryText => _services.Backend.SessionExpiresAtUtc is { } expires
        ? $"Сессия действительна до {expires.ToLocalTime():dd.MM.yyyy HH:mm}."
        : "Срок сессии проверяется автоматически.";
    public string LoginStatusDetail => _services.Backend.AuthState switch
    {
        DesktopAuthState.Authenticated => "API отвечает; локальная запись и доставка доступны.",
        DesktopAuthState.Offline => "Сервер не отвечает. Локальная запись продолжает работать, доставка возобновится после подключения.",
        DesktopAuthState.LoginRequired => "Сессия истекла или отсутствует. Выполните вход, чтобы использовать Assistant и серверную доставку.",
        DesktopAuthState.Refreshing => "Проверяю защищённую сессию и доступность сервера…",
        _ => "Состояние API ещё не подтверждено."
    };
    public string LoginStatusText => _services.Backend.AuthState switch
    {
        DesktopAuthState.Authenticated => "Вход в API выполнен",
        DesktopAuthState.Offline => "API временно недоступен; локальная запись работает",
        DesktopAuthState.LoginRequired => "Требуется повторный вход в API",
        _ => IsLoggedIn ? "Проверка сессии API…" : "Вход в API не выполнен"
    };
    public string VoiceStatusDetail => GetVoiceStatusDetail();

    public async Task RefreshRecorderDiagnosticsAsync()
    {
        try
        {
            var snapshot = await _services.RecorderService.GetSnapshotAsync().ConfigureAwait(true);
            RecorderRuntimeState = snapshot.PipeReachable ? "Подключён" : snapshot.Exists ? "Найден, но недоступен" : "Не запущен";
            RecorderRuntimePath = string.IsNullOrWhiteSpace(snapshot.BinaryPath) ? "—" : snapshot.BinaryPath!;
            RecorderRuntimeBuild = string.IsNullOrWhiteSpace(snapshot.BuildIdentity) ? snapshot.Version ?? "—" : snapshot.BuildIdentity!;
            RecorderRuntimeOrigin = snapshot.ServerOrigin ?? ApiUrl;
            RecorderRuntimeIdentity = snapshot.AgentId is Guid agent && snapshot.InstallationId is Guid installation
                ? $"Agent {agent} · установка {installation}"
                : "Agent identity не подтверждена";
            RecorderRuntimeProcess = snapshot.ProcessId is int pid ? $"PID {pid}" : "PID —";
            RecorderRuntimeError = string.IsNullOrWhiteSpace(snapshot.Error) ? "—" : snapshot.Error!;
        }
        catch (Exception exception)
        {
            RecorderRuntimeState = "Ошибка проверки";
            RecorderRuntimeError = UiErrorFormatter.Format(exception, "RECORDER_HOST_UNAVAILABLE");
        }
        finally
        {
            RefreshAuthenticationBindings();
        }
    }

    private void RefreshAuthenticationBindings()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(IsSessionExpired));
        OnPropertyChanged(nameof(ApiStatusShort));
        OnPropertyChanged(nameof(LoginStatusText));
        OnPropertyChanged(nameof(LoginStatusDetail));
        OnPropertyChanged(nameof(SessionExpiryText));
        OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(CanChangePassword));
        OnPropertyChanged(nameof(CanReconnectAgent));
        OnPropertyChanged(nameof(CanLogout));
    }

    public async Task RefreshVoiceDiagnosticsAsync()
    {
        try
        {
            RefreshAssistantDeliveryDiagnostics();
            var client = new WhisperX.Atom.Desktop.VoiceHostClient();
            var response = await client.GetStatusAsync().ConfigureAwait(true);
            if (VoiceOptions.Count == 0)
            {
                var voices = await client.SendAsync("LIST_RUSSIAN_VOICES").ConfigureAwait(true);
                if (voices.Ok && voices.Data is JsonElement data && data.TryGetProperty("voices", out var voiceItems) && voiceItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var voice in voiceItems.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)))
                        VoiceOptions.Add(voice!);
                }
                if (VoiceOptions.Count == 0) VoiceOptions.Add("Microsoft Irina");
            }
            if (response is null)
            {
                VoiceEffectiveVoice = "—";
                VoiceFallbackUsed = false;
                VoiceSpeechQueue = "Очередь речи: —";
                VoiceLiveMode = "Live-контекст: недоступен";
                VoiceLiveTracks = "Дорожки: —";
                VoiceLiveStats = "Live ASR: —";
                VoiceAcousticDiagnostics = "Автокалибровка: нет данных";
                VoicePlaybackStatus = VoiceQuietMode
                    ? "Озвучка отключена: тихий режим"
                    : "Озвучка не проверена: Voice Host не запущен";
                var controllerError = _services.VoiceHost.LastErrorCode;
                ApplyVoiceDiagnostics(new VoiceDiagnosticsUiState(
                    controllerError is null ? "Voice Host не запущен" : $"Voice Host: {controllerError}",
                    "—",
                    controllerError ?? "VOICE_HOST_NOT_INSTALLED",
                    "—",
                    "—",
                    "—",
                    "—",
                    "—",
                    _services.VoiceHost.InstalledPath ?? "—",
                    _services.VoiceHost.ExpectedBuildIdentity,
                    _services.VoiceHost.LastObservedBuildIdentity ?? "—",
                    "PID —",
                    "—",
                    "—",
                    _services.VoiceHost.LastErrorDetail ?? "—"));
                RefreshAssistantDeliveryDiagnostics();
                return;
            }
            var heartbeat = response.HeartbeatAtUtc ?? response.UpdatedAt;
            var heartbeatStale = DateTimeOffset.UtcNow - heartbeat.ToUniversalTime() > TimeSpan.FromSeconds(10);
            // QuietMode is additive in the status contract. Keep the local
            // setting when talking to an older host that does not send it.
            if (response.QuietMode && !VoiceQuietMode)
            {
                _voiceQuietMode = true;
                OnPropertyChanged(nameof(VoiceQuietMode));
                OnPropertyChanged(nameof(CanEnableVoicePlayback));
            }
            ApplyVoiceDiagnostics(new VoiceDiagnosticsUiState(
                heartbeatStale
                    ? "Нет heartbeat от Voice Host"
                    : $"{response.State} · модель {(response.ModelReady ? "готова" : "не готова")} · микрофон {(response.MicrophoneReady ? "готов" : "недоступен")}",
                response.LastRecognizedText ?? "—",
                response.LastErrorCode ?? "—",
                response.RequestedMicrophoneDeviceId ?? "DEFAULT",
                response.EffectiveMicrophoneDeviceId ?? "—",
                response.EffectiveMicrophoneName ?? "—",
                response.WakeWordMode ?? "—",
                response.BuildIdentity ?? "—",
                _services.VoiceHost.InstalledPath ?? "—",
                _services.VoiceHost.ExpectedBuildIdentity,
                response.BuildIdentity ?? _services.VoiceHost.LastObservedBuildIdentity ?? "—",
                response.ProcessId is int pid ? $"PID {pid}" : "PID —",
                response.LastTraceId ?? "—",
                response.LastCommandId ?? "—",
                response.MicrophoneErrorDetail ?? _services.VoiceHost.LastErrorDetail ?? "—"));
            VoiceEffectiveVoice = string.IsNullOrWhiteSpace(response.EffectiveVoiceName)
                ? "—"
                : $"{response.EffectiveVoiceName} ({response.EffectiveVoiceCulture ?? "ru-RU"})";
            VoiceFallbackUsed = response.VoiceFallbackUsed;
            var fxState = response.TtsFxEnabled
                ? (response.TtsFxApplied ? "FX включён" : $"чистый fallback{(string.IsNullOrWhiteSpace(response.TtsFxFallbackReason) ? string.Empty : $" ({response.TtsFxFallbackReason})")}")
                : "чистый голос";
            VoiceTtsStatus = $"TTS: {response.TtsEngine} · профиль {response.VoiceProfile} · {response.TtsVoice ?? response.EffectiveVoiceName ?? "—"} · {fxState} · {(response.TtsReady ? "готов" : "fallback/недоступен")}";
            VoicePlaybackStatus = FormatPlaybackStatus(response);
            VoiceSpeechQueue = $"Очередь речи: {response.SpeechQueueDepth} · отброшено: {response.SpeechQueueDrops}";
            VoiceLiveMode = response.LiveAudioMode switch
            {
                "DUAL_TRACK" => "Live-контекст: микрофон + звук системы",
                "MIC_ONLY" => "Live-контекст: только микрофон",
                _ => "Live-контекст: резервный микрофон"
            };
            VoiceLiveTracks = $"Дорожки: микрофон — {LiveTrackLabel(response.LiveRoomTrackState)} · система — {LiveTrackLabel(response.LiveSystemTrackState)}";
            VoiceLiveStats = $"Live ASR: опубликовано {response.LiveSegmentsPublished} · подавлено {response.LiveSegmentsSuppressed} · drops {response.LiveAudioDrops}";
            VoiceAcousticDiagnostics = $"Автокалибровка: шум {FormatDb(response.VoiceNoiseFloorDb)} · VAD {FormatDb(response.VoiceVadThresholdDb)}";
            var restartCount = _services.VoiceHost.RestartCount;
            if (_voiceRestartCountCache != restartCount)
            {
                _voiceRestartCountCache = restartCount;
                OnPropertyChanged(nameof(VoiceRestartCount));
            }
            if (!_voiceTelemetry.IsFresh(DateTimeOffset.UtcNow))
                ApplyVoiceTelemetry(new VoiceTelemetryUiState(
                    response.AudioTelemetrySequence,
                    response.LastAudioAtUtc,
                    response.MicrophoneRms ?? 0d,
                    response.MicrophonePeak ?? 0d,
                    response.MicrophoneClipping,
                    response.AudioSignalState ?? "WAITING"));
        }
        catch (Exception ex)
        {
            VoiceEffectiveVoice = "—";
            VoiceFallbackUsed = false;
            VoiceSpeechQueue = "Очередь речи: —";
            VoiceLiveMode = "Live-контекст: недоступен";
            VoiceLiveTracks = "Дорожки: —";
            VoiceLiveStats = "Live ASR: —";
            VoiceAcousticDiagnostics = "Автокалибровка: ошибка чтения telemetry";
            VoicePlaybackStatus = VoiceQuietMode
                ? "Озвучка отключена: тихий режим"
                : "Озвучка недоступна: Voice Host не отвечает";
            var errorCode = _services.VoiceHost.LastErrorCode ?? UiErrorFormatter.Format(ex, "VOICE_HOST_UNAVAILABLE");
            ApplyVoiceDiagnostics(new VoiceDiagnosticsUiState(
                $"Voice Host: {errorCode}",
                "—",
                errorCode,
                "—",
                "—",
                "—",
                "—",
                "—",
                _services.VoiceHost.InstalledPath ?? "—",
                _services.VoiceHost.ExpectedBuildIdentity,
                _services.VoiceHost.LastObservedBuildIdentity ?? "—",
                "PID —",
                "—",
                "—",
                _services.VoiceHost.LastErrorDetail ?? ex.Message));
            RefreshAssistantDeliveryDiagnostics();
        }
        finally
        {
            RefreshAuthenticationBindings();
        }
    }

    private void RefreshAssistantDeliveryDiagnostics()
    {
        var metrics = _services.AssistantDelivery.GetMetrics();
        VoiceAssistantDelivery =
            $"Ответы: pending {metrics.Pending} · приняты {metrics.Accepted} · доставлены {metrics.Delivered} · " +
            $"отменены {metrics.Cancelled} · ambiguous {metrics.Ambiguous} · истекли {metrics.Expired} · " +
            $"проверяются {metrics.Reconciling} · не приняты {metrics.NotAccepted} · " +
            $"дубли подавлены {metrics.DuplicateSuppressed} · сбои {metrics.Failed}";
    }

    public void ApplyVoiceTelemetry(WhisperX.Atom.Desktop.VoiceTelemetryPacket packet)
    {
        ApplyVoiceTelemetry(new VoiceTelemetryUiState(packet.Sequence, packet.AtUtc, packet.Rms, packet.Peak, packet.Clipping, packet.SignalState ?? "WAITING"));
    }

    public void MarkVoiceTelemetryDisconnected()
    {
        VoiceStatus = "VOICE_TELEMETRY_DISCONNECTED";
        VoiceErrorCode = "VOICE_TELEMETRY_DISCONNECTED";
        OnPropertyChanged(nameof(VoiceStatusLabel));
        OnPropertyChanged(nameof(VoiceStatusDetail));
    }

    private void ApplyVoiceDiagnostics(VoiceDiagnosticsUiState state)
    {
        VoiceStatus = state.Status;
        OnPropertyChanged(nameof(VoiceStatusLabel));
        VoiceLastRecognition = state.LastRecognition;
        VoiceErrorCode = state.ErrorCode;
        OnPropertyChanged(nameof(VoiceStatusDetail));
        VoiceMicrophone = state.EffectiveDeviceName;
        VoiceRequestedMicrophone = state.RequestedDeviceId;
        VoiceEffectiveMicrophone = state.EffectiveDeviceId;
        VoiceWakeWordMode = state.WakeWordMode;
        VoiceRuntimeBuild = state.RuntimeBuild;
        VoiceRuntimePath = state.RuntimePath;
        VoiceExpectedBuild = state.ExpectedBuild;
        VoiceObservedBuild = state.ObservedBuild;
        VoiceRuntimeProcess = state.RuntimeProcess;
        VoiceLastTraceId = state.LastTraceId;
        VoiceLastCommandId = state.LastCommandId;
        VoiceDiagnosticsDetail = state.Detail;
    }

    private void ApplyVoiceTelemetry(VoiceTelemetryUiState state)
    {
        if (state.Sequence > 0
            && state.Sequence < _voiceTelemetry.Sequence
            && state.AtUtc is DateTimeOffset at
            && _voiceTelemetry.AtUtc is DateTimeOffset currentAt
            && at <= currentAt) return;
        _voiceTelemetry = state;
        var fresh = state.IsFresh(DateTimeOffset.UtcNow);
        VoiceLevelNormalized = fresh ? Math.Clamp(state.Peak, 0d, 1d) : 0d;
        VoiceLevel = fresh
            ? $"{VoiceLevelNormalized * 100:0}% peak · RMS {state.Rms * 100:0}%"
            : "Нет данных от микрофона";
        VoiceSignalState = !fresh
            ? "Нет данных от микрофона"
            : state.Clipping || string.Equals(state.SignalState, "CLIPPING", StringComparison.OrdinalIgnoreCase)
                ? "Перегрузка"
                : string.Equals(state.SignalState, "VOICE", StringComparison.OrdinalIgnoreCase)
                    ? "Голос записывается"
                    : "Тишина или слабый сигнал";
    }

    public void RefreshVoiceTelemetryStaleness()
    {
        if (_voiceTelemetry.IsFresh(DateTimeOffset.UtcNow)) return;
        ApplyVoiceTelemetry(_voiceTelemetry);
    }

    private static string ToVoiceStatusLabel(string status)
    {
        if (status.Contains("LISTENING", StringComparison.OrdinalIgnoreCase) || status.Contains("слушает", StringComparison.OrdinalIgnoreCase)) return "Мифодий слушает";
        if (status.Contains("RECOGNIZING", StringComparison.OrdinalIgnoreCase)) return "Распознаёт";
        if (status.Contains("EXECUTING", StringComparison.OrdinalIgnoreCase)) return "Выполняет";
        if (status.Contains("RESPONDING", StringComparison.OrdinalIgnoreCase)) return "Говорит";
        if (status.Contains("DISABLED", StringComparison.OrdinalIgnoreCase) || status.Contains("выключен", StringComparison.OrdinalIgnoreCase)) return "Выключен";
        if (status.Contains("STARTING", StringComparison.OrdinalIgnoreCase)) return "Запускается";
        if (status.Contains("TELEMETRY", StringComparison.OrdinalIgnoreCase)) return "Телеметрия недоступна";
        if (status.Contains("heartbeat", StringComparison.OrdinalIgnoreCase) || status.Contains("недоступ", StringComparison.OrdinalIgnoreCase) || status.Contains("ошиб", StringComparison.OrdinalIgnoreCase)) return "Требуется настройка";
        return "Проверяется";
    }

    private string GetVoiceStatusDetail()
    {
        if (VoiceQuietMode || string.Equals(VoiceErrorCode, "VOICE_QUIET_MODE", StringComparison.OrdinalIgnoreCase))
            return "Тихий режим включён. Ответы не воспроизводятся; выключите его, чтобы вернуть озвучку.";
        if (VoicePlaybackStatus.Contains("недоступна", StringComparison.OrdinalIgnoreCase))
            return "Voice Host работает, но локальный TTS не готов. Нажмите «Проверить голос» или включите русский Windows fallback.";
        if (VoicePlaybackStatus.Contains("не проверена", StringComparison.OrdinalIgnoreCase))
            return "Voice Host ещё не запущен. Откройте Desktop заново или нажмите «Обновить статус».";
        if (_services.Backend.AuthState == DesktopAuthState.LoginRequired)
            return "Авторизация Desktop истекла; локальный Voice Host не требует сети для команд записи.";
        if (string.Equals(VoiceErrorCode, "VOICE_TELEMETRY_DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            return "Телеметрия отключена. Voice Host проверяется через резервный статусный канал.";
        if (VoiceErrorCode.Contains("HOST", StringComparison.OrdinalIgnoreCase)
            || VoiceStatus.Contains("Voice Host", StringComparison.OrdinalIgnoreCase))
            return "Voice Host недоступен или ещё запускается. Проверьте локальный процесс и микрофон.";
        if (VoiceStatus.Contains("heartbeat", StringComparison.OrdinalIgnoreCase))
            return "Heartbeat Voice Host просрочен; команда записи остаётся локальной и безопасной.";
        if (VoiceStatus.Contains("RESPONDING", StringComparison.OrdinalIgnoreCase))
            return "Мифодий формирует и озвучивает ответ.";
        if (VoiceStatus.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
            return "Мифодий готов к кодовому слову и локальным командам.";
        return "Состояние Voice Host и телеметрии обновляется автоматически.";
    }

    private static string FormatPlaybackStatus(WhisperX.Atom.Desktop.DesktopVoiceSnapshot response)
    {
        if (response.QuietMode)
            return "Озвучка отключена: тихий режим";
        if (response.TtsReady && response.TtsFallbackUsed)
            return "Озвучка готова: русский Windows fallback";
        if (response.TtsReady)
            return $"Озвучка готова: {response.TtsEngine} · {response.TtsVoice ?? response.EffectiveVoiceName ?? "русский голос"}";
        return $"Озвучка недоступна: {response.TtsFallbackReason ?? response.LastErrorCode ?? "TTS не готов"}";
    }

    private static string LiveTrackLabel(string state) => state switch
    {
        "ACTIVE" => "активна",
        "CONNECTED" => "подключена",
        "WAITING" => "ожидание",
        _ => state
    };

    private static string FormatDb(double? value) => value is double number && double.IsFinite(number)
        ? $"{number:0.0} dBFS"
        : "—";

    public async Task TestVoiceSpeechAsync(string phrase)
    {
        try
        {
            var response = await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync("TEST_SPEECH", new { text = phrase, confidence = 1.0 });
            VoiceLastRecognition = response.Data is JsonElement data && data.TryGetProperty("recognizedText", out var text) ? text.GetString() ?? "—" : "—";
            VoiceStatus = response.Data is JsonElement result && result.TryGetProperty("intent", out var intent) ? $"Тест: {intent.GetString()}" : "Тест выполнен";
        }
        catch (Exception ex) { VoiceErrorCode = UiErrorFormatter.Format(ex, "VOICE_HOST_UNAVAILABLE"); }
    }

    public async Task TestVoiceTtsAsync()
    {
        try
        {
            if (VoiceQuietMode)
            {
                VoiceErrorCode = "VOICE_QUIET_MODE";
                VoicePlaybackStatus = "Озвучка отключена: тихий режим";
                VoiceStatus = "Тихий режим включён";
                return;
            }
            // A stale Desktop session or a delayed startup may leave the
            // hidden Host stopped.  A local TTS test is allowed to recover
            // that process and does not require the Assistant API.
            if (!await _services.VoiceHost.StartAsync().ConfigureAwait(true))
            {
                VoiceErrorCode = _services.VoiceHost.LastErrorCode ?? "VOICE_HOST_UNAVAILABLE";
                VoicePlaybackStatus = "Озвучка недоступна: Voice Host не запущен";
                return;
            }
            var response = await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync("TEST_TTS");
            if (!response.Ok)
            {
                VoiceErrorCode = response.Error ?? "VOICE_TTS_UNAVAILABLE";
                VoicePlaybackStatus = $"Озвучка недоступна: {VoiceErrorCode}";
                return;
            }
            VoiceErrorCode = response.Data is JsonElement data && data.TryGetProperty("errorCode", out var error)
                ? error.GetString() ?? "—"
                : "—";
            VoicePlaybackStatus = response.Data is JsonElement result && result.TryGetProperty("engine", out var engine)
                ? $"Озвучка проверена: {engine.GetString() ?? "русский голос"}"
                : "Озвучка проверена";
            VoiceStatus = "Мифодий готов";
        }
        catch (Exception ex)
        {
            VoiceErrorCode = UiErrorFormatter.Format(ex, "VOICE_HOST_UNAVAILABLE");
            VoicePlaybackStatus = "Озвучка недоступна: Voice Host не отвечает";
        }
    }

    public async Task EnableVoicePlaybackAsync()
    {
        if (!VoiceQuietMode) return;
        VoiceQuietMode = false;
        await RefreshVoiceDiagnosticsAsync().ConfigureAwait(true);
    }

    public async Task CalibrateVoiceNoiseAsync()
    {
        try
        {
            VoiceAcousticDiagnostics = "Автокалибровка: не говорите 5 секунд…";
            var client = new WhisperX.Atom.Desktop.VoiceHostClient();
            var started = await client.SendAsync("CALIBRATION_START", new { phase = "NOISE" }).ConfigureAwait(true);
            if (!started.Ok)
            {
                VoiceAcousticDiagnostics = $"Автокалибровка: {started.Error ?? "недоступна"}";
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            var completed = await client.SendAsync("CALIBRATION_STOP").ConfigureAwait(true);
            if (!completed.Ok || completed.Data is not JsonElement data)
            {
                VoiceAcousticDiagnostics = $"Автокалибровка: {completed.Error ?? "не завершена"}";
                return;
            }
            var rmsDb = data.TryGetProperty("rmsDb", out var rms) && rms.TryGetDouble(out var rmsValue) ? rmsValue : (double?)null;
            var peakDb = data.TryGetProperty("peakDb", out var peak) && peak.TryGetDouble(out var peakValue) ? peakValue : (double?)null;
            var thresholdDb = data.TryGetProperty("recommendedVadThresholdDb", out var threshold) && threshold.TryGetDouble(out var thresholdValue) ? thresholdValue : (double?)null;
            VoiceAcousticDiagnostics = $"Автокалибровка: шум {FormatDb(rmsDb)} · пик {FormatDb(peakDb)} · VAD {FormatDb(thresholdDb)}";
        }
        catch (Exception ex)
        {
            VoiceAcousticDiagnostics = "Автокалибровка: Voice Host недоступен";
            VoiceErrorCode = UiErrorFormatter.Format(ex, "VOICE_HOST_UNAVAILABLE");
        }
    }

    private async Task ApplyVoiceSettingsAsync()
    {
        var current = _services.Settings.Load();
        _services.Settings.Save(current with { VoiceAlwaysListening = VoiceAlwaysListening, VoiceQuietMode = VoiceQuietMode, VoiceSensitivity = VoiceSensitivity, VoiceName = VoiceName, VoiceRate = VoiceRate, VoiceVolume = VoiceVolume, TtsEngine = TtsEngine, TtsVoice = TtsVoice, TtsVoiceProfile = TtsVoiceProfile, TtsSampleRate = TtsSampleRate, TtsCpuThreads = TtsCpuThreads, TtsFallbackEnabled = TtsFallbackEnabled, WindowsFallbackVoice = WindowsFallbackVoice, VoiceProcessingGainDb = VoiceProcessingGainDb });
        try
        {
            var effectiveMicrophone = current.MicrophoneDeviceId;
            try
            {
                var health = await _services.Recorder.GetHealthAsync().ConfigureAwait(true);
                effectiveMicrophone = health.Health?.EffectiveMicrophoneDeviceId
                    ?? health.Health?.SelectedMicrophoneDeviceId
                    ?? effectiveMicrophone;
            }
            catch { }
            if (!await _services.VoiceHost.ConfigureAsync(effectiveMicrophone, VoiceAlwaysListening, VoiceQuietMode, VoiceSensitivity, voiceName: VoiceName, voiceRate: VoiceRate, voiceVolume: VoiceVolume, ttsEngine: TtsEngine, ttsVoice: TtsVoice, ttsVoiceProfile: TtsVoiceProfile, ttsSampleRate: TtsSampleRate, ttsCpuThreads: TtsCpuThreads, ttsFallbackEnabled: TtsFallbackEnabled, windowsFallbackVoice: WindowsFallbackVoice, voiceProcessingGainDb: VoiceProcessingGainDb))
                VoiceErrorCode = _services.VoiceHost.LastErrorCode ?? "VOICE_HOST_UNAVAILABLE";
        }
        catch { if (VoiceAlwaysListening) _ = _services.VoiceHost.StartAsync(); }
    }

    public async Task<bool> LoginAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(ApiUrl) || string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(password))
        {
            StatusText = "Укажите API URL, пользователя и пароль.";
            return false;
        }
        try
        {
            IsBusy = true;
            var ok = await _services.Backend.LoginAsync(ApiUrl.Trim(), Username.Trim(), password);
            if (!ok)
            {
                StatusText = "API отклонил вход. Проверьте учётные данные.";
                return false;
            }
            SaveSettings(_services.Backend.SessionCookie);
            StatusText = "Вход в локальный API выполнен.";
            OnPropertyChanged(nameof(IsLoggedIn));
            OnPropertyChanged(nameof(CanLogin));
            OnPropertyChanged(nameof(CanChangeServerOrigin));
            OnPropertyChanged(nameof(CanChangePassword));
            OnPropertyChanged(nameof(CanReconnectAgent));
            OnPropertyChanged(nameof(CanLogout));
            var currentUser = await _services.Backend.GetCurrentUserAsync();
            MustChangePassword = currentUser?.MustChangePassword == true;
            if (MustChangePassword) StatusText = "Вход выполнен. Установите новый пароль.";
            OnPropertyChanged(nameof(LoginStatusText));
            OnPropertyChanged(nameof(LoginStatusDetail));
            OnPropertyChanged(nameof(IsSessionExpired));
            OnPropertyChanged(nameof(ApiStatusShort));
            OnPropertyChanged(nameof(SessionExpiryText));
            return true;
        }
        catch (Exception ex) { StatusText = SafeError(ex); return false; }
        finally { IsBusy = false; }
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 12)
        {
            StatusText = "Новый пароль должен содержать не менее 12 символов.";
            return false;
        }
        try
        {
            IsBusy = true;
            if (!await _services.Backend.ChangePasswordAsync(currentPassword, newPassword))
            {
                StatusText = "Сервер отклонил смену пароля.";
                return false;
            }
            MustChangePassword = false;
            SaveSettings(_services.Backend.SessionCookie);
            StatusText = "Пароль изменён.";
            return true;
        }
        catch (Exception ex) { StatusText = SafeError(ex); return false; }
        finally { IsBusy = false; }
    }

    public async Task<bool> RegisterAgentAsync(string secret)
    {
        if (!_services.Backend.HasSession) { StatusText = "Сначала войдите в API."; return false; }
        if (string.IsNullOrWhiteSpace(secret)) { StatusText = "Введите enrollment secret."; return false; }
        try
        {
            IsBusy = true;
            var enrollment = await _services.Backend.EnrollAgentAsync(string.IsNullOrWhiteSpace(AgentName) ? "WhisperX Atom Desktop" : AgentName.Trim(), secret.Trim());
            if (enrollment is null || !Guid.TryParse(enrollment.AgentId, out var agentId))
            {
                StatusText = "API не вернул корректные данные Agent.";
                return false;
            }
            var settings = _services.Settings.Load();
            var response = await _services.Recorder.ConfigureAgentAsync(ApiUrl.Trim(), agentId, enrollment.Token, ArchiveRoot, settings.MicrophoneDeviceId, settings.SystemAudioDeviceId);
            StatusText = response.Ok ? "Recorder Agent зарегистрирован и настроен." : response.Error ?? "Agent не подтвердил настройку.";
            return response.Ok;
        }
        catch (Exception ex) { StatusText = SafeError(ex); return false; }
        finally { IsBusy = false; }
    }

#if false
    // The coordinator below is the only active bootstrap/reconnect path.
    public async Task<bool> ReconnectAgentAsync()
    {
        try
        {
            IsBusy = true;
            if (!await _services.Backend.EnsureAuthenticatedAsync())
            {
                StatusText = "Требуется повторный вход в API.";
                return false;
            }
            var healthResponse = await _services.Recorder.GetHealthAsync();
            if (healthResponse.Health is null)
            {
                StatusText = "Recorder Agent недоступен. Запустите службу Agent.";
                return false;
            }
            if (string.Equals(healthResponse.State, "Recording", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(healthResponse.State, "Paused", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(healthResponse.State, "Finalizing", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "Переподключение отложено до завершения текущей записи.";
                return false;
            }

            var health = healthResponse.Health;
            var enrollment = await _services.Backend.LinkLocalAgentAsync(
                health.InstallationId ?? Guid.NewGuid(), health.AgentId,
                string.IsNullOrWhiteSpace(AgentName) ? "WhisperX Atom Desktop" : AgentName.Trim());
            var settings = _services.Settings.Load();
            var response = await _services.Recorder.ConfigureAgentAsync(
                ApiUrl.Trim(), Guid.Parse(enrollment.AgentId), enrollment.Token, ArchiveRoot,
                settings.MicrophoneDeviceId, settings.SystemAudioDeviceId);
            StatusText = response.Ok ? "Recorder Agent подключён к серверу." : response.Error ?? "Agent не подтвердил подключение.";
            return response.Ok;
        }
        catch (Exception ex) { StatusText = SafeError(ex); return false; }
        finally { IsBusy = false; }
    }

    public async Task<bool> StartRecorderServiceAsync()
    {
        try
        {
            IsBusy = true;
            var snapshot = await _services.RecorderService.StartAsync();
            StatusText = snapshot.PipeReachable
                ? "Recorder Service запущен, Named Pipe доступен."
                : snapshot.Error ?? "Recorder Service запущен, но Named Pipe пока недоступен.";
            return snapshot.PipeReachable;
        }
        catch (Exception ex)
        {
            StatusText = SafeError(ex);
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> SaveDiagnosticsAsync()
    {
        try
        {
            IsBusy = true;
            DiagnosticsPath = await _services.Diagnostics.WriteAsync();
            StatusText = $"Диагностический пакет сохранён: {DiagnosticsPath}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = SafeError(ex);
            return false;
        }
        finally { IsBusy = false; }
    }

#endif
    public async Task<bool> StartRecorderServiceAsync()
    {
        try
        {
            IsBusy = true;
            var snapshot = await _services.RecorderService.StartAsync();
            StatusText = snapshot.PipeReachable
                ? "Recorder Service запущен, Named Pipe доступен."
                : snapshot.Error ?? "Recorder Service запущен, но Named Pipe пока недоступен.";
            return snapshot.PipeReachable;
        }
        catch (Exception ex)
        {
            StatusText = SafeError(ex);
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> SaveDiagnosticsAsync()
    {
        try
        {
            IsBusy = true;
            DiagnosticsPath = await _services.Diagnostics.WriteAsync();
            StatusText = $"Диагностический пакет сохранён: {DiagnosticsPath}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = SafeError(ex);
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> ReconnectAgentAsync()
    {
        try
        {
            IsBusy = true;
            var result = await _services.AgentBootstrap.EnsureAgentReadyAsync();
            if (result.RequiresReenroll && result.AgentId is Guid agentId)
            {
                var enrollment = await _services.Backend.ReenrollAgentAsync(agentId);
                var settings = _services.Settings.Load();
                var configured = await _services.Recorder.ConfigureAgentAsync(
                    ApiUrl.Trim(),
                    agentId,
                    enrollment.Token,
                    settings.ArchiveRoot ?? DesktopSettings.DefaultArchiveRoot(),
                    settings.MicrophoneDeviceId,
                    settings.SystemAudioDeviceId);
                if (!configured.Ok)
                {
                    StatusText = configured.Error ?? "Recorder Agent не принял новый токен.";
                    return false;
                }
                result = await _services.AgentBootstrap.EnsureAgentReadyAsync();
            }
            StatusText = result.Message;
            return result.Ready;
        }
        catch (Exception ex)
        {
            StatusText = SafeError(ex);
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task LogoutAsync()
    {
        // Clear local scopes before the cookie is removed.  Voice Host can
        // keep listening for Recorder commands, but it must not retain a
        // meeting, a conversation id or a pending answer from this user.
        _services.ClearUserAssistantState();
        await _services.Backend.LogoutAsync();
        var current = _services.Settings.Load();
        _services.Settings.Save(current with { ProtectedSessionCookie = null, SessionExpiresAtUtc = null, OwnerUserId = null, AgentBootstrapConfirmed = false });
        StatusText = "Выход из API выполнен.";
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(CanChangePassword));
        OnPropertyChanged(nameof(CanReconnectAgent));
        OnPropertyChanged(nameof(CanLogout));
        OnPropertyChanged(nameof(LoginStatusText));
        OnPropertyChanged(nameof(LoginStatusDetail));
        OnPropertyChanged(nameof(IsSessionExpired));
        OnPropertyChanged(nameof(ApiStatusShort));
        OnPropertyChanged(nameof(SessionExpiryText));
        _services.RaiseLoggedOut();
    }

    public async Task SetArchiveRootAsync(string path)
    {
        try
        {
            IsBusy = true;
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText = "Выберите папку архива.";
                return;
            }
            var fullPath = Path.GetFullPath(path.Trim());
            Directory.CreateDirectory(fullPath);
            ArchiveRoot = fullPath;
            SaveSettings(_services.Backend.SessionCookie);
            var response = await _services.Recorder.SetArchiveRootAsync(fullPath);
            StatusText = response.Ok ? "Папка архива сохранена и передана Recorder Agent." : response.Error ?? "Agent не подтвердил папку архива.";
        }
        catch (Exception ex) { StatusText = $"Путь сохранён в Desktop, но Agent недоступен: {SafeError(ex)}"; }
        finally { IsBusy = false; }
    }

    public async Task<bool> CheckBackendAsync()
    {
        try
        {
            IsBusy = true;
            var ready = await _services.Backend.CheckReadyAsync();
            StatusText = ready
                ? "API доступен."
                : _services.Backend.LastConnectionErrorCode switch
                {
                    "SERVER_NETWORK_UNREACHABLE" => "Сервер недоступен по сети. Проверьте LAN, адрес и брандмауэр.",
                    "SERVER_TIMEOUT" => "Сервер не ответил вовремя. Проверьте LAN и состояние gateway.",
                    { } code when code.StartsWith("SERVER_HTTP_", StringComparison.OrdinalIgnoreCase)
                        => $"Сервер ответил отказом ({code[12..]}). Проверьте состояние API и авторизацию.",
                    _ => "Сервер не отвечает. Проверьте адрес и состояние API."
                };
            return ready;
        }
        catch (Exception ex)
        {
            StatusText = SafeError(ex, "Не удалось проверить подключение к API.");
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> ApplyServerOriginAsync()
    {
        if (ServerOriginManaged)
        {
            StatusText = "Адрес сервера управляется установщиком.";
            return false;
        }

        var candidate = ApiUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            StatusText = "Укажите корректный HTTP(S)-адрес сервера.";
            return false;
        }

        try
        {
            IsBusy = true;
            var current = _services.Settings.Load();
            var originChanged = !string.Equals(
                current.ApiUrl.TrimEnd('/'),
                candidate,
                StringComparison.OrdinalIgnoreCase);
            var updated = originChanged
                ? current with
                {
                    ApiUrl = candidate,
                    ProtectedSessionCookie = null,
                    SessionExpiresAtUtc = null,
                    OwnerUserId = null,
                    AgentBootstrapConfirmed = false
                }
                : current with { ApiUrl = candidate };
            _services.Settings.Save(updated);
            _services.Backend.ApplySettings(updated);
            OnPropertyChanged(nameof(LoginStatusText));
            OnPropertyChanged(nameof(LoginStatusDetail));
            OnPropertyChanged(nameof(IsSessionExpired));
            OnPropertyChanged(nameof(ApiStatusShort));
            OnPropertyChanged(nameof(SessionExpiryText));

            if (!await _services.Backend.EnsureAuthenticatedAsync())
            {
                StatusText = "Адрес сервера сохранён. Выполните вход для проверки подключения.";
                return true;
            }

            var bootstrap = await _services.AgentBootstrap.EnsureAgentReadyAsync();
            StatusText = bootstrap.Message;
            return bootstrap.Ready || bootstrap.OfflineEligible;
        }
        catch (Exception ex)
        {
            StatusText = SafeError(ex, "Не удалось применить адрес сервера.");
            return false;
        }
        finally { IsBusy = false; }
    }

    private void SaveSettings(string? cookie)
    {
        var current = _services.Settings.Load();
        var effectiveCookie = string.IsNullOrWhiteSpace(cookie) ? current.UnprotectSessionCookie() : cookie;
        DesktopSettings.Save(ApiUrl.TrimEnd('/'), Username.Trim(), effectiveCookie, ArchiveRoot,
            current.MicrophoneDeviceId, current.SystemAudioDeviceId, _services.Backend.SessionExpiresAtUtc,
            current.RecordingProfile, current.OwnerUserId, current.AgentBootstrapConfirmed,
            current.VoiceAlwaysListening, current.VoiceQuietMode, current.VoiceSensitivity, current.AcousticProfile, current.VoiceName, current.VoiceRate, current.VoiceVolume, current.UpdateChannel, current.TtsEngine, current.TtsVoice, current.TtsSampleRate, current.TtsCpuThreads, current.TtsFallbackEnabled, current.WindowsFallbackVoice, current.VoiceProcessingGainDb, current.TtsVoiceProfile);
    }

    private static string SafeError(Exception ex, string? fallback = null) => UiErrorFormatter.Format(ex, fallback ?? "Не удалось выполнить операцию с настройками.");
}

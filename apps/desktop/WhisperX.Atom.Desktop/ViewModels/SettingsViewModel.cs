using CommunityToolkit.Mvvm.ComponentModel;
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
    private string _voiceStatus = "Проверка Мифодия…";
    private string _voiceLastRecognition = "—";
    private string _voiceErrorCode = "—";
    private string _voiceMicrophone = "—";
    private string _voiceLevel = "—";
    private double _voiceLevelNormalized;
    private string _voiceSignalState = "Ожидание аудиокадров";
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
    public bool VoiceQuietMode { get => _voiceQuietMode; set { if (SetProperty(ref _voiceQuietMode, value)) _ = ApplyVoiceSettingsAsync(); } }
    public string VoiceSensitivity { get => _voiceSensitivity; set { if (SetProperty(ref _voiceSensitivity, value)) _ = ApplyVoiceSettingsAsync(); } }
    public string VoiceStatus { get => _voiceStatus; private set => SetProperty(ref _voiceStatus, value); }
    public string VoiceLastRecognition { get => _voiceLastRecognition; private set => SetProperty(ref _voiceLastRecognition, value); }
    public string VoiceErrorCode { get => _voiceErrorCode; private set => SetProperty(ref _voiceErrorCode, value); }
    public string VoiceMicrophone { get => _voiceMicrophone; private set => SetProperty(ref _voiceMicrophone, value); }
    public string VoiceLevel { get => _voiceLevel; private set => SetProperty(ref _voiceLevel, value); }
    public double VoiceLevelNormalized { get => _voiceLevelNormalized; private set => SetProperty(ref _voiceLevelNormalized, value); }
    public string VoiceSignalState { get => _voiceSignalState; private set => SetProperty(ref _voiceSignalState, value); }
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
    public string LoginStatusText => _services.Backend.AuthState switch
    {
        DesktopAuthState.Authenticated => "Вход в API выполнен",
        DesktopAuthState.Offline => "API временно недоступен; локальная запись работает",
        DesktopAuthState.LoginRequired => "Требуется повторный вход в API",
        _ => IsLoggedIn ? "Проверка сессии API…" : "Вход в API не выполнен"
    };

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
    }

    public async Task RefreshVoiceDiagnosticsAsync()
    {
        try
        {
            var response = await new WhisperX.Atom.Desktop.VoiceHostClient().GetStatusAsync().ConfigureAwait(true);
            if (response is null)
            {
                var controllerError = _services.VoiceHost.LastErrorCode;
                VoiceStatus = controllerError is null ? "Voice Host не запущен" : $"Voice Host: {controllerError}";
                VoiceErrorCode = controllerError ?? "VOICE_HOST_NOT_INSTALLED";
                VoiceRuntimePath = _services.VoiceHost.InstalledPath ?? "—";
                VoiceExpectedBuild = _services.VoiceHost.ExpectedBuildIdentity;
                VoiceObservedBuild = _services.VoiceHost.LastObservedBuildIdentity ?? "—";
                VoiceDiagnosticsDetail = _services.VoiceHost.LastErrorDetail ?? "—";
                return;
            }
            var heartbeat = response.HeartbeatAtUtc ?? response.UpdatedAt;
            var heartbeatStale = DateTimeOffset.UtcNow - heartbeat.ToUniversalTime() > TimeSpan.FromSeconds(10);
            VoiceStatus = heartbeatStale
                ? "Нет heartbeat от Voice Host"
                : $"{response.State} · модель {(response.ModelReady ? "готова" : "не готова")} · микрофон {(response.MicrophoneReady ? "готов" : "недоступен")}";
            VoiceLastRecognition = response.LastRecognizedText ?? "—";
            VoiceErrorCode = response.LastErrorCode ?? "—";
            VoiceMicrophone = response.EffectiveMicrophoneName ?? "—";
            VoiceRequestedMicrophone = response.RequestedMicrophoneDeviceId ?? "DEFAULT";
            VoiceEffectiveMicrophone = response.EffectiveMicrophoneDeviceId ?? response.EffectiveMicrophoneName ?? "—";
            VoiceWakeWordMode = response.WakeWordMode ?? "—";
            VoiceRuntimeBuild = response.BuildIdentity ?? "—";
            VoiceRuntimePath = _services.VoiceHost.InstalledPath ?? "—";
            VoiceExpectedBuild = _services.VoiceHost.ExpectedBuildIdentity;
            VoiceObservedBuild = response.BuildIdentity ?? _services.VoiceHost.LastObservedBuildIdentity ?? "—";
            VoiceDiagnosticsDetail = response.MicrophoneErrorDetail
                ?? _services.VoiceHost.LastErrorDetail
                ?? "—";
            VoiceRuntimeProcess = response.ProcessId is int pid ? $"PID {pid}" : "PID —";
            VoiceLastTraceId = response.LastTraceId ?? "—";
            VoiceLastCommandId = response.LastCommandId ?? "—";
            OnPropertyChanged(nameof(VoiceRestartCount));
            var telemetryFresh = response.LastAudioAtUtc is DateTimeOffset at
                && DateTimeOffset.UtcNow - at <= TimeSpan.FromMilliseconds(750)
                && response.AudioTelemetrySequence > 0;
            var peak = response.MicrophonePeak ?? 0d;
            VoiceLevelNormalized = telemetryFresh ? Math.Clamp(peak, 0d, 1d) : 0d;
            VoiceLevel = telemetryFresh ? $"{VoiceLevelNormalized * 100:0}% peak · RMS {(response.MicrophoneRms ?? 0d) * 100:0}%" : "Нет данных от микрофона";
            VoiceSignalState = !telemetryFresh
                ? "Нет данных от микрофона"
                : response.MicrophoneClipping || string.Equals(response.AudioSignalState, "CLIPPING", StringComparison.OrdinalIgnoreCase)
                    ? "Перегрузка"
                    : string.Equals(response.AudioSignalState, "VOICE", StringComparison.OrdinalIgnoreCase)
                        ? "Голос записывается"
                        : "Тишина или слабый сигнал";
        }
        catch (Exception ex)
        {
            VoiceStatus = _services.VoiceHost.LastErrorCode is { } code ? $"Voice Host: {code}" : "Voice Host недоступен";
            VoiceErrorCode = _services.VoiceHost.LastErrorCode ?? UiErrorFormatter.Format(ex, "VOICE_HOST_UNAVAILABLE");
            VoiceRuntimePath = _services.VoiceHost.InstalledPath ?? "—";
            VoiceExpectedBuild = _services.VoiceHost.ExpectedBuildIdentity;
            VoiceObservedBuild = _services.VoiceHost.LastObservedBuildIdentity ?? "—";
            VoiceDiagnosticsDetail = _services.VoiceHost.LastErrorDetail ?? ex.Message;
        }
    }

    public void ApplyVoiceTelemetry(WhisperX.Atom.Desktop.VoiceTelemetryPacket packet)
    {
        var fresh = packet.AtUtc is DateTimeOffset at
            && DateTimeOffset.UtcNow - at <= TimeSpan.FromMilliseconds(750)
            && packet.Sequence > 0;
        VoiceRequestedMicrophone = packet.DeviceId ?? VoiceRequestedMicrophone;
        VoiceEffectiveMicrophone = packet.DeviceName ?? packet.DeviceId ?? VoiceEffectiveMicrophone;
        VoiceMicrophone = packet.DeviceName ?? VoiceMicrophone;
        VoiceLevelNormalized = fresh ? Math.Clamp(packet.Peak, 0d, 1d) : 0d;
        VoiceLevel = fresh ? $"{VoiceLevelNormalized * 100:0}% peak · RMS {packet.Rms * 100:0}%" : "Нет данных от микрофона";
        VoiceSignalState = !fresh
            ? "Нет данных от микрофона"
            : packet.Clipping || string.Equals(packet.SignalState, "CLIPPING", StringComparison.OrdinalIgnoreCase)
                ? "Перегрузка"
                : string.Equals(packet.SignalState, "VOICE", StringComparison.OrdinalIgnoreCase)
                    ? "Голос записывается"
                    : "Тишина или слабый сигнал";
    }

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
        try { _ = await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync("TEST_TTS"); }
        catch (Exception ex) { VoiceErrorCode = UiErrorFormatter.Format(ex, "VOICE_HOST_UNAVAILABLE"); }
    }

    private async Task ApplyVoiceSettingsAsync()
    {
        var current = _services.Settings.Load();
        _services.Settings.Save(current with { VoiceAlwaysListening = VoiceAlwaysListening, VoiceQuietMode = VoiceQuietMode, VoiceSensitivity = VoiceSensitivity });
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
            if (!await _services.VoiceHost.ConfigureAsync(effectiveMicrophone, VoiceAlwaysListening, VoiceQuietMode, VoiceSensitivity))
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
            StatusText = $"Диагностика сохранена: {DiagnosticsPath}";
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
            StatusText = $"Диагностика сохранена: {DiagnosticsPath}";
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
            current.VoiceAlwaysListening, current.VoiceQuietMode, current.VoiceSensitivity);
    }

    private static string SafeError(Exception ex, string? fallback = null) => UiErrorFormatter.Format(ex, fallback ?? "Не удалось выполнить операцию с настройками.");
}

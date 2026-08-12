using CommunityToolkit.Mvvm.ComponentModel;
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
    }

    public string ApiUrl { get => _apiUrl; set { if (!ServerOriginManaged) SetProperty(ref _apiUrl, value); } }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string ArchiveRoot { get => _archiveRoot; private set => SetProperty(ref _archiveRoot, value); }
    public string AgentName { get => _agentName; set => SetProperty(ref _agentName, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool IsLoggedIn => _services.Backend.HasSession;
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

#endif
    public async Task<bool> ReconnectAgentAsync()
    {
        try
        {
            IsBusy = true;
            var result = await _services.AgentBootstrap.EnsureAgentReadyAsync();
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
        OnPropertyChanged(nameof(LoginStatusText));
        OnPropertyChanged(nameof(SessionExpiryText));
        _services.RaiseLoggedOut();
    }

    public async Task SetArchiveRootAsync(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        Directory.CreateDirectory(fullPath);
        ArchiveRoot = fullPath;
        SaveSettings(_services.Backend.SessionCookie);
        try
        {
            var response = await _services.Recorder.SetArchiveRootAsync(fullPath);
            StatusText = response.Ok ? "Папка архива сохранена и передана Recorder Agent." : response.Error ?? "Agent не подтвердил папку архива.";
        }
        catch (Exception ex) { StatusText = $"Путь сохранён в Desktop, но Agent недоступен: {SafeError(ex)}"; }
    }

    private void SaveSettings(string? cookie)
    {
        var current = _services.Settings.Load();
        var effectiveCookie = string.IsNullOrWhiteSpace(cookie) ? current.UnprotectSessionCookie() : cookie;
        DesktopSettings.Save(ApiUrl.TrimEnd('/'), Username.Trim(), effectiveCookie, ArchiveRoot,
            current.MicrophoneDeviceId, current.SystemAudioDeviceId, _services.Backend.SessionExpiresAtUtc,
            current.RecordingProfile, current.OwnerUserId, current.AgentBootstrapConfirmed);
    }

    private static string SafeError(Exception ex) => UiErrorFormatter.Format(ex, "Не удалось выполнить операцию с настройками.");
}

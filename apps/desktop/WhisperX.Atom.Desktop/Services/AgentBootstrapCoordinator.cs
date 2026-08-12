using WhisperX.Atom.Recorder;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop;

namespace WhisperX_Atom_Desktop.Services;

public sealed record AgentBootstrapStatus(
    bool Ready,
    bool RecorderAvailable,
    bool OfflineEligible,
    string Code,
    string Message)
{
    public bool Authenticated { get; init; }
    public bool PipeReachable { get; init; }
    public bool InstallationIdPresent { get; init; }
    public bool AgentConfigured { get; init; }
    public bool UserLinked { get; init; }
    public bool ServerConnected { get; init; }
    public bool HeartbeatFresh { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? InstallationId { get; init; }

    public bool RequiresReenroll => string.Equals(Code, "REENROLL_REQUIRED", StringComparison.OrdinalIgnoreCase);
    public bool IsTransient => string.Equals(Code, "RECORDER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Code, "SERVER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The single Desktop path that links the current user to the local Recorder Agent.
/// It never rotates an existing Agent token.
/// </summary>
public sealed class AgentBootstrapCoordinator(FrontendServices services)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentBootstrapStatus _lastStatus = new(false, false, false, "AGENT_LINK_PENDING", "Recorder Agent ещё не привязан к пользователю.");

    public event Action? StatusChanged;
    public AgentBootstrapStatus LastStatus => _lastStatus;
    public bool IsReady => _lastStatus.Ready;

    public async Task<AgentBootstrapStatus> EnsureAgentReadyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await EnsureAgentReadyCoreAsync(cancellationToken);
            _lastStatus = result;
            StatusChanged?.Invoke();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<AgentBootstrapStatus> EnsureAgentReadyCoreAsync(CancellationToken cancellationToken)
    {
        var user = await services.Backend.GetCurrentUserAsync(cancellationToken);
        var settings = services.Settings.Load();
        var offlineEligible = services.Backend.CanUseOffline
            && settings.OwnerUserId is not null
            && settings.AgentBootstrapConfirmed;
        if (user is null && services.Backend.AuthState == DesktopAuthState.Offline)
            return new(false, false, offlineEligible, "SERVER_UNAVAILABLE", offlineEligible
                ? "LAN-сервер временно недоступен; ранее подтверждённая локальная запись может продолжиться."
                : "LAN-сервер недоступен, а локальная привязка Agent ещё не подтверждена.");
        if (user is null)
            return new(false, false, false, "AUTH_REQUIRED", "Требуется вход в сервер.") { Authenticated = false };

        AgentIpcResponse health;
        try
        {
            health = await services.Recorder.GetHealthAsync(cancellationToken);
        }
        catch (RecorderIpcException exception)
        {
            var message = exception.ErrorCode is "RECORDER_IPC_TIMEOUT" or "RECORDER_IPC_UNAVAILABLE"
                ? "Recorder Service не запущен или Named Pipe недоступен. Запустите службу Recorder и повторите проверку."
                : "Recorder Service не подтвердил состояние. Повторите проверку позже.";
            return new(false, false, offlineEligible, "RECORDER_UNAVAILABLE", message) { Authenticated = true };
        }
        catch (Exception)
        {
            return new(false, false, offlineEligible, "RECORDER_UNAVAILABLE", "Recorder Service недоступен. Проверьте локальную службу и Named Pipe.") { Authenticated = true };
        }

        if (!health.Ok || health.Health is null)
            return new(false, false, offlineEligible, "RECORDER_UNAVAILABLE", "Recorder Service не запущен или Named Pipe недоступен.") { Authenticated = true };

        var agentHealth = health.Health;
        var installationId = agentHealth.InstallationId;
        if (installationId is null || installationId == Guid.Empty)
            return new(false, true, offlineEligible, "INSTALLATION_ID_MISSING", "Recorder Agent не сообщил постоянный InstallationId.")
            {
                Authenticated = true,
                PipeReachable = true,
                AgentConfigured = health.Health is not null
            };

        DesktopAgentBootstrapResult enrollment;
        try
        {
            enrollment = await services.Backend.BootstrapLocalAgentAsync(
                installationId.Value,
                agentHealth.AgentId,
                "WhisperX Atom Desktop",
                cancellationToken);
        }
        catch (DesktopApiException exception)
        {
            return new(false, true, offlineEligible,
                exception.Retryable ? "SERVER_UNAVAILABLE" : exception.ErrorCode,
                exception.Retryable
                    ? "LAN-сервер временно недоступен. Локальная запись останется доступной после подтверждённой привязки."
                    : $"Сервер отклонил привязку Recorder Agent: {exception.ErrorCode}.")
            { Authenticated = true, PipeReachable = true, InstallationIdPresent = true, InstallationId = installationId };
        }
        catch (HttpRequestException)
        {
            return new(false, true, offlineEligible, "SERVER_UNAVAILABLE",
                "LAN-сервер недоступен. Повторите синхронизацию после восстановления сети.")
            { Authenticated = true, PipeReachable = true, InstallationIdPresent = true, InstallationId = installationId };
        }

        Guid.TryParse(enrollment.AgentId, out var enrollmentAgentId);
        if (enrollment.ReenrollRequired)
            return new(false, true, false, "REENROLL_REQUIRED", "Recorder Agent требует повторной регистрации.")
            { Authenticated = true, PipeReachable = true, InstallationIdPresent = true, InstallationId = installationId, AgentId = enrollmentAgentId == Guid.Empty ? null : enrollmentAgentId };

        if (!enrollment.Linked)
            return new(false, true, offlineEligible, "AGENT_LINK_PENDING", "Recorder Agent ещё не привязан к текущему пользователю.")
            { Authenticated = true, PipeReachable = true, InstallationIdPresent = true, InstallationId = installationId };

        if (enrollmentAgentId == Guid.Empty)
            return new(false, true, false, "AGENT_BOOTSTRAP_INVALID", "Сервер вернул некорректный Agent ID.")
            { Authenticated = true, PipeReachable = true, InstallationIdPresent = true, InstallationId = installationId };
        var agentId = enrollmentAgentId;

        if (!string.IsNullOrWhiteSpace(enrollment.Token))
        {
            settings = services.Settings.Load();
            var configured = await services.Recorder.ConfigureAgentAsync(
                services.Backend.ApiUrl,
                agentId,
                enrollment.Token,
                settings.ArchiveRoot ?? DesktopSettings.DefaultArchiveRoot(),
                settings.MicrophoneDeviceId,
                settings.SystemAudioDeviceId,
                cancellationToken);
            if (!configured.Ok)
                return new(false, true, offlineEligible, configured.Error ?? "AGENT_CONFIGURE_FAILED", "Recorder Agent не подтвердил конфигурацию.")
                { Authenticated = true, PipeReachable = true, InstallationIdPresent = true, InstallationId = installationId, AgentId = agentId };
        }
        else
        {
            // Existing Agents keep their token. Only the managed ServerOrigin
            // changes, persisted atomically by the Recorder with the existing
            // DPAPI-protected identity and device settings.
            var updated = await services.Recorder.UpdateServerUrlAsync(services.Backend.ApiUrl, cancellationToken);
            if (!updated.Ok)
                return new(false, true, offlineEligible, updated.Error ?? "AGENT_ORIGIN_UPDATE_FAILED", "Recorder Agent не подтвердил адрес LAN-сервера.")
                { Authenticated = true, PipeReachable = true, InstallationIdPresent = true, InstallationId = installationId, AgentId = agentId };
        }

        // Bootstrap is not proof that the local token is usable. The Agent must
        // send a fresh heartbeat with the same identity before recording is
        // advertised as ready.
        AgentIpcResponse verifiedHealth = health;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            verifiedHealth = await services.Recorder.GetHealthAsync(cancellationToken);
            var currentHealth = verifiedHealth.Health;
            var agentMatches = currentHealth?.AgentId == agentId;
            var heartbeatFresh = currentHealth?.LastHeartbeatAtUtc is { } heartbeat
                && DateTimeOffset.UtcNow - heartbeat.ToUniversalTime() <= TimeSpan.FromSeconds(90);
            var connected = string.Equals(currentHealth?.ServerConnectionState, "CONNECTED", StringComparison.OrdinalIgnoreCase);
            if (verifiedHealth.Ok && agentMatches && connected && heartbeatFresh)
            {
                var current = services.Settings.Load();
                services.Settings.Save(current with
                {
                    ApiUrl = services.Backend.ApiUrl,
                    Username = user.Username,
                    OwnerUserId = user.Id,
                    AgentBootstrapConfirmed = true
                });
                return new(true, true, true, "READY", "Пользователь привязан к Recorder Agent.")
                {
                    Authenticated = true,
                    PipeReachable = true,
                    InstallationIdPresent = true,
                    AgentConfigured = true,
                    UserLinked = true,
                    ServerConnected = true,
                    HeartbeatFresh = true,
                    AgentId = agentId,
                    InstallationId = installationId
                };
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var finalHealth = verifiedHealth.Health;
        var finalConnected = string.Equals(finalHealth?.ServerConnectionState, "CONNECTED", StringComparison.OrdinalIgnoreCase);
        var finalHeartbeatFresh = finalHealth?.LastHeartbeatAtUtc is { } finalHeartbeat
            && DateTimeOffset.UtcNow - finalHeartbeat.ToUniversalTime() <= TimeSpan.FromSeconds(90);
        return new(false, true, offlineEligible, finalConnected ? "AGENT_HEARTBEAT_STALE" : "AGENT_SERVER_UNAVAILABLE",
            finalConnected ? "Recorder Agent не подтвердил свежий heartbeat за 15 секунд." : "Recorder Agent не подключился к LAN-серверу.")
        {
            Authenticated = true,
            PipeReachable = true,
            InstallationIdPresent = true,
            AgentConfigured = finalHealth?.AgentId == agentId,
            UserLinked = true,
            ServerConnected = finalConnected,
            HeartbeatFresh = finalHeartbeatFresh,
            AgentId = agentId,
            InstallationId = installationId
        };
    }
}

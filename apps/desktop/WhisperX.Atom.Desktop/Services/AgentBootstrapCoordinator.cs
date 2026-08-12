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

    public async Task<AgentBootstrapStatus> EnsureAgentReadyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await EnsureAgentReadyCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<AgentBootstrapStatus> EnsureAgentReadyCoreAsync(CancellationToken cancellationToken)
    {
        var user = await services.Backend.GetCurrentUserAsync(cancellationToken);
        if (user is null && services.Backend.AuthState == DesktopAuthState.Offline)
            return new(false, false, services.Backend.CanUseOffline, "SERVER_UNAVAILABLE", "LAN-сервер временно недоступен; запись может продолжиться локально.");
        if (user is null)
            return new(false, false, services.Backend.CanUseOffline, "AUTH_REQUIRED", "Требуется вход в сервер.");

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
            return new(false, false, services.Backend.CanUseOffline, "RECORDER_UNAVAILABLE", message);
        }
        catch (Exception)
        {
            return new(false, false, services.Backend.CanUseOffline, "RECORDER_UNAVAILABLE", "Recorder Service недоступен. Проверьте локальную службу и Named Pipe.");
        }

        if (!health.Ok || health.Health is null)
            return new(false, false, false, "RECORDER_UNAVAILABLE", "Recorder Service не запущен или Named Pipe недоступен.");

        var agentHealth = health.Health;
        var installationId = agentHealth.InstallationId;
        if (installationId is null || installationId == Guid.Empty)
            return new(false, true, false, "INSTALLATION_ID_MISSING", "Recorder Agent не сообщил постоянный InstallationId.");

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
            return new(false, true, services.Backend.CanUseOffline,
                exception.Retryable ? "SERVER_UNAVAILABLE" : exception.ErrorCode,
                exception.Retryable
                    ? "LAN-сервер временно недоступен. Локальная запись останется доступной после подтверждённой привязки."
                    : $"Сервер отклонил привязку Recorder Agent: {exception.ErrorCode}.");
        }
        catch (HttpRequestException)
        {
            return new(false, true, services.Backend.CanUseOffline, "SERVER_UNAVAILABLE",
                "LAN-сервер недоступен. Повторите синхронизацию после восстановления сети.");
        }

        if (enrollment.ReenrollRequired)
            return new(false, true, false, "REENROLL_REQUIRED", "Recorder Agent требует повторной регистрации.");

        if (!Guid.TryParse(enrollment.AgentId, out var agentId))
            return new(false, true, false, "AGENT_BOOTSTRAP_INVALID", "Сервер вернул некорректный Agent ID.");

        if (!string.IsNullOrWhiteSpace(enrollment.Token))
        {
            var settings = services.Settings.Load();
            var configured = await services.Recorder.ConfigureAgentAsync(
                services.Backend.ApiUrl,
                agentId,
                enrollment.Token,
                settings.ArchiveRoot ?? DesktopSettings.DefaultArchiveRoot(),
                settings.MicrophoneDeviceId,
                settings.SystemAudioDeviceId,
                cancellationToken);
            if (!configured.Ok)
                return new(false, true, false, configured.Error ?? "AGENT_CONFIGURE_FAILED", "Recorder Agent не подтвердил конфигурацию.");
        }

        var current = services.Settings.Load();
        services.Settings.Save(current with
        {
            ApiUrl = services.Backend.ApiUrl,
            Username = user.Username,
            OwnerUserId = user.Id,
            AgentBootstrapConfirmed = true
        });
        return new(true, true, true, "READY", "Пользователь привязан к Recorder Agent.");
    }
}

using System.Diagnostics;
using System.Text.Json;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>Owns the hidden, current-user Voice Host lifetime.</summary>
public sealed class VoiceHostController : IAsyncDisposable
{
    private readonly FrontendServices _services;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Queue<DateTimeOffset> _restartHistory = new();
    private Process? _process;
    private DesktopVoiceBrokerServer? _broker;
    private int _intentionalStop;
    private int _exitHandling;
    private DateTimeOffset? _stableSinceUtc;

    public VoiceHostController(FrontendServices services) => _services = services;
    public string State { get; private set; } = "OFF";
    public string? LastErrorCode { get; private set; }
    public string? LastErrorDetail { get; private set; }
    public string ExpectedBuildIdentity => CurrentBuildIdentity();
    public string? InstalledPath => ResolveExecutable();
    public string? LastObservedBuildIdentity { get; private set; }
    public string? LastObservedProcessPath { get; private set; }
    public int? LastObservedProcessId { get; private set; }
    public int RestartCount { get; private set; }
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StartCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<bool> StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: true })
        {
            try { _process.Exited -= VoiceHostExited; } catch { }
            _process.Dispose();
            _process = null;
        }
        if (_process is { HasExited: false })
        {
            if (await IsExpectedRuntimeAsync(cancellationToken).ConfigureAwait(false)) return true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        var executable = ResolveExecutable();
        if (executable is null)
        {
            State = "NEEDS_SETUP";
            LastErrorCode = "VOICE_HOST_NOT_INSTALLED";
            return false;
        }

        var expectedPath = Path.GetFullPath(executable);
        foreach (var existing in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                var actualPath = existing.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(actualPath))
                {
                    State = "ERROR";
                    LastErrorCode = "VOICE_HOST_PROCESS_UNINSPECTABLE";
                    LastObservedProcessId = existing.Id;
                    LastObservedProcessPath = null;
                    LastErrorDetail = $"PID {existing.Id}: путь процесса недоступен; ожидаемый путь: {expectedPath}";
                    existing.Dispose();
                    return false;
                }
                if (!string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    State = "ERROR";
                    SetBuildMismatch(existing.Id, actualPath, TryReadProcessBuild(existing));
                    existing.Dispose();
                    return false;
                }
                if (!WindowsProcessIdentity.TryGetOwnerSid(existing.Id, out var ownerSid))
                {
                    State = "ERROR";
                    LastErrorCode = "VOICE_HOST_PROCESS_UNINSPECTABLE";
                    LastObservedProcessId = existing.Id;
                    LastObservedProcessPath = actualPath;
                    LastErrorDetail = $"PID {existing.Id}, путь {actualPath}: SID владельца недоступен.";
                    existing.Dispose();
                    return false;
                }
                var currentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                if (!string.Equals(ownerSid, currentSid, StringComparison.OrdinalIgnoreCase))
                {
                    State = "ERROR";
                    LastErrorCode = "VOICE_HOST_OWNER_MISMATCH";
                    LastObservedProcessId = existing.Id;
                    LastObservedProcessPath = actualPath;
                    LastErrorDetail = $"PID {existing.Id}, путь {actualPath}: процесс принадлежит SID {ownerSid}, текущий SID {currentSid}.";
                    existing.Dispose();
                    return false;
                }
                var existingStatus = await new WhisperX.Atom.Desktop.VoiceHostClient().GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (existingStatus is null || existingStatus.ProcessId != existing.Id)
                {
                    State = "ERROR";
                    LastErrorCode = "VOICE_HOST_PROCESS_UNINSPECTABLE";
                    LastObservedProcessId = existing.Id;
                    LastObservedProcessPath = actualPath;
                    LastErrorDetail = $"PID {existing.Id}, путь {actualPath}: Voice Host не подтвердил собственный процесс через pipe.";
                    existing.Dispose();
                    return false;
                }
                if (!string.Equals(existingStatus.BuildIdentity, CurrentBuildIdentity(), StringComparison.OrdinalIgnoreCase))
                {
                    State = "ERROR";
                    SetBuildMismatch(existing.Id, actualPath, existingStatus.BuildIdentity);
                    existing.Dispose();
                    return false;
                }
                // Reclaim an instance that was started outside the Desktop
                // broker (legacy tray/development mode) so commands cannot
                // bypass RecordingCommandService.
                try
                {
                    await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync("SHUTDOWN", cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!existing.WaitForExit(1500))
                    {
                        State = "ERROR";
                        LastErrorCode = "VOICE_HOST_SHUTDOWN_TIMEOUT";
                        existing.Dispose();
                        return false;
                    }
                }
                catch { }
                existing.Dispose();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                State = "ERROR";
                LastErrorCode = "VOICE_HOST_PROCESS_UNINSPECTABLE";
                existing.Dispose();
                return false;
            }
            catch (InvalidOperationException) when (!existing.HasExited)
            {
                State = "ERROR";
                LastErrorCode = "VOICE_HOST_PROCESS_UNINSPECTABLE";
                existing.Dispose();
                return false;
            }
            catch
            {
                State = "ERROR";
                LastErrorCode = "VOICE_HOST_PROCESS_UNINSPECTABLE";
                existing.Dispose();
                return false;
            }
        }

        _broker ??= new DesktopVoiceBrokerServer(_services.RecordingCommands);
        _broker.Start();
        var settings = _services.Settings.Load();
        // Keep the always-listening host on the same endpoint that Recorder
        // actually resolved. DEFAULT is retained only when Recorder has not
        // reported an effective device yet.
        var microphoneId = settings.MicrophoneDeviceId;
        try
        {
            var health = await _services.Recorder.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            microphoneId = health.Health?.EffectiveMicrophoneDeviceId
                ?? health.Health?.SelectedMicrophoneDeviceId
                ?? microphoneId;
        }
        catch { }
        var arguments = $"--managed --parent-pid {Environment.ProcessId} --broker-pipe WhisperXAtomDesktopVoiceBroker";
        if (!string.IsNullOrWhiteSpace(microphoneId))
            arguments += $" --microphone-id \"{microphoneId.Replace("\"", string.Empty)}\"";
        try
        {
            Interlocked.Exchange(ref _intentionalStop, 0);
            _process ??= Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (_process is null) throw new InvalidOperationException("Process.Start returned null.");
            _process.EnableRaisingEvents = true;
            _process.Exited += VoiceHostExited;
            State = "STARTING";
            LastErrorCode = null;
            LastErrorDetail = null;
            LastObservedBuildIdentity = null;
            LastObservedProcessPath = null;
            LastObservedProcessId = null;
            await WaitForPipeAsync(cancellationToken).ConfigureAwait(false);
            var client = new WhisperX.Atom.Desktop.VoiceHostClient();
            var configured = await client.SendAsync("CONFIGURE", new
            {
                microphoneDeviceId = microphoneId,
                enabled = settings.VoiceAlwaysListening,
                quietMode = settings.VoiceQuietMode,
                sensitivity = settings.VoiceSensitivity,
                voiceName = settings.VoiceName,
                voiceRate = settings.VoiceRate,
                voiceVolume = settings.VoiceVolume
            }, cancellationToken).ConfigureAwait(false);
            if (!configured.Ok)
            {
                LastErrorCode = configured.Error ?? "VOICE_MICROPHONE_UNAVAILABLE";
                LastErrorDetail = ReadResponseDetail(configured) ?? LastErrorDetail;
                State = "ERROR";
                await StopCoreAsync().ConfigureAwait(false);
                return false;
            }
            State = "LISTENING";
            _stableSinceUtc = DateTimeOffset.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            await StopCoreAsync().ConfigureAwait(false);
            LastErrorCode = ex is VoiceHostBuildMismatchException
                ? "VOICE_HOST_BUILD_MISMATCH"
                : ex is System.ComponentModel.Win32Exception ? "VOICE_HOST_START_FAILED" : "VOICE_HOST_UNAVAILABLE";
            State = "ERROR";
            return false;
        }
    }

    private async Task WaitForPipeAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var client = new WhisperX.Atom.Desktop.VoiceHostClient();
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status is not null)
                {
                    if (!string.Equals(status.BuildIdentity, CurrentBuildIdentity(), StringComparison.OrdinalIgnoreCase))
                    {
                        var observedPath = TryGetProcessPath(status.ProcessId);
                        SetBuildMismatch(status.ProcessId, observedPath, status.BuildIdentity);
                        throw new VoiceHostBuildMismatchException(status.BuildIdentity);
                    }
                    // The host deliberately rejects mutating IPC while its
                    // microphone/model readiness is being initialized. Wait
                    // for the first non-STARTING snapshot before applying
                    // Desktop settings.
                    if (!string.Equals(status.State, "STARTING", StringComparison.OrdinalIgnoreCase)) return;
                }
            }
            catch (VoiceHostBuildMismatchException) { throw; }
            catch { }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("VOICE_HOST_PIPE_TIMEOUT");
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<bool> ConfigureAsync(string? microphoneDeviceId, bool enabled, bool quietMode, string sensitivity, CancellationToken cancellationToken = default, string? voiceName = null, int voiceRate = 0, int voiceVolume = 90)
    {
        if (!enabled && (_process is null || _process.HasExited))
        {
            State = "OFF";
            LastErrorCode = null;
            return true;
        }
        if (enabled && (_process is null || _process.HasExited))
        {
            if (!await StartAsync(cancellationToken).ConfigureAwait(false)) return false;
        }
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync("CONFIGURE", new
            {
                microphoneDeviceId,
                enabled,
                quietMode,
                sensitivity,
                voiceName,
                voiceRate,
                voiceVolume
            }, cancellationToken).ConfigureAwait(false);
            if (!response.Ok)
            {
                LastErrorCode = response.Error ?? "VOICE_MICROPHONE_UNAVAILABLE";
                LastErrorDetail = ReadResponseDetail(response) ?? LastErrorDetail;
                State = "ERROR";
                return false;
            }
            LastErrorCode = null;
            State = enabled ? "LISTENING" : "OFF";
            return true;
        }
        catch (Exception ex)
        {
            LastErrorCode = ex is TimeoutException ? "VOICE_HOST_HEARTBEAT_EXPIRED" : "VOICE_HOST_UNAVAILABLE";
            State = "ERROR";
            return false;
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref _intentionalStop, 1);
        try
        {
            if (_process is { HasExited: false })
            {
                try
                {
                    var client = new WhisperX.Atom.Desktop.VoiceHostClient();
                    await client.SendAsync("SHUTDOWN").ConfigureAwait(false);
                }
                catch { }
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            if (_process is not null)
            {
                try { _process.Exited -= VoiceHostExited; } catch { }
            }
            _process?.Dispose();
            _process = null;
            State = "OFF";
            if (_broker is not null) await _broker.DisposeAsync().ConfigureAwait(false);
            _broker = null;
        }
    }

    private void VoiceHostExited(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _intentionalStop) != 0) return;
        try { if (_services.Settings.Load().VoiceAlwaysListening == false) return; }
        catch { return; }
        _ = RecoverUnexpectedExitAsync();
    }

    private async Task RecoverUnexpectedExitAsync()
    {
        if (Interlocked.CompareExchange(ref _exitHandling, 1, 0) != 0) return;
        try
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _intentionalStop) != 0 || _services.Settings.Load().VoiceAlwaysListening == false) return;
                if (_stableSinceUtc is { } stable && DateTimeOffset.UtcNow - stable >= TimeSpan.FromMinutes(10))
                {
                    _restartHistory.Clear();
                    RestartCount = 0;
                }
                var now = DateTimeOffset.UtcNow;
                while (_restartHistory.Count > 0 && now - _restartHistory.Peek() > TimeSpan.FromMinutes(5)) _restartHistory.Dequeue();
                if (_restartHistory.Count >= 3)
                {
                    State = "DEGRADED";
                    LastErrorCode = "VOICE_HOST_RESTART_LIMIT";
                    return;
                }
                var delay = _restartHistory.Count switch { 0 => 1, 1 => 2, _ => 5 };
                _restartHistory.Enqueue(now);
                RestartCount++;
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                if (_process is not null)
                {
                    try { _process.Exited -= VoiceHostExited; } catch { }
                    _process.Dispose();
                    _process = null;
                }
                Interlocked.Exchange(ref _intentionalStop, 0);
                await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { _lifecycleGate.Release(); }
        }
        catch (Exception ex)
        {
            State = "DEGRADED";
            LastErrorCode = ex is TimeoutException ? "VOICE_HOST_HEARTBEAT_EXPIRED" : "VOICE_HOST_RESTART_FAILED";
        }
        finally { Interlocked.Exchange(ref _exitHandling, 0); }
    }

    private static string? ResolveExecutable()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhisperX Atom", "VoiceHost", "WhisperX.Atom.Voice.Host.exe");
        return File.Exists(installed) ? installed : null;
    }

    private void SetBuildMismatch(int? processId, string? actualPath, string? actualBuild)
    {
        State = "ERROR";
        LastErrorCode = "VOICE_HOST_BUILD_MISMATCH";
        LastObservedProcessId = processId;
        LastObservedProcessPath = actualPath;
        LastObservedBuildIdentity = actualBuild;
        LastErrorDetail = $"Ожидалась сборка '{ExpectedBuildIdentity}', обнаружена '{actualBuild ?? "неизвестно"}'; PID {processId?.ToString() ?? "неизвестен"}; путь '{actualPath ?? "неизвестен"}'; ожидаемый путь '{InstalledPath ?? "не установлен"}'.";
    }

    private static string? TryGetProcessPath(int? processId)
    {
        if (processId is not > 0) return null;
        try { return Process.GetProcessById(processId.Value).MainModule?.FileName; }
        catch { return null; }
    }

    private static string? TryReadProcessBuild(Process process)
    {
        try { return process.MainModule?.FileVersionInfo.ProductVersion; }
        catch { return null; }
    }

    private static string? ReadResponseDetail(WhisperX.Atom.Desktop.DesktopVoiceResponse response)
    {
        if (response.Data is not JsonElement data || data.ValueKind != JsonValueKind.Object) return null;
        return data.TryGetProperty("microphoneErrorDetail", out var detail) && detail.ValueKind == JsonValueKind.String
            ? detail.GetString()
            : null;
    }

    private async Task<bool> IsExpectedRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await new WhisperX.Atom.Desktop.VoiceHostClient().GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var expected = snapshot is not null
                && _process is { HasExited: false } process
                && snapshot.ProcessId == process.Id
                && string.Equals(snapshot.BuildIdentity, CurrentBuildIdentity(), StringComparison.OrdinalIgnoreCase);
            if (expected && _stableSinceUtc is { } stable && DateTimeOffset.UtcNow - stable >= TimeSpan.FromMinutes(10))
            {
                _restartHistory.Clear();
                RestartCount = 0;
                _stableSinceUtc = DateTimeOffset.UtcNow;
            }
            return expected;
        }
        catch { return false; }
    }

    private static string CurrentBuildIdentity() =>
        typeof(VoiceHostController).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(attribute => attribute.InformationalVersion)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? typeof(VoiceHostController).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    private sealed class VoiceHostBuildMismatchException(string? actual)
        : InvalidOperationException($"Voice Host build mismatch: {actual ?? "missing"}");
}

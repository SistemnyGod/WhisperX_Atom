using System.Diagnostics;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>Owns the hidden, current-user Voice Host lifetime.</summary>
public sealed class VoiceHostController : IAsyncDisposable
{
    private readonly FrontendServices _services;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Process? _process;
    private DesktopVoiceBrokerServer? _broker;

    public VoiceHostController(FrontendServices services) => _services = services;
    public string State { get; private set; } = "OFF";
    public string? LastErrorCode { get; private set; }
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
                    existing.Dispose();
                    return false;
                }
                if (!string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    State = "ERROR";
                    LastErrorCode = "VOICE_HOST_BUILD_MISMATCH";
                    existing.Dispose();
                    return false;
                }
                // Reclaim an instance that was started outside the Desktop
                // broker (legacy tray/development mode) so commands cannot
                // bypass RecordingCommandService.
                try
                {
                    await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync("SHUTDOWN", cancellationToken: cancellationToken).ConfigureAwait(false);
                    existing.WaitForExit(1500);
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
            catch { existing.Dispose(); }
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
            State = "STARTING";
            LastErrorCode = null;
            await WaitForPipeAsync(cancellationToken).ConfigureAwait(false);
            var client = new WhisperX.Atom.Desktop.VoiceHostClient();
            await client.SendAsync("QUIET_MODE", new { enabled = settings.VoiceQuietMode }, cancellationToken).ConfigureAwait(false);
            await client.SendAsync("SET_SENSITIVITY", new { sensitivity = settings.VoiceSensitivity }, cancellationToken).ConfigureAwait(false);
            State = "LISTENING";
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
                        throw new VoiceHostBuildMismatchException(status.BuildIdentity);
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

    private async Task StopCoreAsync()
    {
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
            _process?.Dispose();
            _process = null;
            State = "OFF";
            if (_broker is not null) await _broker.DisposeAsync().ConfigureAwait(false);
            _broker = null;
        }
    }

    private static string? ResolveExecutable()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhisperX Atom", "VoiceHost", "WhisperX.Atom.Voice.Host.exe");
        return File.Exists(installed) ? installed : null;
    }

    private async Task<bool> IsExpectedRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await new WhisperX.Atom.Desktop.VoiceHostClient().GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return snapshot is not null && string.Equals(snapshot.BuildIdentity, CurrentBuildIdentity(), StringComparison.OrdinalIgnoreCase);
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

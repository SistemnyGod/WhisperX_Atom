using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;

namespace WhisperX_Atom_Desktop.Services;

public sealed record RecorderServiceSnapshot(
    string ServiceName,
    string State,
    bool Exists,
    bool PipeReachable,
    string? BinaryPath,
    string? Version,
    string? Error,
    int? ProcessId = null,
    string? BuildIdentity = null,
    IReadOnlyList<string>? Capabilities = null)
{
    public bool Running => string.Equals(State, "RUNNING", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Read-only service inspection plus an explicit UAC start action. Desktop never
/// silently changes service configuration during ordinary polling.
/// </summary>
public sealed class RecorderServiceController(IRecorderService recorder)
{
    public const string ServiceName = "WhisperXAtomRecorder";
    public const string HostProcessName = "WhisperX.Atom.Recorder.Host";

    private static bool IsAudioGraph => RecorderRuntimeMode.IsAudioGraph;

    public async Task<RecorderServiceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (IsAudioGraph)
            return await GetHostSnapshotAsync(cancellationToken);

        var query = await RunScAsync($"query \"{ServiceName}\"", cancellationToken);
        var exists = query.ExitCode == 0;
        var state = exists ? ParseState(query.Output) : "NOT_FOUND";
        string? binaryPath = null;
        string? version = null;
        if (exists)
        {
            var config = await RunScAsync($"qc \"{ServiceName}\"", cancellationToken);
            binaryPath = ParseValue(config.Output, "BINARY_PATH_NAME:");
            if (!string.IsNullOrWhiteSpace(binaryPath))
            {
                var path = binaryPath.Trim().Trim('"');
                try { if (File.Exists(path)) version = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { }
            }
        }

        var pipeReachable = false;
        if (exists && string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            try { pipeReachable = (await recorder.GetHealthAsync(cancellationToken)).Ok; }
            catch (RecorderIpcException) { }
            catch (IOException) { }
        }
        return new RecorderServiceSnapshot(ServiceName, state, exists, pipeReachable, binaryPath, version,
            exists ? null : "RECORDER_SERVICE_NOT_FOUND");
    }

    public async Task<RecorderServiceSnapshot> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsAudioGraph)
            return await StartHostAsync(cancellationToken);

        var before = await GetSnapshotAsync(cancellationToken);
        if (!before.Exists) return before with { Error = "RECORDER_SERVICE_NOT_FOUND" };
        if (!before.Running)
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Start-Service -Name '{ServiceName}'\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using var process = Process.Start(start);
                if (process is not null) await process.WaitForExitAsync(cancellationToken);
            }
            catch (Win32Exception exception)
            {
                return before with { Error = exception.NativeErrorCode == 1223 ? "UAC_CANCELLED" : "RECORDER_SERVICE_START_FAILED" };
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        RecorderServiceSnapshot snapshot = before;
        while (DateTimeOffset.UtcNow < deadline)
        {
            snapshot = await GetSnapshotAsync(cancellationToken);
            if (snapshot.Running && snapshot.PipeReachable) return snapshot;
            await Task.Delay(500, cancellationToken);
        }
        return snapshot with { Error = snapshot.Running ? "RECORDER_PIPE_UNAVAILABLE" : "RECORDER_SERVICE_START_TIMEOUT" };
    }

    private async Task<RecorderServiceSnapshot> GetHostSnapshotAsync(CancellationToken cancellationToken)
    {
        var process = InspectHostProcess();
        try
        {
            var response = await recorder.GetHealthAsync(cancellationToken);
            var health = response.Health;
            var executable = ResolveHostExecutable();
            var expectedBuild = GetFileVersion(executable);
            var buildMatches = string.IsNullOrWhiteSpace(health?.RuntimeBuildIdentity)
                || string.IsNullOrWhiteSpace(expectedBuild)
                || string.Equals(health.RuntimeBuildIdentity, expectedBuild, StringComparison.OrdinalIgnoreCase);
            var hasConcurrentHostCapabilities = health?.Capabilities?.Contains(AgentIpcProtocol.ConcurrentRequestsCapability, StringComparer.OrdinalIgnoreCase) == true;
            var hasEventStreamCapability = health?.Capabilities?.Contains(AgentIpcProtocol.DeviceEventStreamCapability, StringComparer.OrdinalIgnoreCase) == true;
            var capabilityError = hasConcurrentHostCapabilities && hasEventStreamCapability ? null : "RECORDER_HOST_UPDATE_REQUIRED";
            var pipeResponsive = health is not null;
            var error = response.Error;
            if (!buildMatches || capabilityError is not null)
                error = capabilityError ?? "RECORDER_HOST_UPDATE_REQUIRED";
            return new RecorderServiceSnapshot(
                RecorderPipeNames.AudioGraphHost,
                pipeResponsive ? "RUNNING" : "UNKNOWN",
                Exists: pipeResponsive || process.Exists,
                PipeReachable: pipeResponsive && error is null,
                BinaryPath: executable,
                Version: health?.RuntimeBuildIdentity ?? GetFileVersion(executable),
                Error: error,
                ProcessId: process.ProcessId,
                BuildIdentity: health?.RuntimeBuildIdentity,
                Capabilities: health?.Capabilities);
        }
        catch (RecorderIpcException exception) when (string.Equals(exception.ErrorCode, "RECORDER_IPC_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase))
        {
            // The pipe exists but its ACL does not allow this desktop token.
            // Do not report the Host as stopped or try to launch a competing
            // second instance; surface the actionable IPC error instead.
            var executable = ResolveHostExecutable();
            return new RecorderServiceSnapshot(
                RecorderPipeNames.AudioGraphHost,
                "UNKNOWN",
                Exists: true,
                PipeReachable: false,
                BinaryPath: executable,
                Version: GetFileVersion(executable),
                Error: "RECORDER_IPC_ACCESS_DENIED",
                ProcessId: process.ProcessId);
        }
        catch (UnauthorizedAccessException)
        {
            // The pipe exists but its ACL does not allow this desktop token.
            // Do not report the Host as stopped or try to launch a competing
            // second instance; surface the actionable IPC error instead.
            var executable = ResolveHostExecutable();
            return new RecorderServiceSnapshot(
                RecorderPipeNames.AudioGraphHost,
                "UNKNOWN",
                Exists: true,
                PipeReachable: false,
                BinaryPath: executable,
                Version: GetFileVersion(executable),
                Error: "RECORDER_IPC_ACCESS_DENIED",
                ProcessId: process.ProcessId);
        }
        catch (RecorderIpcException exception) when (string.Equals(exception.ErrorCode, "IPC_VERSION_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase))
        {
            return new RecorderServiceSnapshot(
                RecorderPipeNames.AudioGraphHost,
                process.Exists ? "RUNNING" : "NOT_FOUND",
                Exists: process.Exists,
                PipeReachable: false,
                BinaryPath: process.Path ?? ResolveHostExecutable(),
                Version: process.BuildIdentity ?? GetFileVersion(process.Path ?? ResolveHostExecutable()),
                Error: "RECORDER_HOST_UPDATE_REQUIRED",
                ProcessId: process.ProcessId,
                BuildIdentity: process.BuildIdentity);
        }
        catch (Exception exception) when (exception is RecorderIpcException or IOException or TimeoutException)
        {
            var expectedBuild = GetFileVersion(ResolveHostExecutable());
            var staleBuild = process.Exists
                && !string.IsNullOrWhiteSpace(process.BuildIdentity)
                && !string.IsNullOrWhiteSpace(expectedBuild)
                && !string.Equals(process.BuildIdentity, expectedBuild, StringComparison.OrdinalIgnoreCase);
            var error = staleBuild
                ? "RECORDER_HOST_UPDATE_RESTART_REQUIRED"
                : process.Exists ? "RECORDER_HOST_PIPE_UNRESPONSIVE" : "RECORDER_HOST_NOT_RUNNING";
            return new RecorderServiceSnapshot(
                RecorderPipeNames.AudioGraphHost,
                process.Exists ? "RUNNING" : "NOT_FOUND",
                Exists: process.Exists,
                PipeReachable: false,
                BinaryPath: process.Path ?? ResolveHostExecutable(),
                Version: process.BuildIdentity ?? GetFileVersion(process.Path ?? ResolveHostExecutable()),
                Error: error,
                ProcessId: process.ProcessId,
                BuildIdentity: process.BuildIdentity);
        }
    }

    private async Task<RecorderServiceSnapshot> StartHostAsync(CancellationToken cancellationToken)
    {
        var before = await GetHostSnapshotAsync(cancellationToken);
        if (before.PipeReachable) return before;
        if (before.Exists && before.Error is "RECORDER_IPC_ACCESS_DENIED" or "RECORDER_HOST_PIPE_UNRESPONSIVE" or "RECORDER_HOST_UPDATE_REQUIRED")
            return before;

        var executable = ResolveHostExecutable();
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            var directStart = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            directStart.Environment["AUDIO_CAPTURE_ENGINE"] = "AUDIOGRAPH";
            directStart.Environment["ATOM_AGENT_DATA_ROOT"] = @"C:\ProgramData\WhisperXAtom\Agent";
            directStart.Environment["ATOM_AGENT_CONFIG_PATH"] = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WhisperXAtom", "Agent", "agent-config.json");
            directStart.Environment["ATOM_AGENT_DPAPI_SCOPE"] = "CURRENT_USER";
            var currentUserSid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(currentUserSid))
                directStart.Environment["ATOM_AGENT_ALLOWED_SID"] = currentUserSid;
            var installationId = ResolveMachineInstallationId();
            if (!string.IsNullOrWhiteSpace(installationId))
                directStart.Environment["ATOM_AGENT_INSTALLATION_ID"] = installationId;
            directStart.Environment["ATOM_AGENT_FFMPEG_PATH"] = Path.Combine(Path.GetDirectoryName(executable)!, "ffmpeg.exe");
            directStart.Environment["ATOM_AGENT_FFPROBE_PATH"] = Path.Combine(Path.GetDirectoryName(executable)!, "ffprobe.exe");
            try
            {
                using var process = Process.Start(directStart);
                if (process is not null) await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return before with { Error = exception is Win32Exception win32 && win32.NativeErrorCode == 1223 ? "UAC_CANCELLED" : "RECORDER_HOST_START_FAILED" };
            }
        }
        else
        {
            var script = Environment.GetEnvironmentVariable("WHISPERX_RECORDER_HOST_SCRIPT");
            if (string.IsNullOrWhiteSpace(script))
            {
                var repo = Environment.GetEnvironmentVariable("WHISPERX_ATOM_REPO");
                if (!string.IsNullOrWhiteSpace(repo))
                    script = Path.Combine(repo, "scripts", "start-recorder-host.ps1");
            }

            if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
                return before with { Error = "RECORDER_HOST_BINARY_NOT_FOUND" };

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ReadyTimeoutSeconds 20",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using var process = Process.Start(start);
                if (process is not null) await process.WaitForExitAsync(cancellationToken);
            }
            catch (Win32Exception exception)
            {
                return before with { Error = exception.NativeErrorCode == 1223 ? "UAC_CANCELLED" : "RECORDER_HOST_START_FAILED" };
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        RecorderServiceSnapshot snapshot = before;
        while (DateTimeOffset.UtcNow < deadline)
        {
            snapshot = await GetHostSnapshotAsync(cancellationToken);
            if (snapshot.PipeReachable) return snapshot;
            await Task.Delay(500, cancellationToken);
        }
        return snapshot with { Error = "RECORDER_HOST_START_TIMEOUT" };
    }

    private static string? ResolveMachineInstallationId()
    {
        try
        {
            return WhisperX.Atom.Desktop.MachineServerConfig.Load()?.InstallationId?.ToString();
        }
        catch { }
        return null;
    }

    private static HostProcessInspection InspectHostProcess()
    {
        try
        {
            var currentSession = Process.GetCurrentProcess().SessionId;
            foreach (var process in Process.GetProcessesByName(HostProcessName))
            {
                try
                {
                    if (process.SessionId != currentSession) continue;
                    var path = TryGetProcessPath(process);
                    return new HostProcessInspection(true, process.Id, path, GetFileVersion(path));
                }
                finally { process.Dispose(); }
            }
        }
        catch { }

        return new HostProcessInspection(false, null, null, null);
    }

    private static string? TryGetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private sealed record HostProcessInspection(bool Exists, int? ProcessId, string? Path, string? BuildIdentity);

    internal static string? ResolveHostExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("WHISPERX_RECORDER_HOST_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        // Installed layout: {app}\Desktop\WhisperX.Atom.Desktop.exe and
        // {app}\RecorderHost\WhisperX.Atom.Recorder.Host.exe. This fallback
        // makes the package self-contained even when a user environment block
        // was not refreshed after installation.
        var adjacent = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "RecorderHost", "WhisperX.Atom.Recorder.Host.exe"));
        if (File.Exists(adjacent)) return adjacent;

        return null;
    }

    private static string? GetFileVersion(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        }
        catch { return null; }
    }

    private static string ParseState(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("STATE", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "RUNNING";
            if (line.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "STOPPED";
            if (line.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)) return "START_PENDING";
        }
        return "UNKNOWN";
    }

    private static string? ParseValue(string output, string key)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(item => item.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[(line.IndexOf(':') + 1)..].Trim();
    }

    private static async Task<(int ExitCode, string Output)> RunScAsync(string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        output += await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output);
    }
}

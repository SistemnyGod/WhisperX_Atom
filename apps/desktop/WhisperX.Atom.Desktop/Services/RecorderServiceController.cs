using System.ComponentModel;
using System.Diagnostics;
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
    string? Error)
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
        try
        {
            var response = await recorder.GetHealthAsync(cancellationToken);
            var health = response.Health;
            return new RecorderServiceSnapshot(
                RecorderPipeNames.AudioGraphHost,
                response.Ok ? "RUNNING" : "UNKNOWN",
                Exists: response.Ok,
                PipeReachable: response.Ok,
                BinaryPath: null,
                Version: null,
                Error: response.Ok ? null : response.Error ?? "RECORDER_HOST_UNAVAILABLE");
        }
        catch (Exception exception) when (exception is RecorderIpcException or IOException or TimeoutException)
        {
            return new RecorderServiceSnapshot(
                RecorderPipeNames.AudioGraphHost,
                "STOPPED",
                Exists: false,
                PipeReachable: false,
                BinaryPath: null,
                Version: null,
                Error: "RECORDER_HOST_UNAVAILABLE");
        }
    }

    private async Task<RecorderServiceSnapshot> StartHostAsync(CancellationToken cancellationToken)
    {
        var before = await GetHostSnapshotAsync(cancellationToken);
        if (before.PipeReachable) return before;

        var executable = Environment.GetEnvironmentVariable("WHISPERX_RECORDER_HOST_EXE");
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
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "client-config.json");
            if (!File.Exists(path)) return null;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("installationId", out var value)) return value.GetString();
            if (document.RootElement.TryGetProperty("InstallationId", out value)) return value.GetString();
        }
        catch { }
        return null;
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

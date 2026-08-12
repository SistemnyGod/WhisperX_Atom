using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WhisperX.Atom.Desktop;

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

    public async Task<RecorderServiceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
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

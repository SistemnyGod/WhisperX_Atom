using System.Diagnostics;
using System.ComponentModel;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Runs FFmpeg/ffprobe without allowing a child process to pin the encoder or
/// archive worker forever.  Both output streams are drained concurrently and
/// the complete process tree is terminated on timeout/cancellation.
/// </summary>
internal static class ExternalProcessRunner
{
    public sealed record Result(int ExitCode, string StandardOutput, string StandardError);

    public static TimeSpan TimeoutFromEnvironment(string variable, int defaultSeconds, int minimumSeconds, int maximumSeconds)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (!int.TryParse(raw, out var seconds)) seconds = defaultSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, minimumSeconds, maximumSeconds));
    }

    public static Task<Result> RunEncoderAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => RunAsync(
            fileName,
            arguments,
            TimeoutFromEnvironment("ATOM_ENCODER_TIMEOUT_SECONDS", 120, 10, 900),
            "ENCODER_TIMEOUT",
            cancellationToken);

    public static Task<Result> RunArchiveAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => RunAsync(
            fileName,
            arguments,
            TimeoutFromEnvironment("ATOM_ARCHIVE_TIMEOUT_SECONDS", 900, 30, 3600),
            "ARCHIVE_TIMEOUT",
            cancellationToken);

    public static async Task<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string timeoutCode,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("process_start_failed");
        }
        catch (Win32Exception)
        {
            throw;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(process);
            await DrainAfterTerminationAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw new ExternalProcessTimeoutException(timeoutCode, fileName, timeout);
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            await DrainAfterTerminationAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new Result(process.ExitCode, stdout, stderr);
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task DrainAfterTerminationAsync(Process process, Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception) { }
        try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { }
    }
}

internal sealed class ExternalProcessTimeoutException(string code, string fileName, TimeSpan timeout)
    : TimeoutException($"{code}: {fileName} exceeded {timeout.TotalSeconds:0}s")
{
    public string Code { get; } = code;
}

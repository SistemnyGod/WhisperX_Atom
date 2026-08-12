using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>
/// Single client-side path for long-running transcript and summary jobs.
/// The request that creates a job is not treated as completion; callers wait
/// for the terminal server event before reloading the resulting version.
/// </summary>
public sealed class ProcessingJobTracker(IBackendService backend)
{
    public async Task<DesktopJob?> WaitForTerminalAsync(
        DesktopJob job,
        Action<DesktopJob>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(job);
        if (IsTerminal(job.Status)) return job;
        if (!Guid.TryParse(job.Id, out var jobId)) return job;

        var current = await backend.WaitForJobEventsAsync(jobId, cancellationToken) ?? job;
        progress?.Invoke(current);
        if (IsTerminal(current.Status)) return current;

        // The server SSE endpoint has a bounded connection lifetime. Long
        // WhisperX/Qwen jobs must continue through the durable job resource
        // instead of being reported as failed when that stream closes.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            current = await backend.GetJobAsync(jobId, cancellationToken) ?? current;
            progress?.Invoke(current);
            if (IsTerminal(current.Status)) return current;
        }

        return current;
    }

    private static bool IsTerminal(string? status) => status is not null
        && (status.Equals("READY", StringComparison.OrdinalIgnoreCase)
            || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
            || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase));
}

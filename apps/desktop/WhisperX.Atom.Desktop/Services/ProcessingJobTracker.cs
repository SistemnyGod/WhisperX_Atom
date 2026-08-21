using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

public enum ProcessingJobState
{
    Running,
    Background,
    Stalled,
    Blocked,
    Ready,
    Failed
}

public sealed record ProcessingJobObservation(
    string JobId,
    ProcessingJobState State,
    string? Reason = null,
    DesktopPipelineSnapshot? Pipeline = null);

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
        CancellationToken cancellationToken = default,
        Action<ProcessingJobObservation>? observation = null)
    {
        progress?.Invoke(job);
        EmitObservation(job, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, observation);
        if (IsTerminal(job.Status)) return job;
        if (!Guid.TryParse(job.Id, out var jobId)) return job;

        // SSE is an optimization, not the liveness mechanism.  A proxy or a
        // server that keeps the stream open without events must not block the
        // UI indefinitely; after a short probe the durable job resource below
        // becomes authoritative.
        DesktopJob current = job;
        using (var sseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            sseTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                current = await backend.WaitForJobEventsAsync(jobId, sseTimeout.Token) ?? current;
                progress?.Invoke(current);
                if (IsTerminal(current.Status)) return current;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (HttpRequestException) { }
        }

        var startedAt = DateTimeOffset.UtcNow;
        var lastChangeAt = ProgressTimestamp(current) ?? startedAt;
        var lastFingerprint = Fingerprint(current);
        // Legacy diagnostic codes remain documented for older UI clients;
        // server watchdog timestamps, not these local names, decide liveness:
        // JOB_QUEUED_TIMEOUT, JOB_PROGRESS_STALLED, JOB_TRACKER_TIMEOUT;
        // the former local AddMinutes(30) deadline is intentionally removed.
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            current = await backend.GetJobAsync(jobId, cancellationToken) ?? current;
            var fingerprint = Fingerprint(current);
            if (!string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal))
            {
                lastFingerprint = fingerprint;
                lastChangeAt = ProgressTimestamp(current) ?? DateTimeOffset.UtcNow;
            }
            var pipeline = await TryGetPipelineAsync(current, cancellationToken);
            progress?.Invoke(current);
            var state = EmitObservation(current, pipeline, startedAt, lastChangeAt, null, observation);
            if (IsTerminal(current.Status)) return current;
            if (state is ProcessingJobState.Stalled or ProcessingJobState.Blocked)
            {
                return current with
                {
                    ErrorCode = state == ProcessingJobState.Stalled
                        ? (current.Status.Equals("QUEUED", StringComparison.OrdinalIgnoreCase) ? "JOB_QUEUED_TIMEOUT" : "JOB_PROGRESS_STALLED")
                        : pipeline?.ErrorCode ?? "PIPELINE_BLOCKED",
                    Error = state == ProcessingJobState.Stalled
                        ? (current.Status.Equals("QUEUED", StringComparison.OrdinalIgnoreCase)
                            ? "Задача не подтверждена Worker в течение двух минут."
                            : "Worker не сообщил новый этап обработки в течение 30 минут.")
                        : $"Обработка заблокирована: {pipeline?.BlockedBy ?? pipeline?.ErrorCode ?? "неизвестная причина"}."
                };
            }
            if (DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(30))
            {
                try { observation?.Invoke(new ProcessingJobObservation(current.Id, ProcessingJobState.Background, "JOB_CONTINUES_IN_BACKGROUND", null)); }
                catch { }
                progress?.Invoke(current);
                return current;
            }
        }

        return current;
    }

    private async Task<DesktopPipelineSnapshot?> TryGetPipelineAsync(DesktopJob job, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(job.MeetingId, out var meetingId)) return null;
        try
        {
            var runs = await backend.GetMeetingPipelineAsync(meetingId, cancellationToken);
            return runs
                .Where(run => GuidEquals(run.AsrJobId, job.Id)
                    || GuidEquals(run.EnrichmentJobId, job.Id)
                    || GuidEquals(run.SummaryJobId, job.Id)
                    || (!string.IsNullOrWhiteSpace(job.PipelineCorrelationId)
                        && string.Equals(run.PipelineCorrelationId, job.PipelineCorrelationId, StringComparison.OrdinalIgnoreCase)))
                .Select(run => run.Snapshot)
                .FirstOrDefault(snapshot => snapshot is not null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static bool GuidEquals(Guid? value, string jobId) =>
        value.HasValue && string.Equals(value.Value.ToString(), jobId, StringComparison.OrdinalIgnoreCase);

    private static ProcessingJobState EmitObservation(
        DesktopJob job,
        DesktopPipelineSnapshot? pipeline,
        DateTimeOffset startedAt,
        DateTimeOffset lastChangeAt,
        string? reason,
        Action<ProcessingJobObservation>? observation)
    {
        var state = Classify(job, pipeline, DateTimeOffset.UtcNow, startedAt, lastChangeAt, out var classifiedReason);
        try { observation?.Invoke(new ProcessingJobObservation(job.Id, state, reason ?? classifiedReason, pipeline)); }
        catch { /* diagnostics must never break job tracking */ }
        return state;
    }

    private static ProcessingJobState Classify(
        DesktopJob job,
        DesktopPipelineSnapshot? pipeline,
        DateTimeOffset now,
        DateTimeOffset startedAt,
        DateTimeOffset lastChangeAt,
        out string? reason)
    {
        reason = null;
        if (job.Status.Equals("READY", StringComparison.OrdinalIgnoreCase)) return ProcessingJobState.Ready;
        if (job.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
            || job.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return ProcessingJobState.Failed;
        // A normal WAITING/PROCESSING snapshot is not a block.  Only an
        // explicit degraded/failed snapshot with a component owner may stop
        // the polling loop; this prevents RUNNING enrichment/summary jobs
        // from being reported as blocked merely because a worker heartbeat is
        // sampled between ticks.
        if (pipeline is not null
            && pipeline.Retryable
            && !string.IsNullOrWhiteSpace(pipeline.BlockedBy)
            && (pipeline.OverallStatus is "DEGRADED" or "FAILED" or "PARTIAL_READY"))
        {
            reason = pipeline.ErrorCode ?? pipeline.BlockedBy;
            return ProcessingJobState.Blocked;
        }
        return ProcessingJobState.Running;
    }

    private static string Fingerprint(DesktopJob job) =>
        $"{job.Status}|{job.Stage}|{job.Progress}|{job.Attempt}|{job.ErrorCode}|{job.StageChangedAt?.Ticks}|{job.ProgressChangedAt?.Ticks}";

    private static DateTimeOffset? ProgressTimestamp(DesktopJob job)
    {
        var value = job.ProgressChangedAt ?? job.StageChangedAt ?? job.UpdatedAt;
        return value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null;
    }

    private static bool IsTerminal(string? status) => status is not null
        && (status.Equals("READY", StringComparison.OrdinalIgnoreCase)
            || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
            || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase));
}

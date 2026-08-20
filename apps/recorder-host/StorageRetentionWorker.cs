using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Recorder.Host;

public sealed record StorageRetentionSnapshot(
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    int LastArmedPlayableSessions,
    int LastTransportCandidates,
    long LastRawBytesReclaimed,
    long LastTransportBytesReclaimed,
    long LastPlayableBytesReclaimed,
    long LastTemporaryBytesReclaimed,
    long TotalBytesReclaimed,
    long FailedRuns,
    long LastArchiveBytesReclaimed = 0,
    long LastRetentionCandidates = 0);

public sealed class StorageRetentionMetrics
{
    private readonly object _gate = new();
    private StorageRetentionSnapshot _snapshot = new(null, null, 0, 0, 0, 0, 0, 0, 0, 0);

    public StorageRetentionSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void RecordSuccess(
        DateTimeOffset runAt,
        int armed,
        int candidates,
        long raw,
        long transport,
        long playable,
        long temporary,
        long archive,
        long retentionCandidates)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastRunAtUtc = runAt,
                LastSuccessAtUtc = runAt,
                LastArmedPlayableSessions = armed,
                LastTransportCandidates = candidates,
                LastRawBytesReclaimed = raw,
                LastTransportBytesReclaimed = transport,
                LastPlayableBytesReclaimed = playable,
                LastTemporaryBytesReclaimed = temporary,
                LastArchiveBytesReclaimed = archive,
                LastRetentionCandidates = retentionCandidates,
                TotalBytesReclaimed = _snapshot.TotalBytesReclaimed + raw + transport + playable + temporary + archive
            };
        }
    }

    public void RecordStepFailure(DateTimeOffset runAt)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastRunAtUtc = runAt,
                FailedRuns = _snapshot.FailedRuns + 1
            };
        }
    }

    public void RecordFailure(DateTimeOffset runAt)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastRunAtUtc = runAt,
                FailedRuns = _snapshot.FailedRuns + 1
            };
        }
    }
}

/// <summary>
/// Canonical retention owner for the AudioGraph Recorder Host. The legacy
/// Recorder Service keeps its compatibility loop, but new cleanup is driven
/// here so the installed capture runtime cannot silently depend on that path.
/// SQLite remains the source of truth; directory scans are limited to known
/// generated part files belonging to known session directories.
/// </summary>
public sealed class StorageRetentionWorker(
    SpoolStore spool,
    StorageRetentionMetrics metrics,
    ILogger<StorageRetentionWorker> logger) : BackgroundService
{
    private static TimeSpan ReadInterval()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("WHISPERX_RETENTION_INTERVAL_SECONDS"), out var seconds))
            seconds = 300;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 30, 3600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ReadInterval();
        while (!stoppingToken.IsCancellationRequested)
        {
            var runAt = DateTimeOffset.UtcNow;
            try
            {
                await spool.InitializeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                metrics.RecordFailure(runAt);
                logger.LogWarning(ex, "Storage retention pass could not initialize the spool; source audio and pending delivery were preserved.");
                try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                continue;
            }

            var armed = 0;
            var raw = 0L;
            var archive = 0L;
            var playable = 0L;
            var temporary = 0L;
            IReadOnlyList<RetentionCandidate> transportCandidates = Array.Empty<RetentionCandidate>();
            var transportCandidatesCount = 0;
            var retentionCandidates = 0L;
            async Task<T> RunStepAsync<T>(string name, Func<Task<T>> operation, T fallback)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    metrics.RecordStepFailure(runAt);
                    logger.LogWarning(ex, "Storage retention step {Step} failed; continuing with the remaining cleanup steps.", name);
                    return fallback;
                }
            }

            try
            {
                armed = await RunStepAsync("arm-playable", () => spool.ArmPlayableAudioRetentionAsync(runAt, stoppingToken), 0).ConfigureAwait(false);
                raw = await RunStepAsync("raw-pcm", () => spool.PurgeEligibleRawRecoveryAsync(stoppingToken), 0L).ConfigureAwait(false);
                transportCandidates = await RunStepAsync("transport-candidates", () => spool.GetTransportPurgeCandidatesAsync(runAt, stoppingToken), (IReadOnlyList<RetentionCandidate>)Array.Empty<RetentionCandidate>()).ConfigureAwait(false);
                transportCandidatesCount = transportCandidates.Count;
                retentionCandidates += transportCandidatesCount;
                long transport = 0;
                foreach (var candidate in transportCandidates)
                {
                    var reclaimed = await RunStepAsync($"transport:{candidate.SessionId}", () => spool.PurgeFinalizedSessionAsync(candidate.SessionId, stoppingToken), 0L).ConfigureAwait(false);
                    transport += reclaimed;
                }
                archive = await RunStepAsync("local-archive", () => spool.PurgeEligibleLocalArchivesAsync(stoppingToken), 0L).ConfigureAwait(false);
                playable = await RunStepAsync("playable-audio", () => spool.PurgeEligiblePlayableAudioAsync(runAt, stoppingToken), 0L).ConfigureAwait(false);
                temporary = await RunStepAsync("generated-parts", () => spool.CleanupStaleGeneratedPartsAsync(runAt, stoppingToken), 0L).ConfigureAwait(false);
                retentionCandidates += armed;
                metrics.RecordSuccess(runAt, armed, transportCandidatesCount, raw, transport, playable, temporary, archive, retentionCandidates);
                if (raw > 0 || transport > 0 || playable > 0 || archive > 0 || temporary > 0)
                    logger.LogInformation("Storage retention reclaimed {RawBytes} raw, {TransportBytes} transport, {PlayableBytes} playable, {ArchiveBytes} archive and {TemporaryBytes} temporary bytes.", raw, transport, playable, archive, temporary);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                metrics.RecordFailure(runAt);
                logger.LogWarning(ex, "Storage retention pass failed; source audio and pending delivery were preserved.");
            }

            try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

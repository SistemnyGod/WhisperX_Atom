using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Single-flight background WAV builder. It never participates in capture and
/// never makes STOP wait for disk encoding or the network.
/// </summary>
public sealed class PlayableAudioWorker(
    SpoolStore spool,
    LocalPlayableAudioWriter writer,
    PlayableAudioWakeSignal wake,
    ILogger<PlayableAudioWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await spool.WaitUntilInitializedAsync(stoppingToken).ConfigureAwait(false);
        var verifyReadyHashes = true;
        var readyValidationDueAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var includeReady = now >= readyValidationDueAt;
                foreach (var sessionId in await spool.SessionsNeedingPlayableAudioAsync(includeReady, stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        var info = await spool.GetSessionInfoAsync(sessionId, stoppingToken).ConfigureAwait(false);
                        if (info?.PlayableAudioState == "READY")
                        {
                            if (await writer.ValidateReadyAsync(sessionId, verifyReadyHashes, stoppingToken).ConfigureAwait(false))
                                continue;
                            await spool.SetPlayableAudioStateAsync(sessionId, "RECOVERY_PENDING", error: "PLAYABLE_FILE_INVALID", nextRetryAtUtc: DateTimeOffset.UtcNow, clearNextRetry: false, cancellationToken: stoppingToken).ConfigureAwait(false);
                        }
                        await writer.BuildAsync(sessionId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogWarning(ex, "Playable audio build failed. Session={SessionId}", sessionId); }
                }
                verifyReadyHashes = false;
                if (includeReady)
                    readyValidationDueAt = DateTimeOffset.UtcNow.AddMinutes(1);
                await wake.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Playable audio worker iteration failed; SQLite remains authoritative.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}

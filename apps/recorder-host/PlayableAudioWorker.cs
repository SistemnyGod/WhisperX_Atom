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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var sessionId in await spool.SessionsNeedingPlayableAudioAsync(stoppingToken).ConfigureAwait(false))
                {
                    try { await writer.BuildAsync(sessionId, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogWarning(ex, "Playable audio build failed. Session={SessionId}", sessionId); }
                }
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

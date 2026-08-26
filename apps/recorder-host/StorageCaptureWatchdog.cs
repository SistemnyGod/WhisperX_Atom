using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Low-disk protection for the current-user Recorder Host. It never deletes
/// data and delegates the actual durable boundary to RecorderHostRuntime's
/// normal STOP path. The session key makes the emergency action exactly once.
/// </summary>
public sealed class StorageCaptureWatchdog(
    RecorderHostRuntime runtime,
    ILogger<StorageCaptureWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var healthResponse = await runtime.HealthAsync(stoppingToken).ConfigureAwait(false);
                var health = healthResponse.Health;
                if (health is null || string.IsNullOrWhiteSpace(health.ActiveSessionId))
                    continue;

                var policy = StorageRetentionPolicy.FromEnvironment();
                var watermark = policy.EvaluateDuringRecording(health.FreeBytes, health.TotalBytes);
                if (!watermark.IsEmergency)
                    continue;

                logger.LogError("Capture storage emergency for session {SessionId}: free={FreeBytes}, threshold={Threshold}",
                    health.ActiveSessionId, health.FreeBytes, watermark.EmergencyStopFreeBytes);
                var stop = await runtime.StopForLowDiskAsync(stoppingToken).ConfigureAwait(false);
                if (!stop.Ok && !string.Equals(stop.Error, "LOW_DISK_STOP_ALREADY_REQUESTED", StringComparison.Ordinal))
                    logger.LogError("Automatic low-disk STOP failed for session {SessionId}: {Error}", health.ActiveSessionId, stop.Error);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Storage capture watchdog iteration failed.");
            }
        }
    }
}

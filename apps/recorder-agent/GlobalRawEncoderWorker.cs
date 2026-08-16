using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Coalesced wake-up for the process-wide raw encoder. SQLite is the source of
/// truth; a dropped wake-up only causes the bounded polling fallback to run.
/// </summary>
public sealed class RawEncoderWakeSignal
{
    private readonly System.Threading.Channels.Channel<bool> _channel =
        System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });

    public void Signal() => _channel.Writer.TryWrite(true);

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public void Drain()
    {
        while (_channel.Reader.TryRead(out _)) { }
    }
}

/// <summary>
/// The only component allowed to turn durable PCM into FLAC. It is deliberately
/// single-flight so a recovery pass can never race a live session encoder.
/// </summary>
public sealed class GlobalRawEncoderWorker(
    SpoolStore spool,
    RawEncoderWakeSignal wake,
    ILogger<GlobalRawEncoderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await spool.WaitUntilInitializedAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!RecorderRuntimeActivity.IsActive)
                    {
                        await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await spool.RecoverExpiredRawEncodingLeasesAsync(stoppingToken).ConfigureAwait(false);
                    var raw = await spool.ClaimNextRawChunkForEncodingAsync(
                        sessionId: null,
                        workerId: _workerId,
                        leaseDuration: LeaseDuration,
                        cancellationToken: stoppingToken).ConfigureAwait(false);
                    if (raw is not null)
                    {
                        await EncodeOneAsync(raw, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await Task.WhenAny(
                            wake.WaitToReadAsync(stoppingToken).AsTask(),
                            Task.Delay(PollInterval, stoppingToken)).ConfigureAwait(false);
                        wake.Drain();
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // A transient SQLite lock or a temporarily unavailable
                    // encoder must not permanently kill the singleton worker.
                    // The raw row remains durable and will be claimed again.
                    logger.LogWarning(ex, "Global raw encoder iteration failed; retrying.");
                    try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Global raw encoder stopped unexpectedly. WorkerId={WorkerId}", _workerId);
        }
    }

    private async Task EncodeOneAsync(RawRecordingChunk raw, CancellationToken cancellationToken)
    {
        var outputPart = $"{raw.OutputPath}.{_workerId.GetHashCode():x8}.part";
        using var leaseStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewal = RenewLeaseAsync(raw, leaseStop.Token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(raw.OutputPath)!);
            if (File.Exists(raw.OutputPath) && await FlacEncoder.ValidateAsync(RecorderToolPaths.Ffprobe(), raw.OutputPath, raw, cancellationToken).ConfigureAwait(false))
            {
                await spool.CompleteRawEncodingAsync(raw, raw.OutputPath, _workerId, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (File.Exists(outputPart)) File.Delete(outputPart);
            FlacEncoder.Encode(RecorderToolPaths.Ffmpeg(), raw.RawPath, outputPart, FlacEncoder.RawFormat(raw));
            if (!await FlacEncoder.ValidateAsync(RecorderToolPaths.Ffprobe(), outputPart, raw, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("ffprobe_invalid_audio");
            if (!await spool.OwnsRawEncodingLeaseAsync(raw, _workerId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("RAW_ENCODER_LEASE_LOST");
            File.Move(outputPart, raw.OutputPath, true);
            await spool.CompleteRawEncodingAsync(raw, raw.OutputPath, _workerId, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Raw chunk encoded. Session={SessionId}, Track={TrackId}, Sequence={Sequence}", raw.SessionId, raw.TrackId, raw.Sequence);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(outputPart)) File.Delete(outputPart); } catch (IOException) { }
            var code = ex.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase)
                ? "LOCAL_ENCODER_UNAVAILABLE"
                : "ENCODER_FAILED";
            await spool.SetRawEncodingFailureAsync(raw, code, _workerId, cancellationToken).ConfigureAwait(false);
            logger.LogWarning(ex, "Raw chunk encoding failed; PCM remains recoverable. Session={SessionId}, Sequence={Sequence}, ErrorCode={ErrorCode}", raw.SessionId, raw.Sequence, code);
        }
        finally
        {
            leaseStop.Cancel();
            try { await leaseRenewal.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    private async Task RenewLeaseAsync(RawRecordingChunk raw, CancellationToken cancellationToken)
    {
        // Keep a 120-second claim alive while a slow ffmpeg/ffprobe invocation
        // is running.  A failed renewal is intentionally not converted into a
        // process-wide failure: the owner check on completion makes the chunk
        // recoverable by the next worker.
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await spool.RenewRawEncodingLeaseAsync(raw, _workerId, LeaseDuration, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogWarning("Raw encoder lease was lost. Session={SessionId}, Sequence={Sequence}", raw.SessionId, raw.Sequence);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}

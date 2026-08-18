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

    public async ValueTask<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await _channel.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout is the normal bounded polling path. The cancelled
            // waiter is disposed here, so repeated idle polls cannot leave
            // pending channel readers behind.
            return false;
        }
    }

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
    RawEncoderRuntimeState runtimeState,
    ILogger<GlobalRawEncoderWorker> logger) : BackgroundService
{
    private const int MaxTransientEncodeAttempts = 5;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            runtimeState.Mark("STARTING");
            await spool.WaitUntilInitializedAsync(stoppingToken).ConfigureAwait(false);
            runtimeState.Mark("READY");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!RecorderRuntimeActivity.IsActive)
                    {
                        runtimeState.Mark("STOPPED");
                        await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await spool.RecoverExpiredRawEncodingLeasesAsync(stoppingToken).ConfigureAwait(false);
                    var backlog = await spool.GetRawChunkBacklogAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                    if (backlog.TerminalFailed > 0 && backlog.Pending == backlog.TerminalFailed)
                    {
                        runtimeState.Mark("DEGRADED", errorCode: "REQUIRES_MANUAL_REPAIR", queueDepth: backlog.Pending);
                    }
                    else
                    {
                        runtimeState.Mark("READY", queueDepth: backlog.Pending);
                    }
                    var raw = await spool.ClaimNextRawChunkForEncodingAsync(
                        sessionId: null,
                        workerId: _workerId,
                        leaseDuration: LeaseDuration,
                        cancellationToken: stoppingToken).ConfigureAwait(false);
                    if (raw is not null)
                    {
                        runtimeState.Mark("BUSY", raw.Id, queueDepth: Math.Max(0, backlog.Pending - 1));
                        await EncodeOneAsync(raw, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        if (await wake.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false))
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
                    runtimeState.Mark("DEGRADED", errorCode: ClassifyRuntimeError(ex));
                    try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Global raw encoder stopped unexpectedly. WorkerId={WorkerId}", _workerId);
            runtimeState.Mark("FAILED", errorCode: ClassifyRuntimeError(ex));
        }
        finally
        {
            runtimeState.Mark("STOPPED");
        }
    }

    private async Task EncodeOneAsync(RawRecordingChunk raw, CancellationToken cancellationToken)
    {
        var outputPart = $"{raw.OutputPath}.{_workerId.GetHashCode():x8}.part";
        using var leaseStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewal = RenewLeaseAsync(raw, leaseStop.Token);
        var runtimeHeartbeat = PublishRuntimeHeartbeatAsync(raw, leaseStop.Token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(raw.OutputPath)!);
            if (File.Exists(raw.OutputPath) && await FlacEncoder.ValidateAsync(RecorderToolPaths.Ffprobe(), raw.OutputPath, raw, cancellationToken).ConfigureAwait(false))
            {
                await spool.CompleteRawEncodingAsync(raw, raw.OutputPath, _workerId, cancellationToken).ConfigureAwait(false);
                runtimeState.MarkSuccess(raw.Id);
                return;
            }

            ValidateRawSource(raw);

            if (File.Exists(outputPart)) File.Delete(outputPart);
            await FlacEncoder.EncodeAsync(RecorderToolPaths.Ffmpeg(), raw.RawPath, outputPart, FlacEncoder.RawFormat(raw), cancellationToken).ConfigureAwait(false);
            if (!await FlacEncoder.ValidateAsync(RecorderToolPaths.Ffprobe(), outputPart, raw, cancellationToken).ConfigureAwait(false))
                throw new RawEncoderValidationException("FLAC_VALIDATION_FAILED");
            if (!await spool.OwnsRawEncodingLeaseAsync(raw, _workerId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("RAW_ENCODER_LEASE_LOST");
            File.Move(outputPart, raw.OutputPath, true);
            await spool.CompleteRawEncodingAsync(raw, raw.OutputPath, _workerId, cancellationToken).ConfigureAwait(false);
            runtimeState.MarkSuccess(raw.Id);
            logger.LogInformation("Raw chunk encoded. Session={SessionId}, Track={TrackId}, Sequence={Sequence}", raw.SessionId, raw.TrackId, raw.Sequence);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(outputPart)) File.Delete(outputPart); } catch (IOException) { }
            var terminalCode = ex is RawEncoderTerminalException terminal ? terminal.Code : null;
            // A malformed derived FLAC is retryable while the encoder/ffprobe
            // may still be racing a file flush. Only repeated validation
            // failures become terminal; the source PCM is always retained.
            if (ex is RawEncoderValidationException && raw.EncodeAttempts < 3)
                terminalCode = null;
            var code = terminalCode ?? ClassifyEncodeFailure(ex);
            if (terminalCode is null
                && code is not "LOCAL_ENCODER_UNAVAILABLE"
                && raw.EncodeAttempts >= MaxTransientEncodeAttempts)
            {
                terminalCode = "ENCODER_RETRY_EXHAUSTED";
            }
            code = terminalCode ?? code;
            if (terminalCode is not null)
            {
                var terminalClaimed = await spool.SetRawEncodingTerminalFailureAsync(raw, code, _workerId, cancellationToken).ConfigureAwait(false);
                if (terminalClaimed)
                {
                    await spool.SetSessionStateAsync(raw.SessionId, "FAILED", cancellationToken).ConfigureAwait(false);
                    await spool.SetFinalizationStateAsync(raw.SessionId,
                        localFinalizeState: "LOCAL_FAILED",
                        deliveryState: "NOT_REQUESTED",
                        errorCode: code,
                        errorDetail: "Raw encoder encountered a terminal source error; manual repair is required.",
                        clearNextRetry: true,
                        preserveError: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await spool.SetRawEncodingFailureAsync(raw, code, _workerId, cancellationToken).ConfigureAwait(false);
            }
            runtimeState.Mark(terminalCode is null && code == "LOCAL_ENCODER_UNAVAILABLE" ? "WAITING_FOR_FFMPEG" : "DEGRADED", raw.Id, code);
            logger.LogWarning(ex, "Raw chunk encoding failed; PCM remains recoverable. Session={SessionId}, Sequence={Sequence}, ErrorCode={ErrorCode}", raw.SessionId, raw.Sequence, code);
        }
        finally
        {
            leaseStop.Cancel();
            try { await leaseRenewal.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "Raw encoder lease renewal stopped after encode result was determined."); }
            try { await runtimeHeartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "Raw encoder health heartbeat stopped after encode result was determined."); }
        }
    }

    private async Task PublishRuntimeHeartbeatAsync(RawRecordingChunk raw, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                runtimeState.Mark("BUSY", raw.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void ValidateRawSource(RawRecordingChunk raw)
    {
        if (!File.Exists(raw.RawPath)) throw new RawEncoderTerminalException("RAW_SOURCE_MISSING");
        var size = new FileInfo(raw.RawPath).Length;
        if (size <= 0) throw new RawEncoderTerminalException("RAW_SOURCE_EMPTY");
        var blockAlign = Math.Max(1, raw.Channels * Math.Max(1, raw.BitsPerSample / 8));
        if (raw.SampleRate <= 0 || raw.SampleCount <= 0 || size % blockAlign != 0 || size != raw.SampleCount * blockAlign)
            throw new RawEncoderTerminalException("RAW_TIMELINE_INVALID");
        if (!string.IsNullOrWhiteSpace(raw.RawSha256))
        {
            using var stream = File.OpenRead(raw.RawPath);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, raw.RawSha256, StringComparison.OrdinalIgnoreCase))
                throw new RawEncoderTerminalException("RAW_CHECKSUM_MISMATCH");
        }
        if (raw.BitsPerSample is not (16 or 24 or 32))
            throw new RawEncoderTerminalException("UNSUPPORTED_AUDIO_FORMAT");
    }

    private static string ClassifyEncodeFailure(Exception exception)
    {
        if (exception is ExternalProcessTimeoutException timeout)
            return timeout.Code;
        if (exception is System.ComponentModel.Win32Exception
            || exception.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("start_failed", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("ffmpeg_not_found", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("ffprobe_not_found", StringComparison.OrdinalIgnoreCase))
            return "LOCAL_ENCODER_UNAVAILABLE";
        if (exception is RawEncoderValidationException)
            return "FLAC_VALIDATION_FAILED";
        if (exception.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return "FFMPEG_ENCODE_FAILED";
        if (exception.Message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase))
            return "FLAC_VALIDATION_FAILED";
        return "ENCODER_FAILED";
    }

    private static string ClassifyRuntimeError(Exception ex)
        => ex.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase)
            ? "LOCAL_ENCODER_UNAVAILABLE"
            : "ENCODER_FAILED";

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

internal class RawEncoderTerminalException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

internal sealed class RawEncoderValidationException(string code) : RawEncoderTerminalException(code);

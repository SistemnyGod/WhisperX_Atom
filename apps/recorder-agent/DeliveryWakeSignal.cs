using System.Threading.Channels;

namespace WhisperX.Atom.Recorder;

/// <summary>Coalesced wake-up; SQLite polling remains the recovery mechanism.</summary>
public class DeliveryWakeSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });

    public void Signal() => _channel.Writer.TryWrite(true);

    /// <summary>
    /// Waits for a signal and returns false for the bounded polling timeout.
    /// The result lets the canonical Recorder interrupt its command-stream
    /// poll only when delivery really became due.
    /// </summary>
    public async ValueTask<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var signaled = await _channel.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false);
            while (_channel.Reader.TryRead(out _)) { }
            return signaled;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
}

/// <summary>Separate coalesced wake-up for the local playable-file worker.</summary>
public sealed class PlayableAudioWakeSignal : DeliveryWakeSignal { }

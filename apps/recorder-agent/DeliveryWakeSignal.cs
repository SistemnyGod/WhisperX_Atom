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

    public async ValueTask WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try { await _channel.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        while (_channel.Reader.TryRead(out _)) { }
    }
}

/// <summary>Separate coalesced wake-up for the local playable-file worker.</summary>
public sealed class PlayableAudioWakeSignal : DeliveryWakeSignal { }

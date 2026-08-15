using System.Threading.Channels;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Neutral frame consumer shared by AudioGraph Host and future capture adapters.
/// The capture callback only enqueues; durable IO and chunk rotation happen on
/// this worker side. RECORDING may be acknowledged only after the callback has
/// completed once successfully.
/// </summary>
public sealed class AudioFrameDurableConsumer
{
    private readonly TaskCompletionSource<bool> _firstDurableWrite =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstDurableWrite => _firstDurableWrite.Task;

    public async Task RunAsync(
        ChannelReader<AudioFrame> frames,
        Func<AudioFrame, Task> durableWrite,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await durableWrite(frame).ConfigureAwait(false);
                    _firstDurableWrite.TrySetResult(true);
                }
                finally
                {
                    // AudioGraph may transfer an ArrayPool lease with the
                    // frame. Returning it here also covers writer failures.
                    frame.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _firstDurableWrite.TrySetException(ex);
            throw;
        }
    }
}

using System.Threading.Channels;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Small Host-only extension of the platform-neutral capture contract.
/// Recorder Host uses it to bind a durable consumer before a source starts
/// emitting frames. Each source owns its own channel and sample clock.
/// </summary>
internal interface IHostCaptureSource
{
    ChannelReader<AudioFrame> PrepareFrameChannel();
    void MarkFrameConsumed(int sampleCount);
    long CurrentMediaTimeMs { get; }
    Task StopAsync(CancellationToken cancellationToken = default);
}

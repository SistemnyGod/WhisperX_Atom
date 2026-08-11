using System.Diagnostics;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Provides one monotonic sample timeline for every capture track in a session.
/// Capture devices can be opened a few milliseconds apart, so each track gets
/// an explicit offset instead of silently starting at sample zero.
/// </summary>
public sealed class RecordingSessionClock
{
    private readonly long _startedAt = Stopwatch.GetTimestamp();

    public long GetStartSample(int sampleRate)
    {
        if (sampleRate <= 0) return 0;

        var elapsedTicks = Stopwatch.GetTimestamp() - _startedAt;
        return Math.Max(0, (long)Math.Round(elapsedTicks * sampleRate / (double)Stopwatch.Frequency));
    }
}

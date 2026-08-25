using WhisperX.Atom.Recorder;
using Xunit;

namespace WhisperX.Atom.Recorder.Tests;

public sealed class RecorderTelemetrySelectorTests
{
    [Fact]
    public void FreshLiveSnapshotSuppliesRmsPeakAndDurableMetadataAsOneView()
    {
        var capture = new AudioTelemetrySnapshot(
            10, 100, null, -24, -6, false,
            DateTimeOffset.UtcNow.AddSeconds(-5), 9000);
        var capturedAt = DateTimeOffset.UtcNow;
        var live = new LiveAudioTelemetrySnapshot(2, 100, 0.25, 0.8, true, capturedAt, false, 350);

        var selected = RecorderTelemetrySelector.Select(capture, live);

        Assert.True(selected.UsesFreshLiveTelemetry);
        Assert.Equal(live.RmsDb, selected.Snapshot.RmsDb);
        Assert.Equal(live.PeakDb, selected.Snapshot.PeakDb);
        Assert.True(selected.Snapshot.Clipping);
        Assert.Equal(capturedAt, selected.Snapshot.LastAudioAtUtc);
        Assert.Equal(350, selected.Snapshot.SilenceDurationMs);
    }

    [Fact]
    public void StaleLiveSnapshotKeepsCumulativeCaptureTelemetry()
    {
        var capture = new AudioTelemetrySnapshot(
            10, 100, null, -24, -6, false,
            DateTimeOffset.UtcNow.AddSeconds(-5), 9000);
        var live = new LiveAudioTelemetrySnapshot(2, 100, 0.25, 0.8, true, DateTimeOffset.UtcNow, true, 350);

        var selected = RecorderTelemetrySelector.Select(capture, live);

        Assert.False(selected.UsesFreshLiveTelemetry);
        Assert.Equal(capture.RmsDb, selected.Snapshot.RmsDb);
        Assert.Equal(capture.PeakDb, selected.Snapshot.PeakDb);
        Assert.Equal(capture.LastAudioAtUtc, selected.Snapshot.LastAudioAtUtc);
        Assert.Equal(capture.SilenceDurationMs, selected.Snapshot.SilenceDurationMs);
    }
}

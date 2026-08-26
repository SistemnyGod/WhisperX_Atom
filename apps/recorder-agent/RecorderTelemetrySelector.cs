namespace WhisperX.Atom.Recorder;

/// <summary>
/// Chooses one coherent telemetry snapshot for the Recorder health contract.
/// A live window is preferred only while it is fresh; all values then come
/// from that same window instead of mixing live RMS with cumulative timestamps.
/// </summary>
public sealed record RecorderTelemetrySelection(
    AudioTelemetrySnapshot Snapshot,
    bool UsesFreshLiveTelemetry);

public static class RecorderTelemetrySelector
{
    public static RecorderTelemetrySelection Select(
        AudioTelemetrySnapshot capture,
        LiveAudioTelemetrySnapshot? live)
    {
        if (live is null || live.IsStale)
            return new RecorderTelemetrySelection(capture, false);

        return new RecorderTelemetrySelection(
            capture with
            {
                RmsDb = live.RmsDb,
                PeakDb = live.PeakDb,
                Clipping = live.Clipping,
                LastAudioAtUtc = live.CapturedAtUtc,
                SilenceDurationMs = live.SilenceDurationMs,
                IsStale = false
            },
            true);
    }
}

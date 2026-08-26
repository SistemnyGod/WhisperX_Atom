namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Production probe boundary for the AudioGraph engine. Diagnostics must call
/// this adapter through the same engine used by recording; it must not create a
/// second capture implementation just for a script.
/// </summary>
public sealed class AudioGraphDeviceProbe : IAudioDeviceProbe
{
    public AudioEngineKind EngineKind => AudioEngineKind.AudioGraph;

    public Task<AudioDeviceProbeResult> ProbeAsync(
        IAudioCaptureEngine engine,
        AudioSelectionMode selectionMode,
        string? deviceId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (engine.Kind != EngineKind)
            throw new InvalidOperationException("AUDIO_PROBE_ENGINE_MISMATCH");
        return engine.ProbeAsync(selectionMode, deviceId, duration, cancellationToken);
    }
}

public sealed class AudioGraphCaptureEngineFactory(AudioGraphDeviceCatalog catalog) : IAudioCaptureEngineFactory
{
    public IAudioCaptureEngine Create(string profile)
        => new AudioGraphCaptureEngine(catalog);
}

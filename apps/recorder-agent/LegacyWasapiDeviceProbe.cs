namespace WhisperX.Atom.Recorder;

/// <summary>Probe boundary for the isolated legacy Windows adapter.</summary>
public sealed class LegacyWasapiDeviceProbe : IAudioDeviceProbe
{
    public AudioEngineKind EngineKind => AudioEngineKind.LegacyWasapi;

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

public sealed class LegacyWasapiCaptureEngineFactory : IAudioCaptureEngineFactory
{
    public IAudioCaptureEngine Create(string profile)
        => new LegacyWasapiCaptureEngine(new LegacyWasapiDeviceCatalog());
}

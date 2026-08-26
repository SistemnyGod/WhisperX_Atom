namespace WhisperX_Atom_Desktop.ViewModels;

/// <summary>
/// Static facts supplied by the Voice Host status pipe. These values never come
/// from the high-frequency audio stream, so device identities cannot flicker
/// between an endpoint id and a friendly name.
/// </summary>
internal sealed record VoiceDiagnosticsUiState(
    string Status,
    string LastRecognition,
    string ErrorCode,
    string RequestedDeviceId,
    string EffectiveDeviceId,
    string EffectiveDeviceName,
    string WakeWordMode,
    string RuntimeBuild,
    string RuntimePath,
    string ExpectedBuild,
    string ObservedBuild,
    string RuntimeProcess,
    string LastTraceId,
    string LastCommandId,
    string Detail);

/// <summary>
/// Latest-only audio values. Rendering may interpolate these values, but they
/// must not alter diagnostic text or the layout of the settings page.
/// </summary>
internal sealed record VoiceTelemetryUiState(
    long Sequence,
    DateTimeOffset? AtUtc,
    double Rms,
    double Peak,
    bool Clipping,
    string SignalState)
{
    public static readonly VoiceTelemetryUiState Empty = new(0, null, 0, 0, false, "WAITING");

    public bool IsFresh(DateTimeOffset now) => Sequence > 0
        && AtUtc is DateTimeOffset at
        && now - at <= TimeSpan.FromMilliseconds(750);
}

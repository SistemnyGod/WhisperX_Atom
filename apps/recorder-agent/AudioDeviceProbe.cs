namespace WhisperX.Atom.Recorder;

/// <summary>
/// The capture probe is intentionally separate from endpoint discovery. An
/// Active MMDevice only proves that Windows enumerated an endpoint; this
/// helper defines the stronger evidence required before START is enabled.
/// </summary>
public static class AudioDeviceProbe
{
    public static bool IsReady(AudioSourceTestResult? result)
        => result is
        {
            Success: true,
            StreamOpened: true,
            StreamStarted: true,
            PacketCount: > 0,
            BytesReceived: > 0
        };

    public static bool IsReady(AudioSourceTestResult? result, string? expectedDeviceId)
        => IsReady(result)
            && string.Equals(Normalize(result!.DeviceId), Normalize(expectedDeviceId), StringComparison.OrdinalIgnoreCase);

    public static string State(AudioSourceTestResult? result)
    {
        if (result is null) return "UNKNOWN";
        if (IsReady(result)) return result.SignalDetected ? "READY" : "READY_NO_SIGNAL";
        return result.ErrorCode switch
        {
            "AUDIO_NO_DATA" => "NO_PACKETS",
            "AUDIO_DEVICE_ACCESS_DENIED" => "ACCESS_BLOCKED",
            "AUDIO_FORMAT_UNSUPPORTED" => "OPEN_FAILED",
            "DEVICE_INACTIVE" or "AUDIO_SOURCE_UNAVAILABLE" => "DEVICE_LOST",
            _ => result.CaptureState
        };
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

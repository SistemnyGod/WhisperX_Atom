using System.Text.Json.Serialization;

namespace WhisperX.Atom.Recorder;

public sealed record AudioSelectionConfigurationV2(
    [property: JsonPropertyName("selectionMode")] string SelectionMode,
    [property: JsonPropertyName("deviceId")] string? DeviceId);

public sealed record AudioConfigurationV2(
    [property: JsonPropertyName("audioConfigurationVersion")] int AudioConfigurationVersion,
    [property: JsonPropertyName("captureEngine")] string CaptureEngine,
    [property: JsonPropertyName("microphone")] AudioSelectionConfigurationV2 Microphone,
    [property: JsonPropertyName("systemAudio")] AudioSelectionConfigurationV2 SystemAudio,
    [property: JsonPropertyName("userReselectRequired")] bool UserReselectRequired = false)
{
    public static AudioConfigurationV2 FromCurrent(string? microphoneId, string? systemAudioId, string engine, bool userReselectRequired = false)
        => new(
            2,
            string.IsNullOrWhiteSpace(engine) ? RecorderRuntimeResolver.AudioGraph : engine.Trim().ToUpperInvariant(),
            new(string.IsNullOrWhiteSpace(microphoneId) ? "DEFAULT" : "FIXED", Normalize(microphoneId)),
            new(string.IsNullOrWhiteSpace(systemAudioId) ? "DEFAULT" : "FIXED", Normalize(systemAudioId)),
            userReselectRequired);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

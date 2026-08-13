using System.Text.Json.Serialization;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Host-only neutral heartbeat DTO. The legacy Service keeps its richer
/// NAudio-backed collector in the Service project; the AudioGraph Host sends
/// its live catalog/telemetry through AgentIpcHealth instead.
/// </summary>
public sealed record DeviceHealthSnapshot(
    [property: JsonPropertyName("microphone")] bool Microphone,
    [property: JsonPropertyName("captureDeviceCount")] int CaptureDeviceCount,
    [property: JsonPropertyName("systemAudio")] bool SystemAudio,
    [property: JsonPropertyName("renderDeviceCount")] int RenderDeviceCount,
    [property: JsonPropertyName("freeBytes")] long FreeBytes,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("dataRoot")] string DataRoot,
    [property: JsonPropertyName("error")] string? Error,
    IReadOnlyList<AgentIpcAudioDevice>? CaptureDevices = null,
    IReadOnlyList<AgentIpcAudioDevice>? RenderDevices = null);

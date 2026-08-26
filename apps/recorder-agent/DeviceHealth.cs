using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;

namespace WhisperX.Atom.Recorder;

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
    IReadOnlyList<AgentIpcAudioDevice>? RenderDevices = null,
    [property: JsonPropertyName("activeSessionId")] string? ActiveSessionId = null)
{
    public static DeviceHealthSnapshot Collect(string dataRoot, AgentStorageSettings? storage = null)
    {
        var microphone = false;
        var systemAudio = false;
        var captureCount = 0;
        var renderCount = 0;
        IReadOnlyList<AgentIpcAudioDevice> captureDevices = [];
        IReadOnlyList<AgentIpcAudioDevice> renderDevices = [];
        string? error = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var captureEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var renderEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var captureDefaults = GetDefaultIds(enumerator, DataFlow.Capture);
            var renderDefaults = GetDefaultIds(enumerator, DataFlow.Render);
            captureDevices = captureEndpoints.Select(device => ToDevice(device, DataFlow.Capture, captureDefaults)).ToArray();
            renderDevices = renderEndpoints.Select(device => ToDevice(device, DataFlow.Render, renderDefaults)).ToArray();
            captureCount = captureDevices.Count;
            renderCount = renderDevices.Count;
            microphone = IsSelectedDeviceAvailable(captureDevices, storage?.MicrophoneDeviceId);
            systemAudio = IsSelectedDeviceAvailable(renderDevices, storage?.SystemAudioDeviceId);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            error = "audio_device_probe_failed:" + ex.GetType().Name;
        }

        try
        {
            var fullPath = Path.GetFullPath(dataRoot);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return new DeviceHealthSnapshot(microphone, captureCount, systemAudio, renderCount, 0, 0, fullPath, error ?? "storage_root_unavailable", captureDevices, renderDevices);
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return new DeviceHealthSnapshot(microphone, captureCount, systemAudio, renderCount, 0, 0, fullPath, error ?? "storage_drive_unavailable", captureDevices, renderDevices);
            return new DeviceHealthSnapshot(microphone, captureCount, systemAudio, renderCount, drive.AvailableFreeSpace, drive.TotalSize, fullPath, error, captureDevices, renderDevices);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DeviceHealthSnapshot(microphone, captureCount, systemAudio, renderCount, 0, 0, dataRoot, error ?? "storage_probe_failed:" + ex.GetType().Name, captureDevices, renderDevices);
        }
    }

    private static bool IsSelectedDeviceAvailable(IReadOnlyList<AgentIpcAudioDevice> devices, string? selectedId) =>
        devices.Count > 0 && (string.IsNullOrWhiteSpace(selectedId) || devices.Any(device => string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase)));

    private static AgentIpcAudioDevice ToDevice(MMDevice device, DataFlow flow, DefaultEndpointIds defaults)
    {
        int? sampleRate = null;
        int? channels = null;
        int? bits = null;
        int? validBits = null;
        string? sourceEncoding = null;
        string? sourceSubFormat = null;
        string? normalized = null;
        try
        {
            var format = device.AudioClient.MixFormat;
            var descriptor = AudioSampleFormatResolver.Resolve(format);
            sampleRate = descriptor.SampleRate;
            channels = descriptor.Channels;
            bits = descriptor.BitsPerSample;
            validBits = descriptor.ValidBitsPerSample;
            sourceEncoding = descriptor.SourceEncoding;
            sourceSubFormat = descriptor.SourceSubFormat;
            normalized = descriptor.CanonicalEncoding;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            sourceEncoding = "UNRESOLVED";
        }

        return new AgentIpcAudioDevice(
            device.ID,
            string.IsNullOrWhiteSpace(device.FriendlyName) ? device.ID : device.FriendlyName,
            string.Equals(device.ID, defaults.Multimedia, StringComparison.OrdinalIgnoreCase),
            device.State.ToString(),
            flow.ToString(),
            string.Equals(device.ID, defaults.Console, StringComparison.OrdinalIgnoreCase),
            string.Equals(device.ID, defaults.Multimedia, StringComparison.OrdinalIgnoreCase),
            string.Equals(device.ID, defaults.Communications, StringComparison.OrdinalIgnoreCase),
            sampleRate,
            channels,
            sourceEncoding,
            sourceSubFormat,
            bits,
            validBits,
            normalized,
            DateTimeOffset.UtcNow);
    }

    private static DefaultEndpointIds GetDefaultIds(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        return new(
            TryGetDefaultId(enumerator, flow, Role.Console),
            TryGetDefaultId(enumerator, flow, Role.Multimedia),
            TryGetDefaultId(enumerator, flow, Role.Communications));
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try { return enumerator.GetDefaultAudioEndpoint(flow, role)?.ID; }
        catch (COMException) { return null; }
    }

    private sealed record DefaultEndpointIds(string? Console, string? Multimedia, string? Communications);
}

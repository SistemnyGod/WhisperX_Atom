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
    [property: JsonPropertyName("error")] string? Error)
{
    public static DeviceHealthSnapshot Collect(string dataRoot)
    {
        var microphone = false;
        var systemAudio = false;
        var captureCount = 0;
        var renderCount = 0;
        string? error = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            captureCount = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count;
            renderCount = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Count;
            microphone = captureCount > 0;
            systemAudio = renderCount > 0;
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
                return new DeviceHealthSnapshot(microphone, captureCount, systemAudio, renderCount, 0, 0, fullPath, error ?? "storage_root_unavailable");
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return new DeviceHealthSnapshot(microphone, captureCount, systemAudio, renderCount, 0, 0, fullPath, error ?? "storage_drive_unavailable");
            return new DeviceHealthSnapshot(microphone, captureCount, systemAudio, renderCount, drive.AvailableFreeSpace, drive.TotalSize, fullPath, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DeviceHealthSnapshot(microphone, captureCount, systemAudio, renderCount, 0, 0, dataRoot, error ?? "storage_probe_failed:" + ex.GetType().Name);
        }
    }
}

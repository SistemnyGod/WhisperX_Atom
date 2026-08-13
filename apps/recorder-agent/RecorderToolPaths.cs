namespace WhisperX.Atom.Recorder;

/// <summary>
/// Resolves tools bundled with Recorder independently from the Windows service
/// environment block. SCM may retain a pre-install environment until reboot,
/// while the pinned executables live beside the service binary immediately.
/// </summary>
internal static class RecorderToolPaths
{
    public static string Ffmpeg() => Resolve("ATOM_AGENT_FFMPEG_PATH", "ffmpeg.exe", "ffmpeg");

    public static string Ffprobe() => Resolve("ATOM_AGENT_FFPROBE_PATH", "ffprobe.exe", "ffprobe");

    private static string Resolve(string environmentVariable, string bundledName, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var bundled = Path.Combine(AppContext.BaseDirectory, bundledName);
        return File.Exists(bundled) ? bundled : configured ?? fallback;
    }
}

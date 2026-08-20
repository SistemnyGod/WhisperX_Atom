namespace WhisperX.Atom.Voice.Host.Tts;

internal static class TtsPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "TTS");
    public static string TempRoot => Path.Combine(Root, "Temp");
    public static string StaticCacheRoot => Path.Combine(Root, "StaticCache");
    public static string GetWritableTempRoot()
    {
        foreach (var candidate in new[] { TempRoot, Path.Combine(Path.GetTempPath(), "WhisperXAtom", "TTS", "Temp") })
        {
            try { Directory.CreateDirectory(candidate); var probe = Path.Combine(candidate, ".write-test-" + Guid.NewGuid().ToString("N")); File.WriteAllText(probe, string.Empty); File.Delete(probe); return candidate; }
            catch { }
        }
        throw new UnauthorizedAccessException("TTS_TEMP_UNAVAILABLE");
    }
    public static string CreateTempWavPath(string root) { Directory.CreateDirectory(root); return Path.Combine(root, $"tts-{Guid.NewGuid():N}.wav"); }
    public static void CleanupTemp(TimeSpan maxAge, string? root = null)
    {
        try
        {
            var cleanupRoot = string.IsNullOrWhiteSpace(root) ? TempRoot : Path.GetFullPath(root);
            if (!Directory.Exists(cleanupRoot)) return;
            foreach (var file in Directory.EnumerateFiles(cleanupRoot, "*.wav"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > maxAge) WindowsTtsEngine.TryDelete(file);
        }
        catch { }
    }
}

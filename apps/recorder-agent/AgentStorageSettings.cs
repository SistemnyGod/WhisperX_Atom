namespace WhisperX.Atom.Recorder;

/// <summary>
/// Local, user-visible storage configured by Desktop. The service spool remains in
/// the agent data root; this path is only for the permanent recording archive.
/// </summary>
public sealed class AgentStorageSettings
{
    private readonly object _gate = new();
    private string _archiveRoot;

    public AgentStorageSettings()
    {
        _archiveRoot = Normalize(Environment.GetEnvironmentVariable("ATOM_AGENT_ARCHIVE_ROOT"))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "WhisperX Atom");
    }

    public string ArchiveRoot
    {
        get { lock (_gate) return _archiveRoot; }
    }

    public string SetArchiveRoot(string path)
    {
        var normalized = Normalize(path) ?? throw new ArgumentException("archive_root_required", nameof(path));
        lock (_gate) _archiveRoot = normalized;
        return normalized;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var fullPath = Path.GetFullPath(path.Trim());
        if (File.Exists(fullPath)) throw new IOException("archive_root_is_file");
        Directory.CreateDirectory(fullPath);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

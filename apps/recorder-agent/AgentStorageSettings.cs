namespace WhisperX.Atom.Recorder;

/// <summary>
/// Local, user-visible storage configured by Desktop. The service spool remains in
/// the agent data root; this path is only for the permanent recording archive.
/// </summary>
public sealed class AgentStorageSettings
{
    private readonly object _gate = new();
    private string _archiveRoot;
    private string? _microphoneDeviceId;
    private string? _systemAudioDeviceId;

    public AgentStorageSettings()
    {
        _archiveRoot = Normalize(Environment.GetEnvironmentVariable("ATOM_AGENT_ARCHIVE_ROOT"))
            ?? DefaultArchiveRoot();
        _microphoneDeviceId = NormalizeDeviceId(Environment.GetEnvironmentVariable("ATOM_AGENT_MICROPHONE_DEVICE_ID"));
        _systemAudioDeviceId = NormalizeDeviceId(Environment.GetEnvironmentVariable("ATOM_AGENT_SYSTEM_AUDIO_DEVICE_ID"));
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

    public string? MicrophoneDeviceId
    {
        get { lock (_gate) return _microphoneDeviceId; }
    }

    public string? SystemAudioDeviceId
    {
        get { lock (_gate) return _systemAudioDeviceId; }
    }

    public void SetAudioDevices(string? microphoneDeviceId, string? systemAudioDeviceId)
    {
        lock (_gate)
        {
            _microphoneDeviceId = NormalizeDeviceId(microphoneDeviceId);
            _systemAudioDeviceId = NormalizeDeviceId(systemAudioDeviceId);
        }
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

    private static string DefaultArchiveRoot()
    {
        var commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var publicRoot = Directory.GetParent(commonDocuments)?.FullName;
        return Path.Combine(
            string.IsNullOrWhiteSpace(publicRoot) ? commonDocuments : publicRoot,
            "WhisperX Atom");
    }

    private static string? NormalizeDeviceId(string? deviceId) => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
}

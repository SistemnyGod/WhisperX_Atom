using System.Text;

namespace WhisperX.Atom.Recorder;

/// <summary>Resolves the one durable meeting directory used by archive and playable files.</summary>
public static class LocalMeetingDirectoryResolver
{
    public static string Resolve(AgentStorageSettings storage, RecordingSessionInfo info, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(info.ArchivePath) && Directory.Exists(info.ArchivePath))
            return Path.GetFullPath(info.ArchivePath);

        var started = info.StartedAt ?? DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(info.Title) ? "Запись" : info.Title.Trim();
        var suffix = sessionId[..Math.Min(8, sessionId.Length)];
        var folder = $"{started.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{Sanitize(title)}_{suffix}";
        foreach (var root in new[] { storage.ArchiveRoot, FallbackRoot() }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var meetings = Path.Combine(root, "Meetings");
                Directory.CreateDirectory(meetings);
                var probe = Path.Combine(meetings, $".write-probe-{Guid.NewGuid():N}.part");
                File.WriteAllText(probe, "ok", new UTF8Encoding(false));
                File.Move(probe, probe[..^5]);
                File.Delete(probe[..^5]);
                var directory = Path.Combine(meetings, folder);
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        throw new IOException("PLAYABLE_STORAGE_UNAVAILABLE");
    }

    public static string FallbackRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Archive");

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);
        var sanitized = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Запись" : sanitized[..Math.Min(96, sanitized.Length)];
    }
}

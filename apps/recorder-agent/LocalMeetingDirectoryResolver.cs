using System.Text;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Resolves the user-facing directory for a recording. New archives use a
/// compact year/month hierarchy; the legacy Meetings tree remains readable.
/// </summary>
public static class LocalMeetingDirectoryResolver
{
    private static readonly object ResolveGate = new();

    public static string Resolve(AgentStorageSettings storage, RecordingSessionInfo info, string sessionId)
    {
        lock (ResolveGate)
        {
            if (!string.IsNullOrWhiteSpace(info.ArchivePath) && Directory.Exists(info.ArchivePath))
                return Path.GetFullPath(info.ArchivePath);
            // ReserveAsync stores the destination in the playable-path field
            // until the FLAC manifest is verified. Reuse that reservation so
            // finalization never creates a second meeting directory.
            if (!string.IsNullOrWhiteSpace(info.PlayableAudioPath) && Directory.Exists(info.PlayableAudioPath))
                return Path.GetFullPath(info.PlayableAudioPath);

            var started = info.StartedAt ?? DateTimeOffset.UtcNow;
            var title = string.IsNullOrWhiteSpace(info.Title) ? "Запись" : info.Title.Trim();
            var local = started.ToLocalTime();
            var folder = $"{local:dd.MM.yyyy HH-mm} — {Sanitize(title)}";
            foreach (var root in new[] { storage.ArchiveRoot, FallbackRoot() }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var year = Path.Combine(root, local.ToString("yyyy"));
                    var month = Path.Combine(year, local.ToString("MM"));
                    Directory.CreateDirectory(month);
                    var probe = Path.Combine(month, $".write-probe-{Guid.NewGuid():N}.part");
                    File.WriteAllText(probe, "ok", new UTF8Encoding(false));
                    File.Move(probe, probe[..^5]);
                    File.Delete(probe[..^5]);
                    var directory = Path.Combine(month, folder);
                    if (Directory.Exists(directory))
                    {
                        var baseFolder = directory;
                        for (var suffix = 2; Directory.Exists(directory); suffix++)
                            directory = $"{baseFolder} ({suffix})";
                    }
                    Directory.CreateDirectory(directory);
                    return directory;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw new IOException("PLAYABLE_STORAGE_UNAVAILABLE");
        }
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

using WhisperX.Atom.Recorder;
using Xunit;

public sealed class LocalArchiveLayoutTests
{
    [Fact]
    public void NewSessionsUseYearMonthMeetingFoldersAndCollisionSuffix()
    {
        var root = Path.Combine(Path.GetTempPath(), "WhisperXAtom-tests", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("ATOM_AGENT_ARCHIVE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ATOM_AGENT_ARCHIVE_ROOT", root);
            var storage = new AgentStorageSettings();
            var started = new DateTimeOffset(2026, 8, 25, 9, 1, 0, TimeSpan.Zero);
            var info = new RecordingSessionInfo("session-1", null, "Оперативка", started);

            var first = LocalMeetingDirectoryResolver.Resolve(storage, info, info.SessionId);
            var second = LocalMeetingDirectoryResolver.Resolve(storage, info, "session-2");

            Assert.Contains(Path.Combine(started.ToLocalTime().ToString("yyyy"), started.ToLocalTime().ToString("MM")), first, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(" — Оперативка", first, StringComparison.Ordinal);
            Assert.EndsWith(" (2)", second, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ATOM_AGENT_ARCHIVE_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

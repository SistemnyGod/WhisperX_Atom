using System.Text.Json;

namespace WhisperX.Atom.Voice.Host;

/// <summary>
/// Small local technical ledger for voice lifecycle events. It stores only
/// correlation and diagnostic fields (never audio), so commands and answers
/// remain observable even when no Recorder session exists.
/// </summary>
internal sealed class VoiceLedgerStore
{
    private const long MaximumBytes = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public VoiceLedgerStore(string? root = null)
    {
        root ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperXAtom", "Voice");
        _path = Path.Combine(root, "voice-ledger.jsonl");
    }

    public bool Append(string eventType, object payload)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                eventId = Guid.NewGuid().ToString("N"),
                eventType,
                recordedAtUtc = DateTimeOffset.UtcNow,
                payload
            }, _json) + Environment.NewLine;
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(_path) && new FileInfo(_path).Length + line.Length > MaximumBytes)
                    File.Move(_path, _path + ".previous", true);
                File.AppendAllText(_path, line);
            }
            return true;
        }
        catch
        {
            // Voice telemetry must not block microphone capture or TTS.
            return false;
        }
    }
}

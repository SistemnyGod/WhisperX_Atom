using System.Text.Json;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>
/// Persists only local TUS transfer coordinates.  It never stores an API
/// cookie, media bytes, transcript text or a server token.  Keeping this
/// small record allows an interrupted Desktop upload to resume after restart.
/// </summary>
public sealed class DesktopImportTransferStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperXAtom", "import-transfers.json");

    public DesktopImportTransfer? Find(string path, long sizeBytes, DateTime lastWriteTimeUtc, Uri apiOrigin)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_sync)
        {
            return LoadUnsafe().FirstOrDefault(item =>
                string.Equals(item.LocalPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                item.SizeBytes == sizeBytes &&
                item.LastWriteTimeUtc == lastWriteTimeUtc &&
                string.Equals(item.ApiOrigin, apiOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Upsert(DesktopImportTransfer transfer)
    {
        lock (_sync)
        {
            var items = LoadUnsafe();
            var index = items.FindIndex(item => item.Id == transfer.Id);
            if (index >= 0) items[index] = transfer;
            else items.Add(transfer);
            SaveUnsafe(items);
        }
    }

    public void Remove(Guid id)
    {
        lock (_sync)
        {
            var items = LoadUnsafe();
            if (items.RemoveAll(item => item.Id == id) > 0) SaveUnsafe(items);
        }
    }

    private static List<DesktopImportTransfer> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<DesktopImportTransfer>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
        }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
    }

    private static void SaveUnsafe(List<DesktopImportTransfer> items)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = FilePath + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, JsonOptions));
        File.Move(temporary, FilePath, true);
    }
}

public sealed record DesktopImportTransfer(
    Guid Id,
    string ApiOrigin,
    string LocalPath,
    string MeetingId,
    string MeetingTitle,
    string UploadUrl,
    long SizeBytes,
    long ConfirmedOffset,
    DateTime LastWriteTimeUtc,
    DateTimeOffset UpdatedAtUtc,
    bool UploadCompleted = false);

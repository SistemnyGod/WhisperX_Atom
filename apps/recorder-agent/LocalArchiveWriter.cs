using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Converts the transport chunks into a permanent, user-visible audio archive.
/// It never removes spool data; PurgeFinalizedSessionAsync remains responsible for
/// deleting only confirmed transport chunks.
/// </summary>
public sealed class LocalArchiveWriter(
    SpoolStore spool,
    AgentStorageSettings storage,
    ILogger<LocalArchiveWriter> logger)
{
    private readonly ConcurrentDictionary<string, Task<string>> _writes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _manifestGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ffmpegPath = Environment.GetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH") ?? "ffmpeg";
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Task<string> CreateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("session_required", nameof(sessionId));
        var task = _writes.GetOrAdd(sessionId, _ => CreateCoreAsync(sessionId, cancellationToken));
        _ = task.ContinueWith(completed =>
        {
            if ((completed.IsFaulted || completed.IsCanceled) && _writes.TryGetValue(sessionId, out var current) && ReferenceEquals(current, completed))
                _writes.TryRemove(sessionId, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    public async Task SetUploadStateAsync(string sessionId, string state, string? error, CancellationToken cancellationToken = default)
    {
        var directory = await LocateArchiveAsync(sessionId, cancellationToken);
        if (directory is null) return;

        var gate = _manifestGates.GetOrAdd(directory, _ => new object());
        lock (gate)
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) return;
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllText(manifestPath), _json);
            if (manifest is null) return;
            var updated = manifest with { UploadState = state, UploadError = error, UpdatedAt = DateTimeOffset.UtcNow };
            WriteJsonAtomically(manifestPath, updated);
        }

        var stateDbPath = Path.Combine(directory, "upload-state.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stateDbPath }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS upload_state (id INTEGER PRIMARY KEY CHECK(id=1), state TEXT NOT NULL, error TEXT, updated_at TEXT NOT NULL); INSERT INTO upload_state(id,state,error,updated_at) VALUES(1,$state,$error,$updated) ON CONFLICT(id) DO UPDATE SET state=excluded.state,error=excluded.error,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string> CreateCoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        var existing = await LocateArchiveAsync(sessionId, cancellationToken);
        if (existing is not null) return existing;

        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("recording_session_not_found");
        var chunks = await spool.GetArchiveChunksAsync(sessionId, cancellationToken);
        if (chunks.Count == 0) throw new InvalidOperationException("recording_chunks_not_found");

        var startedAt = info.StartedAt ?? DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(info.Title) ? "Запись" : info.Title.Trim();
        var folderName = $"{startedAt.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{Sanitize(title)}_{sessionId[..Math.Min(8, sessionId.Length)]}";
        var directory = Path.Combine(storage.ArchiveRoot, "Meetings", folderName);
        var sourceDirectory = Path.Combine(directory, "source");
        var exportDirectory = Path.Combine(directory, "export");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(exportDirectory);

        var trackFiles = new List<ArchiveFileEntry>();
        foreach (var group in chunks.GroupBy(item => new { item.TrackId, item.TrackType, item.SampleRate, item.Channels }).OrderBy(item => item.Key.TrackType))
        {
            var ordered = group.OrderBy(item => item.Sequence).ToArray();
            ValidateTrack(ordered);
            var trackPath = Path.Combine(sourceDirectory, $"track-{Sanitize(group.Key.TrackType)}-{Sanitize(group.Key.TrackId[..Math.Min(8, group.Key.TrackId.Length)])}.flac");
            await ConcatTrackAsync(ordered, trackPath, cancellationToken);
            trackFiles.Add(await DescribeFileAsync(trackPath, "source", group.Key.TrackType, group.Key.SampleRate, group.Key.Channels, ordered.Sum(item => item.SampleCount), cancellationToken));
        }

        var masterPath = Path.Combine(exportDirectory, "master.flac");
        await CreateMasterAsync(trackFiles.Select(item => Path.Combine(directory, item.RelativePath)).ToArray(), masterPath, cancellationToken);
        var exportFiles = new List<ArchiveFileEntry>
        {
            await DescribeFileAsync(masterPath, "export", "master", 48000, 1, null, cancellationToken)
        };

        var previewPath = Path.Combine(exportDirectory, "preview.opus");
        try
        {
            await RunFfmpegAsync(["-i", masterPath, "-c:a", "libopus", "-b:a", "48k", previewPath], cancellationToken);
            exportFiles.Add(await DescribeFileAsync(previewPath, "export", "preview", 48000, 1, null, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to create Opus preview. Master archive remains available. Session={SessionId}", sessionId);
        }

        var manifest = new ArchiveManifest(
            sessionId,
            info.MeetingId,
            title,
            info.StartedAt,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "WAITING_FOR_API",
            null,
            trackFiles.Concat(exportFiles).ToArray());
        WriteJsonAtomically(Path.Combine(directory, "manifest.json"), manifest);
        await InitializeUploadStateAsync(Path.Combine(directory, "upload-state.db"), cancellationToken);
        await SetUploadStateAsync(sessionId, "WAITING_FOR_API", null, cancellationToken);
        logger.LogInformation("Permanent local audio archive created. Session={SessionId}, Directory={Directory}", sessionId, directory);
        return directory;
    }

    private async Task<string?> LocateArchiveAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_writes.TryGetValue(sessionId, out var pending) && pending.IsCompletedSuccessfully)
            return await pending;

        var meetingsRoot = Path.Combine(storage.ArchiveRoot, "Meetings");
        if (!Directory.Exists(meetingsRoot)) return null;
        foreach (var directory in Directory.EnumerateDirectories(meetingsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), _json);
                if (string.Equals(manifest?.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return directory;
            }
            catch (JsonException) { logger.LogWarning("Ignoring malformed archive manifest {ManifestPath}", manifestPath); }
        }
        return null;
    }

    private async Task ConcatTrackAsync(IReadOnlyList<RecordingArchiveChunk> chunks, string output, CancellationToken cancellationToken)
    {
        var listPath = output + ".concat.txt";
        var lines = chunks.Select(chunk => $"file '{EscapeConcatPath(chunk.LocalPath)}'");
        await File.WriteAllTextAsync(listPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, Encoding.UTF8, cancellationToken);
        try
        {
            await RunFfmpegAsync(["-f", "concat", "-safe", "0", "-i", listPath, "-c:a", "flac", output], cancellationToken);
        }
        finally
        {
            try { File.Delete(listPath); } catch (IOException) { }
        }
    }

    private async Task CreateMasterAsync(IReadOnlyList<string> tracks, string output, CancellationToken cancellationToken)
    {
        if (tracks.Count == 1)
        {
            await RunFfmpegAsync(["-i", tracks[0], "-ac", "1", "-ar", "48000", "-c:a", "flac", output], cancellationToken);
            return;
        }

        var arguments = new List<string>();
        foreach (var track in tracks) arguments.AddRange(["-i", track]);
        arguments.AddRange(["-filter_complex", $"amix=inputs={tracks.Count}:duration=longest:normalize=0,aresample=48000", "-ac", "1", "-c:a", "flac", output]);
        await RunFfmpegAsync(arguments, cancellationToken);
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg_start_failed");
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg_failed:{error.Trim()}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"ffmpeg_not_found:{_ffmpegPath}", ex);
        }
    }

    private static void ValidateTrack(IReadOnlyList<RecordingArchiveChunk> chunks)
    {
        long previousEnd = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            if (chunk.Sequence != index) throw new InvalidOperationException($"recording_chunk_sequence_gap:{chunk.TrackId}:{index}");
            if (chunk.StartSample != previousEnd) throw new InvalidOperationException($"recording_chunk_sample_gap:{chunk.TrackId}:{index}");
            if (!File.Exists(chunk.LocalPath)) throw new FileNotFoundException("recording_chunk_missing", chunk.LocalPath);
            var size = new FileInfo(chunk.LocalPath).Length;
            if (size != chunk.SizeBytes) throw new InvalidOperationException($"recording_chunk_size_mismatch:{chunk.TrackId}:{index}");
            if (!string.Equals(ComputeSha256(chunk.LocalPath), chunk.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"recording_chunk_checksum_mismatch:{chunk.TrackId}:{index}");
            previousEnd += chunk.SampleCount;
        }
    }

    private static Task<ArchiveFileEntry> DescribeFileAsync(string path, string kind, string name, int sampleRate, int channels, long? sampleCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ArchiveFileEntry(kind, name, Path.GetRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, path), new FileInfo(path).Length, ComputeSha256(path), sampleRate, channels, sampleCount));
    }

    private static async Task InitializeUploadStateAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS upload_state (id INTEGER PRIMARY KEY CHECK(id=1), state TEXT NOT NULL, error TEXT, updated_at TEXT NOT NULL); INSERT OR IGNORE INTO upload_state(id,state,updated_at) VALUES(1,'WAITING_FOR_API',$updated)";
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        var temporary = path + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string EscapeConcatPath(string path) => path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "recording" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private sealed record ArchiveManifest(string SessionId, Guid? MeetingId, string Title, DateTimeOffset? StartedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string UploadState, string? UploadError, IReadOnlyList<ArchiveFileEntry> Files);
    private sealed record ArchiveFileEntry(string Kind, string Name, string RelativePath, long SizeBytes, string Sha256, int SampleRate, int Channels, long? SampleCount);
}

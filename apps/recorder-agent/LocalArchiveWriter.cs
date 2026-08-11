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
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _writes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _manifestGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ffmpegPath = Environment.GetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH") ?? "ffmpeg";
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _ffprobePath = Environment.GetEnvironmentVariable("ATOM_AGENT_FFPROBE_PATH") ?? "ffprobe";

    public Task<string> CreateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("session_required", nameof(sessionId));
        // ConcurrentDictionary may invoke a value factory more than once.
        // Lazy<Task<...>> is required here: discarded factory tasks would still
        // run and delete the same concat/.part files as the winning task.
        var lazy = _writes.GetOrAdd(sessionId, _ => new Lazy<Task<string>>(
            () => CreateCoreAsync(sessionId, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(completed =>
        {
            if ((completed.IsFaulted || completed.IsCanceled) && _writes.TryGetValue(sessionId, out var current) && ReferenceEquals(current, lazy))
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
        if (_writes.TryGetValue(sessionId, out var pending) && pending.IsValueCreated && pending.Value.IsCompletedSuccessfully)
            return await pending.Value;

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
                if (string.Equals(manifest?.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                    && manifest is not null
                    && IsUsableManifest(directory, manifest))
                    return directory;
            }
            catch (JsonException) { logger.LogWarning("Ignoring malformed archive manifest {ManifestPath}", manifestPath); }
        }
        return null;
    }

    private static bool IsUsableManifest(string directory, ArchiveManifest manifest)
    {
        var files = manifest.Files;
        if (files is null || files.Count == 0) return false;

        // A preview is optional, but source tracks and the master are part of
        // the durable archive contract.  Do not let a manifest created before
        // a crash hide a partially written archive from recovery.
        foreach (var file in files.Where(file =>
                     !string.Equals(file.Kind, "preview", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(file.Name, "preview", StringComparison.OrdinalIgnoreCase)))
            if (!IsArchiveFileUsable(directory, file)) return false;

        var master = files.FirstOrDefault(file =>
            string.Equals(file.Kind, "export", StringComparison.OrdinalIgnoreCase)
            && string.Equals(file.Name, "master", StringComparison.OrdinalIgnoreCase));
        return master is not null && IsArchiveFileUsable(directory, master);
    }

    private static bool IsArchiveFileUsable(string directory, ArchiveFileEntry file)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath)) return false;
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, file.RelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 && (file.SizeBytes <= 0 || info.Length == file.SizeBytes);
    }

    private async Task ConcatTrackAsync(IReadOnlyList<RecordingArchiveChunk> chunks, string output, CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid().ToString("N");
        var listPath = output + $".{attempt}.concat.txt";
        var outputPart = output + $".{attempt}.part";
        var lines = chunks.Select(chunk => $"file '{EscapeConcatPath(chunk.LocalPath)}'");
        DeleteIfExists(listPath);
        DeleteIfExists(outputPart);
        // FFmpeg's concat demuxer does not accept a UTF-8 BOM before the first
        // `file` directive. Encoding.UTF8 includes EF BB BF on this runtime.
        await File.WriteAllTextAsync(listPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
        try
        {
            // The atomic target intentionally ends in `.part`; ffmpeg cannot
            // infer the muxer from that suffix on Windows.
            await RunFfmpegAsync(["-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c:a", "flac", "-f", "flac", outputPart], cancellationToken);
            await ValidateAudioFileAsync(outputPart, cancellationToken);
            File.Move(outputPart, output, true);
        }
        finally
        {
            DeleteIfExists(listPath);
            DeleteIfExists(outputPart);
        }
    }

    private async Task CreateMasterAsync(IReadOnlyList<string> tracks, string output, CancellationToken cancellationToken)
    {
        var outputPart = output + $".{Guid.NewGuid():N}.part";
        DeleteIfExists(outputPart);
        if (tracks.Count == 1)
        {
            try
            {
                await RunFfmpegAsync(["-y", "-i", tracks[0], "-ac", "1", "-ar", "48000", "-c:a", "flac", "-f", "flac", outputPart], cancellationToken);
                await ValidateAudioFileAsync(outputPart, cancellationToken);
                File.Move(outputPart, output, true);
            }
            finally { DeleteIfExists(outputPart); }
            return;
        }

        var arguments = new List<string>();
        foreach (var track in tracks) arguments.AddRange(["-i", track]);
        arguments.AddRange(["-y", "-filter_complex", $"amix=inputs={tracks.Count}:duration=longest:normalize=0,aresample=48000", "-ac", "1", "-c:a", "flac", "-f", "flac", outputPart]);
        try
        {
            await RunFfmpegAsync(arguments, cancellationToken);
            await ValidateAudioFileAsync(outputPart, cancellationToken);
            File.Move(outputPart, output, true);
        }
        finally { DeleteIfExists(outputPart); }
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

    private async Task ValidateAudioFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException("local_audio_output_empty");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffprobe_start_failed");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration) || duration <= 0)
                throw new InvalidOperationException($"ffprobe_invalid_audio:{error.Trim()}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"ffprobe_not_found:{_ffprobePath}", ex);
        }
    }

    private static void ValidateTrack(IReadOnlyList<RecordingArchiveChunk> chunks)
    {
        long? previousEnd = null;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            if (chunk.Sequence != index) throw new InvalidOperationException($"recording_chunk_sequence_gap:{chunk.TrackId}:{index}");
            if (previousEnd is long expectedStart && chunk.StartSample != expectedStart) throw new InvalidOperationException($"recording_chunk_sample_gap:{chunk.TrackId}:{index}");
            if (!File.Exists(chunk.LocalPath)) throw new FileNotFoundException("recording_chunk_missing", chunk.LocalPath);
            var size = new FileInfo(chunk.LocalPath).Length;
            if (size != chunk.SizeBytes) throw new InvalidOperationException($"recording_chunk_size_mismatch:{chunk.TrackId}:{index}");
            if (!string.Equals(ComputeSha256(chunk.LocalPath), chunk.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"recording_chunk_checksum_mismatch:{chunk.TrackId}:{index}");
            previousEnd = chunk.StartSample + chunk.SampleCount;
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

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
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

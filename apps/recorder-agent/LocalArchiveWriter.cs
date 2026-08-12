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
        var archiveRoot = SelectWritableArchiveRoot(sessionId);
        var directory = Path.Combine(archiveRoot, "Meetings", folderName);
        var sourceDirectory = Path.Combine(directory, "source");
        var exportDirectory = Path.Combine(directory, "export");
        try
        {
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(exportDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Public Documents can be protected by Windows security controls.
            // Preserve a durable local archive under ProgramData instead of
            // failing the whole recording finalization.
            var fallbackRoot = FallbackArchiveRoot();
            logger.LogWarning(exception, "Configured archive root is unavailable; using fallback archive root. Session={SessionId}, ArchiveRoot={ArchiveRoot}", sessionId, fallbackRoot);
            directory = Path.Combine(fallbackRoot, "Meetings", folderName);
            sourceDirectory = Path.Combine(directory, "source");
            exportDirectory = Path.Combine(directory, "export");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(exportDirectory);
        }

        var trackFiles = new List<ArchiveFileEntry>();
        var masterInputs = new List<MasterTrackInput>();
        foreach (var group in chunks.GroupBy(item => new { item.TrackId, item.TrackType, item.SampleRate, item.Channels }).OrderBy(item => item.Key.TrackType))
        {
            var ordered = group.OrderBy(item => item.Sequence).ToArray();
            ValidateTrack(ordered);
            var trackPath = Path.Combine(sourceDirectory, $"track-{Sanitize(group.Key.TrackType)}-{Sanitize(group.Key.TrackId[..Math.Min(8, group.Key.TrackId.Length)])}.flac");
            await ConcatTrackAsync(ordered, trackPath, cancellationToken);
            var entry = await DescribeFileAsync(trackPath, "source", group.Key.TrackType, group.Key.SampleRate, group.Key.Channels, ordered.Sum(item => item.SampleCount), cancellationToken);
            trackFiles.Add(entry);
            masterInputs.Add(new MasterTrackInput(trackPath, group.Key.TrackType, group.Key.SampleRate, ordered[0].StartSample, ordered.Sum(item => item.SampleCount)));
        }

        var profile = (await spool.GetTrackInfosAsync(sessionId, cancellationToken)).Select(track => track.Profile).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "ROOM";
        var masterPath = Path.Combine(exportDirectory, "master.flac");
        var assemblyResult = await CreateMasterAsync(masterInputs, masterPath, profile, cancellationToken);
        var exportFiles = new List<ArchiveFileEntry>
        {
            await DescribeFileAsync(masterPath, "export", "master", 48000, 1, null, cancellationToken)
        };
        var assemblyResultPath = Path.Combine(exportDirectory, "assembly-result.json");
        WriteJsonAtomically(assemblyResultPath, assemblyResult);
        exportFiles.Add(await DescribeFileAsync(assemblyResultPath, "export", "assembly-result", 0, 0, null, cancellationToken));

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

        var meetingsRoots = new[]
        {
            Path.Combine(storage.ArchiveRoot, "Meetings"),
            Path.Combine(FallbackArchiveRoot(), "Meetings")
        }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var meetingsRoot in meetingsRoots)
        {
            if (!Directory.Exists(meetingsRoot)) continue;
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
        }
        return null;
    }

    private static string FallbackArchiveRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WhisperXAtom", "Archive");

    private string SelectWritableArchiveRoot(string sessionId)
    {
        try
        {
            VerifyAtomicWrite(storage.ArchiveRoot);
            return storage.ArchiveRoot;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            var fallbackRoot = FallbackArchiveRoot();
            logger.LogWarning(exception, "Configured archive root cannot atomically write files; using fallback archive root. Session={SessionId}, ArchiveRoot={ArchiveRoot}", sessionId, fallbackRoot);
            VerifyAtomicWrite(fallbackRoot);
            return fallbackRoot;
        }
    }

    private static void VerifyAtomicWrite(string archiveRoot)
    {
        var probeDirectory = Path.Combine(archiveRoot, "Meetings");
        Directory.CreateDirectory(probeDirectory);
        var probePath = Path.Combine(probeDirectory, $".write-probe-{Guid.NewGuid():N}");
        var partPath = probePath + ".part";
        try
        {
            File.WriteAllText(partPath, "ok", new UTF8Encoding(false));
            File.Move(partPath, probePath);
        }
        finally
        {
            DeleteIfExists(partPath);
            DeleteIfExists(probePath);
        }
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
        var tempRoot = Path.Combine(Path.GetTempPath(), "WhisperXAtom");
        Directory.CreateDirectory(tempRoot);
        var listPath = Path.Combine(tempRoot, $"{attempt}.concat.txt");
        var outputPart = Path.Combine(tempRoot, $"{attempt}.flac.part");
        var lines = chunks.Select(chunk => $"file '{EscapeConcatPath(chunk.LocalPath)}'");
        var outputDirectory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidOperationException("LOCAL_ARCHIVE_PATH_INVALID");
        // Recovery may observe a partially-created archive directory after a
        // previous process was interrupted. Re-establish the final destination
        // immediately before the atomic move. Temporary files live in the
        // system temp directory so service account/path encoding restrictions
        // cannot prevent creation of the concat input.
        Directory.CreateDirectory(outputDirectory);
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

    private async Task<MasterAssemblyResult> CreateMasterAsync(IReadOnlyList<MasterTrackInput> tracks, string output, string recordingProfile, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidOperationException("LOCAL_ARCHIVE_PATH_INVALID");
        Directory.CreateDirectory(outputDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), "WhisperXAtom");
        Directory.CreateDirectory(tempRoot);
        var outputPart = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.master.flac.part");
        DeleteIfExists(outputPart);
        var microphone = tracks.FirstOrDefault(track => track.TrackType.Contains("microphone", StringComparison.OrdinalIgnoreCase));
        var loopback = tracks.FirstOrDefault(track => track.TrackType.Contains("system", StringComparison.OrdinalIgnoreCase) || track.TrackType.Contains("loopback", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<MasterTrackInput> selectedTracks = recordingProfile.Equals("ONLINE", StringComparison.OrdinalIgnoreCase)
            ? tracks
            : recordingProfile.Equals("SYSTEM_ONLY", StringComparison.OrdinalIgnoreCase) ? (loopback is null ? tracks.Take(1).ToArray() : [loopback])
            : microphone is null ? tracks.Take(1).ToArray() : [microphone];
        var baseStart = selectedTracks.Min(track => track.FirstStartSample);
        var timeline = new List<MasterTrackTimeline>();
        foreach (var track in selectedTracks)
        {
            var actual = await GetDurationMsAsync(track.Path, cancellationToken);
            var expected = track.ExpectedSamples * 1000d / Math.Max(1, track.SampleRate);
            timeline.Add(new MasterTrackTimeline(track.TrackType, Math.Round((track.FirstStartSample - baseStart) * 1000d / Math.Max(1, track.SampleRate)), Math.Round(expected), actual, Math.Round(actual - expected)));
        }
        var tolerance = int.TryParse(Environment.GetEnvironmentVariable("AUDIO_TRACK_DRIFT_TOLERANCE_MS"), out var configuredTolerance) ? configuredTolerance : 250;
        var warningsList = timeline.Where(item => Math.Abs(item.DriftMs) > tolerance).Select(_ => "AUDIO_TRACK_DRIFT_HIGH").Distinct().ToList();
        var degradedOnline = recordingProfile.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) && warningsList.Count > 0;
        var selectedAsrSource = selectedTracks.Count == 1
            ? (recordingProfile.Equals("SYSTEM_ONLY", StringComparison.OrdinalIgnoreCase) ? "loopback" : "microphone")
            : "controlled_mix";
        var mixStrategy = recordingProfile.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) && selectedTracks.Count > 1 ? "controlled_online_mix" : "single_original_track";
        if (degradedOnline)
        {
            var fallback = microphone ?? loopback ?? tracks.FirstOrDefault();
            if (fallback is null) throw new InvalidOperationException("recording_source_unavailable");
            selectedTracks = [fallback];
            selectedAsrSource = fallback == microphone ? "microphone" : fallback == loopback ? "loopback" : "single_track";
            mixStrategy = "online_single_track_fallback";
            warningsList.Add("ONLINE_MIX_DEGRADED");
        }
        var warnings = warningsList.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new MasterAssemblyResult(recordingProfile, selectedAsrSource, tracks.Count, mixStrategy, tolerance, timeline, warnings);
        if (selectedTracks.Count == 1)
        {
            try
            {
                await RunFfmpegAsync(["-y", "-i", selectedTracks[0].Path, "-ac", "1", "-ar", "48000", "-c:a", "flac", "-f", "flac", outputPart], cancellationToken);
                await ValidateAudioFileAsync(outputPart, cancellationToken);
                File.Move(outputPart, output, true);
            }
            finally { DeleteIfExists(outputPart); }
            if (warnings.Length > 0 && !degradedOnline) throw new InvalidOperationException("AUDIO_TRACK_DRIFT_HIGH");
            return result;
        }

        var arguments = new List<string>();
        foreach (var track in selectedTracks) arguments.AddRange(["-i", track.Path]);
        var chains = selectedTracks.Select((track, index) => {
            var delay = Math.Max(0, (long)timeline[index].StartOffsetMs);
            return $"[{index}:a]aresample=48000:async=1000:first_pts=0,adelay={delay}:all=1[a{index}]";
        }).ToArray();
        var labels = string.Concat(selectedTracks.Select((_, index) => $"[a{index}]"));
        var filter = string.Join(';', chains) + $";{labels}amix=inputs={selectedTracks.Count}:duration=longest:dropout_transition=2:normalize=1,alimiter=limit=0.95,aresample=48000[mix]";
        arguments.AddRange(["-y", "-filter_complex", filter, "-map", "[mix]", "-ac", "1", "-c:a", "flac", "-f", "flac", outputPart]);
        try
        {
            await RunFfmpegAsync(arguments, cancellationToken);
            await ValidateAudioFileAsync(outputPart, cancellationToken);
            File.Move(outputPart, output, true);
        }
        finally { DeleteIfExists(outputPart); }
        if (warnings.Length > 0 && !degradedOnline) throw new InvalidOperationException("AUDIO_TRACK_DRIFT_HIGH");
        return result;
    }

    private async Task<long> GetDurationMsAsync(string path, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = _ffprobePath, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path }) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("ffprobe_start_failed");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            throw new InvalidOperationException("ffprobe_invalid_audio");
        return (long)Math.Round(seconds * 1000d);
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
    private sealed record MasterTrackInput(string Path, string TrackType, int SampleRate, long FirstStartSample, long ExpectedSamples);
    private sealed record MasterTrackTimeline(string TrackType, double StartOffsetMs, double ExpectedDurationMs, long ActualDurationMs, double DriftMs);
    private sealed record MasterAssemblyResult(string RecordingProfile, string SelectedAsrSource, int TrackCount, string MixStrategy, int DriftToleranceMs, IReadOnlyList<MasterTrackTimeline> Tracks, IReadOnlyList<string> Warnings);
}

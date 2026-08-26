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
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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

    /// <summary>Creates the user-facing destination at START.</summary>
    public async Task<string> ReserveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("recording_session_not_found");
        if (!string.IsNullOrWhiteSpace(info.ArchivePath) && Directory.Exists(info.ArchivePath))
            return Path.GetFullPath(info.ArchivePath);
        var directory = LocalMeetingDirectoryResolver.Resolve(storage, info, sessionId);
        var technicalDirectory = Path.Combine(directory, ".whisperx");
        Directory.CreateDirectory(technicalDirectory);
        TryMarkHidden(technicalDirectory);
        // Keep archive_path as the completion attestation used by the
        // recovery scheduler. The playable path is already an additive local
        // field and can safely carry the reserved directory for UI visibility
        // until the verified FLAC manifest is written.
        await spool.SetPlayableAudioStateAsync(
            sessionId,
            "PENDING",
            path: directory,
            clearError: true,
            clearNextRetry: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return directory;
    }

    /// <summary>
    /// Checks the durable, user-visible archive rather than the reserved
    /// directory path. Reservation happens at START so the UI can show a
    /// destination immediately; it must not be mistaken for a completed FLAC.
    /// </summary>
    public async Task<bool> IsReadyAsync(string sessionId, string? reservedPath = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(reservedPath))
                return await IsReadyAtPathAsync(reservedPath, sessionId, cancellationToken).ConfigureAwait(false);
            return await LocateArchiveAsync(sessionId, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not attest local archive yet. Session={SessionId}", sessionId);
            return false;
        }
    }

    private async Task<bool> IsReadyAtPathAsync(string directory, string sessionId, CancellationToken cancellationToken)
    {
        var normalized = Path.GetFullPath(directory);
        var manifestPath = FindManifestPath(normalized);
        if (!File.Exists(manifestPath)) return false;
        try
        {
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), _json);
            return manifest is not null
                && string.Equals(manifest.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                && IsUsableManifest(normalized, manifest);
        }
        catch (JsonException) { return false; }
    }

    public async Task SetUploadStateAsync(string sessionId, string state, string? error, CancellationToken cancellationToken = default)
    {
        var directory = await LocateArchiveAsync(sessionId, cancellationToken);
        if (directory is null) return;

        var gate = _manifestGates.GetOrAdd(directory, _ => new object());
        lock (gate)
        {
            var manifestPath = FindManifestPath(directory);
            if (!File.Exists(manifestPath)) return;
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllText(manifestPath), _json);
            if (manifest is null) return;
            var updated = manifest with { UploadState = state, UploadError = error, UpdatedAt = DateTimeOffset.UtcNow };
            WriteJsonAtomically(manifestPath, updated);
        }

        var stateDbPath = StateDbPath(directory, IsLegacyLayout(directory));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stateDbPath }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS upload_state (id INTEGER PRIMARY KEY CHECK(id=1), state TEXT NOT NULL, error TEXT, updated_at TEXT NOT NULL); INSERT INTO upload_state(id,state,error,updated_at) VALUES(1,$state,$error,$updated) ON CONFLICT(id) DO UPDATE SET state=excluded.state,error=excluded.error,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Converts the permanent local master (or the playable WAV fallback) to a
    /// user-selected destination. This operation is local-only and never
    /// changes the archive, spool or server delivery state.
    /// </summary>
    public async Task<string> ExportAsync(
        string sessionId,
        string? format,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("session_required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("destination_path_required", nameof(destinationPath));

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "WAV" : format.Trim().ToUpperInvariant();
        if (normalizedFormat is not ("WAV" or "MP3"))
            throw new InvalidOperationException("LOCAL_AUDIO_EXPORT_FORMAT_UNSUPPORTED");

        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("session_not_found");
        var source = await ResolveExportSourceAsync(info, sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("LOCAL_AUDIO_NOT_READY");

        var destination = Path.GetFullPath(destinationPath.Trim());
        var extension = normalizedFormat == "WAV" ? ".wav" : ".mp3";
        if (!string.Equals(Path.GetExtension(destination), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LOCAL_AUDIO_EXPORT_EXTENSION_INVALID");
        if (string.Equals(Path.GetFullPath(source), destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LOCAL_AUDIO_EXPORT_SOURCE_EQUALS_DESTINATION");

        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("LOCAL_AUDIO_EXPORT_PATH_INVALID");
        Directory.CreateDirectory(parent);
        var part = destination + ".whisperx.part";
        DeleteIfExists(part);
        try
        {
            var arguments = normalizedFormat == "WAV"
                ? new[] { "-y", "-i", source, "-map", "0:a:0", "-vn", "-c:a", "pcm_s16le", "-f", "wav", part }
                : new[] { "-y", "-i", source, "-map", "0:a:0", "-vn", "-c:a", "libmp3lame", "-q:a", "2", "-f", "mp3", part };
            await RunFfmpegAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(part) || new FileInfo(part).Length == 0)
                throw new InvalidOperationException("LOCAL_AUDIO_EXPORT_EMPTY");
            File.Move(part, destination, true);
            return destination;
        }
        finally
        {
            DeleteIfExists(part);
        }
    }

    private async Task<string?> ResolveExportSourceAsync(
        RecordingSessionInfo info,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(info.ArchivePath))
        {
            var archive = Path.GetFullPath(info.ArchivePath);
            foreach (var candidate in new[]
            {
                Path.Combine(archive, "Аудиозапись.flac"),
                Path.Combine(archive, "export", "master.flac")
            })
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0) return candidate;
            }
        }

        var playable = await spool.GetPlayableFilesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return playable
            .Where(file => File.Exists(file.LocalPath))
            .OrderBy(file => file.TrackType.Contains("microphone", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(file => file.LocalPath)
            .FirstOrDefault();
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
        // Archive and user-playable files share one deterministic meeting
        // directory. The resolver also applies the configured-root/fallback
        // policy before any files are created.
        var directory = LocalMeetingDirectoryResolver.Resolve(storage, info, sessionId);
        var legacyLayout = IsLegacyLayout(directory);
        var technicalDirectory = legacyLayout ? directory : Path.Combine(directory, ".whisperx");
        var sourceDirectory = legacyLayout ? Path.Combine(directory, "source") : Path.Combine(directory, "Дорожки");
        var exportDirectory = legacyLayout ? Path.Combine(directory, "export") : directory;
        try
        {
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(exportDirectory);
            Directory.CreateDirectory(technicalDirectory);
            if (!legacyLayout) TryMarkHidden(technicalDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Public Documents can be protected by Windows security controls.
            // Preserve a durable local archive under ProgramData instead of
            // failing the whole recording finalization.
            logger.LogWarning(exception, "Meeting archive directory is unavailable. Session={SessionId}, Directory={Directory}", sessionId, directory);
            throw;
        }

        var trackFiles = new List<ArchiveFileEntry>();
        var masterInputs = new List<MasterTrackInput>();
        var trackAssemblyModes = new List<string>();
        var friendlyTrackCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in chunks.GroupBy(item => new { item.TrackId, item.TrackType, item.SampleRate, item.Channels }).OrderBy(item => item.Key.TrackType))
        {
            var ordered = group.OrderBy(item => item.Sequence).ToArray();
            ValidateTrack(ordered);
            var friendlyName = FriendlyTrackName(group.Key.TrackType);
            friendlyTrackCounts.TryGetValue(friendlyName, out var friendlyCount);
            friendlyCount++;
            friendlyTrackCounts[friendlyName] = friendlyCount;
            var trackFileName = legacyLayout
                ? $"track-{Sanitize(group.Key.TrackType)}-{Sanitize(group.Key.TrackId[..Math.Min(8, group.Key.TrackId.Length)])}.flac"
                : friendlyCount == 1 ? $"{friendlyName}.flac" : $"{friendlyName} ({friendlyCount}).flac";
            var trackPath = Path.Combine(sourceDirectory, trackFileName);
            trackAssemblyModes.Add(await ConcatTrackAsync(ordered, trackPath, cancellationToken));
            var entry = await DescribeFileAsync(directory, trackPath, "source", group.Key.TrackType, group.Key.SampleRate, group.Key.Channels, ordered.Sum(item => item.SampleCount), cancellationToken);
            trackFiles.Add(entry);
            masterInputs.Add(new MasterTrackInput(trackPath, group.Key.TrackType, group.Key.SampleRate, ordered[0].StartSample, ordered.Sum(item => item.SampleCount)));
        }

        var profile = (await spool.GetTrackInfosAsync(sessionId, cancellationToken)).Select(track => track.Profile).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "ROOM";
        var masterPath = Path.Combine(exportDirectory, legacyLayout ? "master.flac" : "Аудиозапись.flac");
        var assemblyResult = await CreateMasterAsync(masterInputs, masterPath, profile, trackAssemblyModes, cancellationToken);
        var exportFiles = new List<ArchiveFileEntry>
        {
            await DescribeFileAsync(directory, masterPath, "export", "master", 48000, 1, null, cancellationToken)
        };
        var assemblyResultPath = Path.Combine(technicalDirectory, "assembly-result.json");
        WriteJsonAtomically(assemblyResultPath, assemblyResult);
        exportFiles.Add(await DescribeFileAsync(directory, assemblyResultPath, "export", "assembly-result", 0, 0, null, cancellationToken));

        var previewPath = Path.Combine(technicalDirectory, "preview.opus");
        try
        {
            await RunFfmpegAsync(["-i", masterPath, "-c:a", "libopus", "-b:a", "48k", previewPath], cancellationToken);
            exportFiles.Add(await DescribeFileAsync(directory, previewPath, "export", "preview", 48000, 1, null, cancellationToken));
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
        var manifestPath = ManifestPath(directory, legacyLayout);
        WriteJsonAtomically(manifestPath, manifest);
        await InitializeUploadStateAsync(StateDbPath(directory, legacyLayout), cancellationToken);
        await SetUploadStateAsync(sessionId, "WAITING_FOR_API", null, cancellationToken);
        logger.LogInformation("Permanent local audio archive created. Session={SessionId}, Directory={Directory}", sessionId, directory);
        return directory;
    }

    private async Task<string?> LocateArchiveAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_writes.TryGetValue(sessionId, out var pending) && pending.IsValueCreated && pending.Value.IsCompletedSuccessfully)
            return await pending.Value;

        var roots = new[] { storage.ArchiveRoot, FallbackArchiveRoot() }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in roots.SelectMany(EnumerateArchiveDirectories).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = FindManifestPath(directory);
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

    private static IEnumerable<string> EnumerateArchiveDirectories(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            // Yield the meeting directory for both layouts.  Returning the
            // hidden `.whisperx` child itself would make FindManifestPath look
            // for `.whisperx/.whisperx/manifest.json` and break restart/recovery
            // discovery of a valid new archive.
            if (File.Exists(Path.Combine(directory, "manifest.json"))
                || File.Exists(Path.Combine(directory, ".whisperx", "manifest.json")))
                yield return directory;
    }

    private static bool IsLegacyLayout(string directory) =>
        Directory.Exists(Path.Combine(directory, "source"))
        || Directory.Exists(Path.Combine(directory, "export"))
        || File.Exists(Path.Combine(directory, "manifest.json"));

    private static string ManifestPath(string directory, bool legacy) => legacy
        ? Path.Combine(directory, "manifest.json")
        : Path.Combine(directory, ".whisperx", "manifest.json");

    private static string FindManifestPath(string directory) =>
        File.Exists(Path.Combine(directory, "manifest.json"))
            ? Path.Combine(directory, "manifest.json")
            : Path.Combine(directory, ".whisperx", "manifest.json");

    private static string StateDbPath(string directory, bool legacy) => legacy
        ? Path.Combine(directory, "upload-state.db")
        : Path.Combine(directory, ".whisperx", "upload-state.db");

    private static string FriendlyTrackName(string trackType) =>
        trackType.Contains("system", StringComparison.OrdinalIgnoreCase) || trackType.Contains("loopback", StringComparison.OrdinalIgnoreCase)
            ? "Системный звук"
            : "Микрофон";

    private static void TryMarkHidden(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        catch (Exception) { }
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

    private async Task<string> ConcatTrackAsync(IReadOnlyList<RecordingArchiveChunk> chunks, string output, CancellationToken cancellationToken)
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
            // Compatible FLAC chunks can be joined without a lossy or
            // CPU-heavy decode/re-encode. If a container is incompatible or
            // validation rejects the stream, retry once through the existing
            // safe re-encode path.
            try
            {
                await RunFfmpegAsync(["-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c:a", "copy", "-f", "flac", outputPart], cancellationToken);
                await ValidateAudioFileAsync(outputPart, chunks[0].SampleRate, chunks[0].Channels, chunks.Sum(item => item.SampleCount), cancellationToken);
                File.Move(outputPart, output, true);
                return "STREAM_COPY";
            }
            catch (Exception copyException) when (copyException is InvalidOperationException or Win32Exception)
            {
                DeleteIfExists(outputPart);
                logger.LogDebug(copyException, "FLAC stream copy was not compatible; using re-encode fallback. Track={TrackId}", chunks[0].TrackId);
                await RunFfmpegAsync(["-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c:a", "flac", "-f", "flac", outputPart], cancellationToken);
                await ValidateAudioFileAsync(outputPart, chunks[0].SampleRate, chunks[0].Channels, chunks.Sum(item => item.SampleCount), cancellationToken);
                File.Move(outputPart, output, true);
                return "REENCODE_FALLBACK";
            }
        }
        finally
        {
            DeleteIfExists(listPath);
            DeleteIfExists(outputPart);
        }
    }

    private async Task<MasterAssemblyResult> CreateMasterAsync(IReadOnlyList<MasterTrackInput> tracks, string output, string recordingProfile, IReadOnlyList<string> trackAssemblyModes, CancellationToken cancellationToken)
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
        var masterKind = string.Equals(mixStrategy, "controlled_online_mix", StringComparison.OrdinalIgnoreCase)
            ? "DERIVED_MIX_NO_AEC"
            : "CANONICAL_TRACK";
        var result = new MasterAssemblyResult(recordingProfile, selectedAsrSource, tracks.Count, mixStrategy, masterKind, "NONE", tolerance, timeline, warnings, trackAssemblyModes);
        if (selectedTracks.Count == 1)
        {
            try
            {
                await RunFfmpegAsync(["-y", "-i", selectedTracks[0].Path, "-ac", "1", "-ar", "48000", "-c:a", "flac", "-f", "flac", outputPart], cancellationToken);
                await ValidateAudioFileAsync(outputPart, 48000, 1, null, cancellationToken);
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
            await ValidateAudioFileAsync(outputPart, 48000, 1, null, cancellationToken);
            File.Move(outputPart, output, true);
        }
        finally { DeleteIfExists(outputPart); }
        if (warnings.Length > 0 && !degradedOnline) throw new InvalidOperationException("AUDIO_TRACK_DRIFT_HIGH");
        return result;
    }

    private async Task<long> GetDurationMsAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobePath = RecorderToolPaths.Ffprobe();
        var result = await ExternalProcessRunner.RunArchiveAsync(ffprobePath, ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path], cancellationToken);
        if (result.ExitCode != 0 || !double.TryParse(result.StandardOutput.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            throw new InvalidOperationException("ffprobe_invalid_audio");
        return (long)Math.Round(seconds * 1000d);
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var ffmpegPath = RecorderToolPaths.Ffmpeg();
        try
        {
            var result = await ExternalProcessRunner.RunArchiveAsync(ffmpegPath, arguments, cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException($"ffmpeg_failed:{result.StandardError.Trim()}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"ffmpeg_not_found:{ffmpegPath}", ex);
        }
    }

    private async Task ValidateAudioFileAsync(string path, int expectedSampleRate, int expectedChannels, long? expectedSamples, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException("local_audio_output_empty");

        var ffprobePath = RecorderToolPaths.Ffprobe();
        try
        {
            var result = await ExternalProcessRunner.RunArchiveAsync(ffprobePath, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,nb_samples:format=duration", "-of", "json", path], cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException($"ffprobe_invalid_audio:{result.StandardError.Trim()}");
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() == 0)
                throw new InvalidOperationException("ffprobe_invalid_audio:stream");
            var stream = streams[0];
            if (!stream.TryGetProperty("codec_name", out var codec)
                || !string.Equals(codec.GetString(), "flac", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ffprobe_invalid_audio:codec");
            if (!TryReadJsonInt(stream, "sample_rate", out var sampleRate) || sampleRate != expectedSampleRate)
                throw new InvalidOperationException("ffprobe_invalid_audio:sample_rate");
            if (!TryReadJsonInt(stream, "channels", out var channels) || channels != expectedChannels)
                throw new InvalidOperationException("ffprobe_invalid_audio:channels");
            if (expectedSamples is > 0 && stream.TryGetProperty("nb_samples", out var samples)
                && long.TryParse(samples.ToString(), out var actualSamples)
                && Math.Abs(actualSamples - expectedSamples.Value) > Math.Max(1, expectedSampleRate / 100))
                throw new InvalidOperationException("ffprobe_invalid_audio:sample_count");
            if (!document.RootElement.TryGetProperty("format", out var format)
                || !format.TryGetProperty("duration", out var durationValue)
                || !double.TryParse(durationValue.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration)
                || duration <= 0 || double.IsNaN(duration) || double.IsInfinity(duration))
                throw new InvalidOperationException("ffprobe_invalid_audio:duration");
            if (expectedSamples is > 0 && Math.Abs(duration - expectedSamples.Value / (double)expectedSampleRate) > 0.1d)
                throw new InvalidOperationException("ffprobe_invalid_audio:duration_mismatch");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ffprobe_invalid_audio:json", ex);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"ffprobe_not_found:{ffprobePath}", ex);
        }

        // ffprobe validates the container metadata; decode the complete file
        // as an additional integrity gate so a truncated FLAC cannot be sent
        // to the server and fail later during ASR.
        var ffmpegPath = RecorderToolPaths.Ffmpeg();
        try
        {
            var result = await ExternalProcessRunner.RunArchiveAsync(ffmpegPath, ["-v", "error", "-i", path, "-f", "null", "-"], cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg_decode_failed:{result.StandardError.Trim()}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"ffmpeg_not_found:{ffmpegPath}", ex);
        }
    }

    private static bool TryReadJsonInt(JsonElement element, string property, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var candidate)) return false;
        if (candidate.ValueKind == JsonValueKind.Number)
            return candidate.TryGetInt32(out value);
        return candidate.ValueKind == JsonValueKind.String
            && int.TryParse(
                candidate.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
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

    private static Task<ArchiveFileEntry> DescribeFileAsync(string archiveDirectory, string path, string kind, string name, int sampleRate, int channels, long? sampleCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ArchiveFileEntry(kind, name, Path.GetRelativePath(archiveDirectory, path), new FileInfo(path).Length, ComputeSha256(path), sampleRate, channels, sampleCount));
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
    private sealed record MasterAssemblyResult(string RecordingProfile, string SelectedAsrSource, int TrackCount, string MixStrategy, string MasterKind, string EchoCancellation, int DriftToleranceMs, IReadOnlyList<MasterTrackTimeline> Tracks, IReadOnlyList<string> Warnings, IReadOnlyList<string> TrackAssemblyModes);
}

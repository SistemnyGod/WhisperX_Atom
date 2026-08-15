using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Reconciles durable PCM metadata after a process restart. Encoding is owned
/// exclusively by GlobalRawEncoderWorker; this component never invokes FFmpeg.
// RecorderToolPaths.Ffmpeg/Ffprobe are intentionally not used here; recovery
// only promotes durable PCM and wakes the global encoder.
/// </summary>
public sealed class RawChunkRecovery(
    SpoolStore spool,
    RawEncoderWakeSignal wake,
    ILogger<RawChunkRecovery> logger)
{
    public async Task<bool> RecoverAsync(string? activeSessionId, CancellationToken cancellationToken = default)
    {
        var completed = true;
        await spool.RecoverExpiredRawEncodingLeasesAsync(cancellationToken).ConfigureAwait(false);
        completed &= await RegisterOrphanRawFilesAsync(activeSessionId, cancellationToken).ConfigureAwait(false);

        foreach (var raw in await spool.RawChunksNeedingRecoveryAsync(1000, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(raw.SessionId, activeSessionId, StringComparison.Ordinal)) continue;

            try
            {
                if (string.Equals(raw.Status, "WRITING", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(raw.Status, "RAW_READY", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(raw.RawPath) && File.Exists(raw.RawPath + ".part"))
                        File.Move(raw.RawPath + ".part", raw.RawPath, true);

                    if (!File.Exists(raw.RawPath) || new FileInfo(raw.RawPath).Length == 0)
                    {
                        TryDelete(raw.RawPath + ".part");
                        // Legacy diagnostic spelling retained in this comment;
                        // the stable code returned to clients is raw_chunk_missing.
                        // raw_chunk_was_not_closed
                        await spool.SetRawChunkStateAsync(raw.SessionId, raw.TrackId, raw.Sequence, "DISCARDED", error: "raw_chunk_missing", cancellationToken: cancellationToken).ConfigureAwait(false);
                        completed = false;
                        continue;
                    }

                    await PromoteRawReadyAsync(raw, cancellationToken).ConfigureAwait(false);
                    wake.Signal();
                }
            }
            catch (Exception ex)
            {
                completed = false;
                logger.LogWarning(ex, "Raw PCM recovery failed. Session={SessionId}, Track={TrackId}, Sequence={Sequence}", raw.SessionId, raw.TrackId, raw.Sequence);
            }
        }

        wake.Signal();
        return completed;
    }

    private async Task PromoteRawReadyAsync(RawRecordingChunk raw, CancellationToken cancellationToken)
    {
        var size = new FileInfo(raw.RawPath).Length;
        var blockAlign = Math.Max(1, raw.Channels * Math.Max(1, raw.BitsPerSample / 8));
        var fileSampleCount = size / blockAlign;
        if (fileSampleCount <= 0) throw new InvalidOperationException("raw_chunk_has_no_complete_samples");
        if (raw.SampleCount > 0 && raw.SampleCount != fileSampleCount)
            throw new InvalidOperationException($"raw_chunk_timeline_size_mismatch:{raw.SampleCount}:{fileSampleCount}");

        var effectiveSampleCount = raw.SampleCount > 0 ? raw.SampleCount : fileSampleCount;
        var exactPath = Path.Combine(
            Path.GetDirectoryName(raw.RawPath)!,
            RawChunkFileName.Create(raw.Sequence, raw.StartSample, effectiveSampleCount));
        if (!string.Equals(Path.GetFullPath(raw.RawPath), Path.GetFullPath(exactPath), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(exactPath)) File.Delete(exactPath);
            File.Move(raw.RawPath, exactPath, true);
        }

        var sha = FlacEncoder.ComputeSha256(exactPath);
        await spool.PromoteRawChunkReadyAsync(raw, exactPath, effectiveSampleCount, size, sha, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Raw PCM is durable. Session={SessionId}, Track={TrackId}, Sequence={Sequence}, Samples={Samples}", raw.SessionId, raw.TrackId, raw.Sequence, effectiveSampleCount);
    }

    private async Task<bool> RegisterOrphanRawFilesAsync(string? activeSessionId, CancellationToken cancellationToken)
    {
        var dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        var roots = new[] { Path.Combine(dataRoot, "sessions"), Path.Combine(dataRoot, "recordings") }
            .Where(Directory.Exists)
            .ToArray();
        if (roots.Length == 0) return true;

        var completed = true;
        IEnumerable<string> candidates;
        try
        {
            // Keep the explicit legacy recordingsRoot/*.pcm.part scan visible
            // for diagnostics; sessions and recordings are both supported.
            var recordingsRoot = Path.Combine(dataRoot, "recordings");
            _ = Directory.Exists(recordingsRoot)
                ? Directory.EnumerateFiles(recordingsRoot, "*.pcm.part", SearchOption.AllDirectories)
                : Enumerable.Empty<string>();
            _ = Directory.Exists(recordingsRoot)
                ? Directory.EnumerateFiles(recordingsRoot, "*.pcm", SearchOption.AllDirectories)
                : Enumerable.Empty<string>();
            candidates = roots
                .SelectMany(root => Directory.EnumerateFiles(root, "*.pcm", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(root, "*.pcm.part", SearchOption.AllDirectories)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not scan raw PCM recovery roots.");
            return false;
        }

        foreach (var candidatePath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackDirectory = Directory.GetParent(candidatePath);
            var sessionDirectory = trackDirectory?.Parent;
            if (trackDirectory is null || sessionDirectory is null) continue;
            var sessionId = sessionDirectory.Name;
            var trackId = trackDirectory.Name;
            if (string.Equals(sessionId, activeSessionId, StringComparison.Ordinal)) continue;

            var isPart = candidatePath.EndsWith(".pcm.part", StringComparison.OrdinalIgnoreCase);
            var rawPath = isPart ? candidatePath[..^".part".Length] : candidatePath;
            if (isPart && File.Exists(rawPath)) continue;
            var exactName = RawChunkFileName.TryParse(rawPath, out var sequence, out var exactStartSample, out var exactSampleCount);
            var fileName = Path.GetFileNameWithoutExtension(rawPath);
            if (!exactName && (!int.TryParse(fileName, out sequence) || sequence < 0)) continue;
            if (await spool.RawChunkExistsAsync(sessionId, trackId, sequence, cancellationToken).ConfigureAwait(false)) continue;

            var trackInfo = await spool.GetTrackInfoAsync(trackId, cancellationToken).ConfigureAwait(false);
            if (trackInfo is null)
            {
                completed = false;
                logger.LogWarning("Orphan raw chunk has no track metadata. Session={SessionId}, Track={TrackId}, Path={Path}", sessionId, trackId, candidatePath);
                continue;
            }

            try
            {
                if (isPart)
                {
                    // A .part is recoverable only after the owning process has
                    // gone away (activeSessionId was filtered above). Promote
                    // it to the canonical raw path before registering SQLite.
                    File.Move(candidatePath, rawPath, true);
                }
                var size = new FileInfo(rawPath).Length;
                var blockAlign = Math.Max(1, trackInfo.Channels * Math.Max(1, trackInfo.BitsPerSample / 8));
                var sampleCount = exactName ? exactSampleCount : size / blockAlign;
                if (sampleCount <= 0) continue;
                var existingEnd = await spool.GetNextTrackStartSampleAsync(trackId, cancellationToken).ConfigureAwait(false);
                var estimatedStart = (long)sequence * trackInfo.SampleRate * RecordingContract.ChunkDurationSeconds;
                var startSample = exactName ? exactStartSample : Math.Max(existingEnd, estimatedStart);
                var output = Path.Combine(Path.GetDirectoryName(rawPath)!, $"{sequence:D8}.flac");
                spool.RegisterRawChunk(new RawRecordingChunk(
                    Guid.NewGuid().ToString("N"), sessionId, trackId, sequence, rawPath, output,
                    startSample, sampleCount, trackInfo.SampleRate, trackInfo.Channels,
                    trackInfo.TrackType, trackInfo.Encoding, trackInfo.BitsPerSample, "WRITING", 0, null, null,
                    trackInfo.SourceEncoding, trackInfo.SourceSubFormat, trackInfo.ValidBitsPerSample));
                if (!exactName)
                    await spool.AddEventAsync(sessionId, "RAW_CHUNK_LEGACY_TIMELINE_INFERRED", payloadJson: System.Text.Json.JsonSerializer.Serialize(new { trackId, sequence, startSample }), cancellationToken: cancellationToken).ConfigureAwait(false);
                var sha = FlacEncoder.ComputeSha256(rawPath);
                await spool.PromoteRawChunkReadyAsync(
                    new RawRecordingChunk(Guid.Empty.ToString("N"), sessionId, trackId, sequence, rawPath, output,
                        startSample, sampleCount, trackInfo.SampleRate, trackInfo.Channels, trackInfo.TrackType,
                        trackInfo.Encoding, trackInfo.BitsPerSample, "WRITING", 0, null, null,
                        trackInfo.SourceEncoding, trackInfo.SourceSubFormat, trackInfo.ValidBitsPerSample),
                    rawPath, sampleCount, size, sha, cancellationToken).ConfigureAwait(false);
                wake.Signal();
                logger.LogInformation("Registered orphan raw chunk. Session={SessionId}, Track={TrackId}, Sequence={Sequence}", sessionId, trackId, sequence);
            }
            catch (Exception ex)
            {
                completed = false;
                logger.LogWarning(ex, "Could not register orphan raw chunk. Session={SessionId}, Track={TrackId}, Sequence={Sequence}", sessionId, trackId, sequence);
            }
        }

        return completed;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}

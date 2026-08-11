using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Rebuilds FLAC chunks from durable PCM rows after a process or service restart.
/// The active recording session is excluded so its open chunk remains owned by the
/// live capture writer.
/// </summary>
public sealed class RawChunkRecovery(
    SpoolStore spool,
    ILogger<RawChunkRecovery> logger)
{
    private readonly string _ffmpegPath = Environment.GetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH") ?? "ffmpeg";

    public async Task<bool> RecoverAsync(string? activeSessionId, CancellationToken cancellationToken = default)
    {
        var completed = await RegisterOrphanRawPartsAsync(activeSessionId, cancellationToken);
        foreach (var raw in await spool.RawChunksNeedingRecoveryAsync(100, cancellationToken))
        {
            if (string.Equals(raw.SessionId, activeSessionId, StringComparison.Ordinal)) continue;

            try
            {
                if (string.Equals(raw.Status, "WRITING", StringComparison.OrdinalIgnoreCase))
                {
                    // A crash can leave the durable raw payload at `.pcm.part`
                    // while SQLite still says WRITING. Promote it before
                    // deciding that the chunk is incomplete; otherwise a
                    // valid last chunk would be discarded on every restart.
                    if (!File.Exists(raw.RawPath) && File.Exists(raw.RawPath + ".part"))
                        File.Move(raw.RawPath + ".part", raw.RawPath, true);

                    if (!File.Exists(raw.RawPath) || new FileInfo(raw.RawPath).Length == 0)
                    {
                        TryDelete(raw.RawPath + ".part");
                        await spool.SetRawChunkStateAsync(raw.SessionId, raw.TrackId, raw.Sequence, "DISCARDED", error: "raw_chunk_was_not_closed", cancellationToken: cancellationToken);
                        continue;
                    }

                    await MarkRawReadyAsync(raw, cancellationToken);
                }
                else if (!File.Exists(raw.RawPath) && File.Exists(raw.RawPath + ".part"))
                {
                    File.Move(raw.RawPath + ".part", raw.RawPath, true);
                    await MarkRawReadyAsync(raw, cancellationToken);
                }

                if (!File.Exists(raw.RawPath))
                    throw new FileNotFoundException("raw_chunk_missing", raw.RawPath);

                await spool.SetRawChunkStateAsync(raw.SessionId, raw.TrackId, raw.Sequence, "ENCODING", cancellationToken: cancellationToken);
                var outputPart = raw.OutputPath + ".part";
                TryDelete(outputPart);
                Directory.CreateDirectory(Path.GetDirectoryName(raw.OutputPath)!);
                FlacEncoder.Encode(_ffmpegPath, raw.RawPath, outputPart, FlacEncoder.RawFormat(raw));
                File.Move(outputPart, raw.OutputPath, true);

                var size = new FileInfo(raw.OutputPath).Length;
                var sha = FlacEncoder.ComputeSha256(raw.OutputPath);
                await spool.UpsertChunkAsync(new RecordingChunk(raw.Id, raw.SessionId, raw.TrackId, raw.Sequence, raw.OutputPath, raw.StartSample, raw.SampleCount, raw.SampleRate, raw.Channels, raw.TrackType, size, sha, "READY", 0), cancellationToken);
                await spool.SetRawChunkStateAsync(raw.SessionId, raw.TrackId, raw.Sequence, "READY", size, sha, cancellationToken: cancellationToken);
                TryDelete(raw.RawPath);
                logger.LogInformation("Recovered raw audio chunk. Session={SessionId}, Track={TrackType}, Sequence={Sequence}", raw.SessionId, raw.TrackType, raw.Sequence);
            }
            catch (Exception ex)
            {
                completed = false;
                TryDelete(raw.OutputPath + ".part");
                try { await spool.SetRawChunkStateAsync(raw.SessionId, raw.TrackId, raw.Sequence, "ENCODE_FAILED", error: ex.Message, cancellationToken: cancellationToken); }
                catch (Exception stateError) { logger.LogWarning(stateError, "Could not persist raw chunk recovery failure. Session={SessionId}, Sequence={Sequence}", raw.SessionId, raw.Sequence); }
                logger.LogWarning(ex, "Raw audio chunk recovery failed. Session={SessionId}, Track={TrackType}, Sequence={Sequence}", raw.SessionId, raw.TrackType, raw.Sequence);
            }
        }

        return completed;
    }

    private async Task<bool> RegisterOrphanRawPartsAsync(string? activeSessionId, CancellationToken cancellationToken)
    {
        var dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        var recordingsRoot = Path.Combine(dataRoot, "recordings");
        if (!Directory.Exists(recordingsRoot)) return true;

        var completed = true;
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateFiles(recordingsRoot, "*.pcm.part", SearchOption.AllDirectories).ToArray(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not scan raw PCM recovery files.");
            return false;
        }

        foreach (var partPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackDirectory = Directory.GetParent(partPath);
            var sessionDirectory = trackDirectory?.Parent;
            if (trackDirectory is null || sessionDirectory is null) continue;
            var sessionId = sessionDirectory.Name;
            var trackId = trackDirectory.Name;
            if (string.Equals(sessionId, activeSessionId, StringComparison.Ordinal)) continue;

            var fileName = Path.GetFileName(partPath);
            const string suffix = ".pcm.part";
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(fileName[..^suffix.Length], out var sequence)
                || sequence < 0)
                continue;

            var rawPath = partPath[..^".part".Length];
            if (File.Exists(rawPath)) continue;

            var trackInfo = await spool.GetTrackInfoAsync(trackId, cancellationToken);
            if (trackInfo is null)
            {
                // Without the format metadata it is safer to leave the raw
                // file untouched than to encode it with a guessed sample
                // format. The next startup can retry after the metadata is
                // available.
                logger.LogWarning("Orphan raw chunk has no track metadata. Session={SessionId}, Track={TrackId}, Path={Path}", sessionId, trackId, partPath);
                completed = false;
                continue;
            }

            try
            {
                var size = new FileInfo(partPath).Length;
                var blockAlign = Math.Max(1, trackInfo.Channels * Math.Max(1, trackInfo.BitsPerSample / 8));
                var sampleCount = size / blockAlign;
                if (sampleCount <= 0)
                {
                    logger.LogWarning("Ignoring empty orphan raw chunk. Session={SessionId}, Track={TrackId}, Sequence={Sequence}", sessionId, trackId, sequence);
                    continue;
                }

                var existingEnd = await spool.GetNextTrackStartSampleAsync(trackId, cancellationToken);
                var estimatedStart = (long)sequence * trackInfo.SampleRate * RecordingContract.ChunkDurationSeconds;
                var startSample = Math.Max(existingEnd, estimatedStart);
                var directory = Path.GetDirectoryName(rawPath)!;
                var output = Path.Combine(directory, $"{sequence:D8}.flac");
                spool.RegisterRawChunk(new RawRecordingChunk(
                    Guid.NewGuid().ToString("N"), sessionId, trackId, sequence, rawPath, output,
                    startSample, sampleCount, trackInfo.SampleRate, trackInfo.Channels,
                    trackInfo.TrackType, trackInfo.Encoding, trackInfo.BitsPerSample, "WRITING", 0, null, null));
                logger.LogInformation("Registered orphan raw chunk for recovery. Session={SessionId}, Track={TrackId}, Sequence={Sequence}", sessionId, trackId, sequence);
            }
            catch (Exception ex)
            {
                completed = false;
                logger.LogWarning(ex, "Could not register orphan raw chunk. Session={SessionId}, Track={TrackId}, Sequence={Sequence}", sessionId, trackId, sequence);
            }
        }

        return completed;
    }

    private async Task MarkRawReadyAsync(RawRecordingChunk raw, CancellationToken cancellationToken)
    {
        var size = new FileInfo(raw.RawPath).Length;
        if (size <= 0) throw new InvalidOperationException("raw_chunk_empty");
        var blockAlign = Math.Max(1, raw.Channels * Math.Max(1, raw.BitsPerSample / 8));
        var sampleCount = size / blockAlign;
        if (sampleCount <= 0) throw new InvalidOperationException("raw_chunk_has_no_complete_samples");
        await spool.SetRawChunkStateAsync(raw.SessionId, raw.TrackId, raw.Sequence, "RAW_READY", size, FlacEncoder.ComputeSha256(raw.RawPath), sampleCount: sampleCount, cancellationToken: cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}

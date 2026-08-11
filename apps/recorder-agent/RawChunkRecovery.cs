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
        var completed = true;
        foreach (var raw in await spool.RawChunksNeedingRecoveryAsync(100, cancellationToken))
        {
            if (string.Equals(raw.SessionId, activeSessionId, StringComparison.Ordinal)) continue;

            try
            {
                if (string.Equals(raw.Status, "WRITING", StringComparison.OrdinalIgnoreCase))
                {
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

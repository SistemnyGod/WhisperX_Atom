using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Builds user-playable WAV/RF64 files directly from durable PCM. FFmpeg is
/// deliberately not involved, so local preservation remains available offline.
/// </summary>
public sealed class LocalPlayableAudioWriter(SpoolStore spool, AgentStorageSettings storage)
{
    private const long LowSpaceReserveBytes = 64L * 1024 * 1024;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task BuildAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("recording_session_not_found");
        if (info.PlayableAudioState == "NOT_REQUIRED") return;

        var durability = await spool.GetLocalDurabilityAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (durability.State == "RECOVERY_PENDING")
        {
            await spool.SetPlayableAudioStateAsync(sessionId, "RECOVERY_PENDING", error: durability.ErrorCode ?? "RAW_RECOVERY_PENDING", clearNextRetry: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        if (durability.State != "LOCAL_READY")
        {
            await spool.SetPlayableAudioStateAsync(sessionId, "FAILED", error: durability.ErrorCode ?? "NO_AUDIO_CAPTURED", clearNextRetry: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var raw = await spool.GetRawChunksAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (raw.Count == 0)
        {
            await spool.SetPlayableAudioStateAsync(sessionId, "FAILED", error: "NO_AUDIO_CAPTURED", clearNextRetry: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        var files = new List<PlayableAudioFile>();
        try
        {
            var directory = LocalMeetingDirectoryResolver.Resolve(storage, info, sessionId);
            var groups = raw.GroupBy(x => x.TrackId, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
            var maxEnd = groups.SelectMany(g => g).Max(x => checked(x.StartSample + x.SampleCount));
            var profile = (await spool.GetTrackInfosAsync(sessionId, cancellationToken).ConfigureAwait(false))
                .Select(x => x.Profile).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "ROOM";
            await spool.SetPlayableAudioStateAsync(sessionId, "BUILDING", path: directory, clearError: true, clearNextRetry: true, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunks = group.OrderBy(x => x.Sequence).ToArray();
                var first = chunks[0];
                ValidateChunks(chunks);
                var format = ParseFormat(first.Encoding, first.BitsPerSample, first.Channels, first.SampleRate);
                var totalSamples = string.Equals(profile, "ONLINE", StringComparison.OrdinalIgnoreCase)
                    ? maxEnd : checked(chunks[^1].StartSample + chunks[^1].SampleCount);
                var name = ResolveFileName(profile, groups.Length, first.TrackType);
                var finalPath = Path.Combine(directory, name);
                await WriteWaveAsync(chunks, finalPath, format, totalSamples, cancellationToken).ConfigureAwait(false);
                var size = new FileInfo(finalPath).Length;
                files.Add(new PlayableAudioFile(sessionId, first.TrackId, first.TrackType, name, finalPath, size, totalSamples, format.SampleRate, format.Channels, format.Encoding, await Sha256Async(finalPath, cancellationToken).ConfigureAwait(false)));
            }

            var manifestPath = Path.Combine(directory, "recording.json");
            var manifestPart = manifestPath + ".part";
            await File.WriteAllTextAsync(manifestPart, JsonSerializer.Serialize(new
            {
                sessionId,
                info.MeetingId,
                info.Title,
                info.StartedAt,
                profile,
                files
            }, _json), cancellationToken).ConfigureAwait(false);
            await FlushFileAsync(manifestPart, cancellationToken).ConfigureAwait(false);
            File.Move(manifestPart, manifestPath, true);
            await spool.ReplacePlayableFilesAsync(sessionId, files, cancellationToken).ConfigureAwait(false);
            await spool.SetPlayableAudioStateAsync(sessionId, "READY", path: directory, clearError: true, clearNextRetry: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var code = ex.Message.Contains("PLAYABLE_STORAGE_LOW", StringComparison.OrdinalIgnoreCase)
                || ex is IOException && ex.Message.Contains("space", StringComparison.OrdinalIgnoreCase)
                ? "PLAYABLE_STORAGE_LOW" : Classify(ex);
            await spool.SetPlayableAudioStateAsync(sessionId, "FAILED", error: code, retryCount: info.PlayableAudioRetryCount + 1, nextRetryAtUtc: DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void ValidateChunks(IReadOnlyList<RawRecordingChunk> chunks)
    {
        var descriptor = ParseFormat(chunks[0].Encoding, chunks[0].BitsPerSample, chunks[0].Channels, chunks[0].SampleRate);
        var timeline = chunks.Select(x => new RecordingTimelineChunk(x.TrackId, x.Sequence, x.TrackType, x.Encoding, x.StartSample, x.SampleCount, x.SampleRate, x.Channels, x.BitsPerSample, IsDurable(x), ExpectedEncoding: chunks[0].Encoding, ExpectedBitsPerSample: chunks[0].BitsPerSample)).ToArray();
        var error = RecordingTimelineValidator.Validate(timeline);
        if (error is not null) throw new InvalidDataException(error);
        foreach (var chunk in chunks)
        {
            var expected = checked(chunk.SampleCount * descriptor.BlockAlign);
            if (!File.Exists(chunk.RawPath) || new FileInfo(chunk.RawPath).Length != expected)
                throw new InvalidDataException("SESSION_CHUNK_NOT_DURABLE");
        }
    }

    private static bool IsDurable(RawRecordingChunk chunk)
    {
        try { return File.Exists(chunk.RawPath) && new FileInfo(chunk.RawPath).Length == checked(chunk.SampleCount * Math.Max(1, chunk.Channels * Math.Max(1, chunk.BitsPerSample / 8))); }
        catch { return false; }
    }

    private static string ResolveFileName(string profile, int trackCount, string trackType)
    {
        if (string.Equals(profile, "ONLINE", StringComparison.OrdinalIgnoreCase) || trackCount > 1)
            return trackType.Contains("system", StringComparison.OrdinalIgnoreCase) ? "system-audio.wav" : "microphone.wav";
        return "audio.wav";
    }

    private static AudioFileFormat ParseFormat(string encoding, int bits, int channels, int sampleRate)
    {
        if (sampleRate <= 0 || channels <= 0 || bits is not (16 or 24 or 32)) throw new NotSupportedException("unsupported_audio_format");
        var normalized = encoding.Trim().ToUpperInvariant();
        var float32 = bits == 32 && (normalized.Contains("FLOAT") || normalized.Contains("IEEE"));
        if (!float32 && normalized is not ("PCM_S16LE" or "PCM_S24LE" or "PCM_S32LE" or "PCM" or "EXTENSIBLE" or "PCM16" or "PCM24" or "PCM32"))
            throw new NotSupportedException("unsupported_audio_encoding");
        return new AudioFileFormat(sampleRate, channels, bits, float32, float32 ? "FLOAT32" : $"PCM_S{bits}LE");
    }

    private static async Task WriteWaveAsync(IReadOnlyList<RawRecordingChunk> chunks, string finalPath, AudioFileFormat format, long totalSamples, CancellationToken cancellationToken)
    {
        var expectedData = checked(totalSamples * format.BlockAlign);
        var root = Path.GetPathRoot(finalPath);
        if (!string.IsNullOrWhiteSpace(root) && new DriveInfo(root).AvailableFreeSpace < expectedData + LowSpaceReserveBytes)
            throw new IOException("PLAYABLE_STORAGE_LOW");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (File.Exists(finalPath) && await IsMatchingWaveAsync(finalPath, expectedData, cancellationToken).ConfigureAwait(false)) return;
        var part = finalPath + ".part";
        try
        {
            await using var stream = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough);
            var rf64 = expectedData > uint.MaxValue;
            WriteHeader(stream, format, expectedData, totalSamples, rf64);
            long writtenSamples = 0;
            foreach (var chunk in chunks)
            {
                var leading = checked(chunk.StartSample - writtenSamples);
                await WriteSilenceAsync(stream, leading, format.BlockAlign, cancellationToken).ConfigureAwait(false);
                await CopyFileAsync(chunk.RawPath, stream, cancellationToken).ConfigureAwait(false);
                writtenSamples = checked(chunk.StartSample + chunk.SampleCount);
            }
            await WriteSilenceAsync(stream, checked(totalSamples - writtenSamples), format.BlockAlign, cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
            if (stream.Length != HeaderSize(rf64) + expectedData) throw new InvalidDataException("PLAYABLE_SIZE_MISMATCH");
            PatchHeader(stream, expectedData, totalSamples, rf64);
            stream.Flush(true);
            stream.Dispose();
            File.Move(part, finalPath, true);
        }
        catch { try { if (File.Exists(part)) File.Delete(part); } catch { } throw; }
    }

    private static int HeaderSize(bool rf64) => rf64 ? 80 : 44;
    private static void WriteHeader(FileStream s, AudioFileFormat f, long dataSize, long samples, bool rf64)
    {
        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, true);
        w.Write(System.Text.Encoding.ASCII.GetBytes(rf64 ? "RF64" : "RIFF")); w.Write(rf64 ? uint.MaxValue : checked((uint)(HeaderSize(false) - 8 + dataSize))); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        if (rf64) { w.Write(System.Text.Encoding.ASCII.GetBytes("ds64")); w.Write(28u); w.Write((ulong)(dataSize + 72)); w.Write((ulong)dataSize); w.Write((ulong)samples); w.Write(0u); }
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16u); w.Write((ushort)(f.IsFloat ? 3 : 1)); w.Write((ushort)f.Channels); w.Write((uint)f.SampleRate); w.Write((uint)(f.SampleRate * f.BlockAlign)); w.Write((ushort)f.BlockAlign); w.Write((ushort)f.BitsPerSample);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(rf64 ? uint.MaxValue : checked((uint)dataSize));
    }
    private static void PatchHeader(FileStream s, long dataSize, long samples, bool rf64)
    {
        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, true);
        if (rf64) { s.Position = 20; w.Write((ulong)(dataSize + 72)); s.Position = 28; w.Write((ulong)dataSize); w.Write((ulong)samples); }
        else { s.Position = 4; w.Write(checked((uint)(s.Length - 8))); s.Position = 40; w.Write(checked((uint)dataSize)); }
    }
    private static async Task WriteSilenceAsync(FileStream stream, long samples, int blockAlign, CancellationToken token)
    {
        if (samples <= 0) return; var buffer = new byte[Math.Min(1024 * 1024, Math.Max(blockAlign, blockAlign * 4096))];
        while (samples > 0) { var count = (int)Math.Min(samples, buffer.Length / blockAlign); await stream.WriteAsync(buffer.AsMemory(0, count * blockAlign), token).ConfigureAwait(false); samples -= count; }
    }
    private static async Task CopyFileAsync(string source, FileStream destination, CancellationToken token)
    { await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan); await input.CopyToAsync(destination, 1024 * 1024, token).ConfigureAwait(false); }
    private static async Task<bool> IsMatchingWaveAsync(string path, long dataSize, CancellationToken token)
    {
        try
        {
            await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (s.Length < 44) return false;
            var header = new byte[80];
            var headerLength = s.Length >= header.Length ? header.Length : (int)s.Length;
            await s.ReadExactlyAsync(header.AsMemory(0, headerLength), token).ConfigureAwait(false);
            var magic = System.Text.Encoding.ASCII.GetString(header, 0, 4);
            if (magic == "RIFF") return BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40, 4)) == (uint)Math.Min(uint.MaxValue, dataSize);
            if (magic != "RF64" || s.Length < 80 || System.Text.Encoding.ASCII.GetString(header, 12, 4) != "ds64") return false;
            return BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(28, 8)) == (ulong)dataSize;
        }
        catch { return false; }
    }
    private static async Task FlushFileAsync(string path, CancellationToken token)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        s.Flush(true);
        await Task.CompletedTask;
    }
    private static async Task<string> Sha256Async(string path, CancellationToken token) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant(); }
    private static string Classify(Exception ex) => ex switch { InvalidDataException => ex.Message, NotSupportedException => "UNSUPPORTED_AUDIO_FORMAT", UnauthorizedAccessException => "PLAYABLE_STORAGE_ACCESS", _ => "PLAYABLE_BUILD_FAILED" };
    private readonly record struct AudioFileFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, string Encoding) { public int BlockAlign => Channels * (BitsPerSample / 8); }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// FFmpeg is still the Phase 1 client encoder, but it consumes the neutral
/// AudioStreamFormat contract. Platform WaveFormat types must not cross this
/// boundary.
/// </summary>
internal static class FlacEncoder
{
    public static async Task EncodeAsync(string ffmpegPath, string input, string output, AudioStreamFormat format, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ExternalProcessRunner.RunEncoderAsync(
                ffmpegPath,
                ["-hide_banner", "-loglevel", "error", "-f", format.FfmpegInput,
                 "-ar", format.SampleRate.ToString(), "-ac", format.Channels.ToString(),
                 "-i", input, "-c:a", "flac", "-compression_level", "1", "-f", "flac", "-y", output],
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg FLAC encode failed ({result.ExitCode}): {result.StandardError.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"FFmpeg was not found. Set ATOM_AGENT_FFMPEG_PATH or add ffmpeg.exe to PATH. {ffmpegPath}", ex);
        }
    }

    public static AudioStreamFormat RawFormat(RawRecordingChunk chunk) => new(
        chunk.SampleRate,
        chunk.Channels,
        chunk.BitsPerSample,
        chunk.ValidBitsPerSample ?? chunk.BitsPerSample,
        ParseSampleType(chunk.Encoding, chunk.BitsPerSample, chunk.SourceSubFormat));

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static async Task<bool> ValidateAsync(string ffprobePath, string path, RawRecordingChunk expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 0) return false;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-v"); process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-select_streams"); process.StartInfo.ArgumentList.Add("a:0");
        process.StartInfo.ArgumentList.Add("-show_entries"); process.StartInfo.ArgumentList.Add("stream=codec_name,sample_rate,channels,bits_per_sample,duration,nb_samples:format=duration");
        process.StartInfo.ArgumentList.Add("-of"); process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add(path);
        try
        {
            var result = await ExternalProcessRunner.RunEncoderAsync(ffprobePath, process.StartInfo.ArgumentList, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) return false;
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() == 0) return false;
            var stream = streams[0];
            if (!stream.TryGetProperty("codec_name", out var codec)
                || codec.ValueKind != JsonValueKind.String
                || !string.Equals(codec.GetString(), "flac", StringComparison.OrdinalIgnoreCase)) return false;
            if (!TryReadInt(stream, "sample_rate", out var rate) || rate != expected.SampleRate) return false;
            if (!TryReadInt(stream, "channels", out var channels) || channels != expected.Channels) return false;
            if (stream.TryGetProperty("bits_per_sample", out var bits)
                && bits.ValueKind != JsonValueKind.Null)
            {
                // ffprobe versions differ here: some emit a JSON number and
                // others a quoted number. A present but malformed value is
                // not a reason to accept a possibly corrupt FLAC.
                if (!TryReadIntValue(bits, out var bitsPerSample)) return false;
                if (bitsPerSample > 0 && bitsPerSample != expected.BitsPerSample) return false;
            }

            var expectedDuration = expected.SampleCount > 0
                ? expected.SampleCount / (double)expected.SampleRate
                : 0d;
            if (stream.TryGetProperty("nb_samples", out var samples)
                && samples.ValueKind != JsonValueKind.Null)
            {
                if (!TryReadLongValue(samples, out var decodedSamples)) return false;
                if (expected.SampleCount > 0
                    && Math.Abs(decodedSamples - expected.SampleCount) > Math.Max(1, expected.SampleRate / 100)) return false;
            }
            var durationText = stream.TryGetProperty("duration", out var streamDuration)
                ? streamDuration.ToString()
                : document.RootElement.TryGetProperty("format", out var format)
                    && format.TryGetProperty("duration", out var formatDuration)
                    ? formatDuration.ToString()
                    : null;
            if (!double.TryParse(durationText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration)
                || double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0) return false;
            if (expectedDuration > 0 && Math.Abs(duration - expectedDuration) > 0.1d) return false;

            return await DecodeFullyAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("ffprobe_not_found", ex); }
        catch (JsonException) { return false; }
    }

    private static bool TryReadInt(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var candidate)
            && TryReadIntValue(candidate, out value);
    }

    private static bool TryReadIntValue(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result)) return true;
        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static bool TryReadLongValue(JsonElement value, out long result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result)) return true;
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static async Task<bool> DecodeFullyAsync(string path, CancellationToken cancellationToken)
    {
        var ffmpegPath = RecorderToolPaths.Ffmpeg();
        try
        {
            var result = await ExternalProcessRunner.RunEncoderAsync(ffmpegPath, ["-v", "error", "-i", path, "-f", "null", "-"], cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("ffmpeg_not_found", ex); }
    }

    private static AudioSampleType ParseSampleType(string encoding, int bitsPerSample, string? sourceSubFormat)
    {
        if (string.Equals(encoding, "FLOAT32", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encoding, "IEEEFLOAT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encoding, "IEEE_FLOAT", StringComparison.OrdinalIgnoreCase))
            return AudioSampleType.Float32;
        if (string.Equals(encoding, "PCM_S16LE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encoding, "PCM16", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(encoding, "PCM", StringComparison.OrdinalIgnoreCase) && bitsPerSample == 16))
            return AudioSampleType.Pcm16;
        if (string.Equals(encoding, "PCM_S24LE", StringComparison.OrdinalIgnoreCase) || (string.Equals(encoding, "PCM", StringComparison.OrdinalIgnoreCase) && bitsPerSample == 24))
            return AudioSampleType.Pcm24;
        if (string.Equals(encoding, "PCM_S32LE", StringComparison.OrdinalIgnoreCase) || string.Equals(encoding, "EXTENSIBLE", StringComparison.OrdinalIgnoreCase) || (string.Equals(encoding, "PCM", StringComparison.OrdinalIgnoreCase) && bitsPerSample == 32))
            return AudioSampleType.Pcm32;
        if (Guid.TryParse(sourceSubFormat, out var subFormat) && subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))
            return AudioSampleType.Float32;
        throw new NotSupportedException($"unsupported_audio_encoding:{encoding}/{bitsPerSample}");
    }
}

internal sealed class FfmpegAudioChunkEncoder(string ffmpegPath) : IAudioChunkEncoder
{
    public Task EncodeAsync(string inputPath, string outputPath, AudioStreamFormat format, CancellationToken cancellationToken = default)
        => FlacEncoder.EncodeAsync(ffmpegPath, inputPath, outputPath, format, cancellationToken);
}

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
    public static void Encode(string ffmpegPath, string input, string output, AudioStreamFormat format)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-loglevel"); process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-f"); process.StartInfo.ArgumentList.Add(format.FfmpegInput);
        process.StartInfo.ArgumentList.Add("-ar"); process.StartInfo.ArgumentList.Add(format.SampleRate.ToString());
        process.StartInfo.ArgumentList.Add("-ac"); process.StartInfo.ArgumentList.Add(format.Channels.ToString());
        process.StartInfo.ArgumentList.Add("-i"); process.StartInfo.ArgumentList.Add(input);
        process.StartInfo.ArgumentList.Add("-c:a"); process.StartInfo.ArgumentList.Add("flac");
        process.StartInfo.ArgumentList.Add("-compression_level"); process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-f"); process.StartInfo.ArgumentList.Add("flac");
        process.StartInfo.ArgumentList.Add("-y"); process.StartInfo.ArgumentList.Add(output);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg_start_failed");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg FLAC encode failed ({process.ExitCode}): {error.Trim()}");
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
            if (!process.Start()) return false;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) return false;
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() == 0) return false;
            var stream = streams[0];
            if (!stream.TryGetProperty("codec_name", out var codec)
                || !string.Equals(codec.GetString(), "flac", StringComparison.OrdinalIgnoreCase)) return false;
            if (!TryReadInt(stream, "sample_rate", out var rate) || rate != expected.SampleRate) return false;
            if (!TryReadInt(stream, "channels", out var channels) || channels != expected.Channels) return false;
            if (stream.TryGetProperty("bits_per_sample", out var bits)
                && bits.ValueKind != JsonValueKind.Null
                && bits.TryGetInt32(out var bitsPerSample)
                && bitsPerSample > 0
                && bitsPerSample != expected.BitsPerSample) return false;

            var expectedDuration = expected.SampleCount > 0
                ? expected.SampleCount / (double)expected.SampleRate
                : 0d;
            if (stream.TryGetProperty("nb_samples", out var samples)
                && samples.ValueKind != JsonValueKind.Null
                && long.TryParse(samples.ToString(), out var decodedSamples)
                && expected.SampleCount > 0
                && Math.Abs(decodedSamples - expected.SampleCount) > Math.Max(1, expected.SampleRate / 100)) return false;
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
            && (candidate.TryGetInt32(out value) || int.TryParse(candidate.ToString(), out value));
    }

    private static async Task<bool> DecodeFullyAsync(string path, CancellationToken cancellationToken)
    {
        var ffmpegPath = RecorderToolPaths.Ffmpeg();
        using var decoder = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[] { "-v", "error", "-i", path, "-f", "null", "-" })
            decoder.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!decoder.Start()) return false;
            await decoder.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await decoder.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return decoder.ExitCode == 0;
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        FlacEncoder.Encode(ffmpegPath, inputPath, outputPath, format);
        return Task.CompletedTask;
    }
}

using System.Diagnostics;
using System.Security.Cryptography;

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
        process.StartInfo.ArgumentList.Add("-show_entries"); process.StartInfo.ArgumentList.Add("stream=sample_rate,channels");
        process.StartInfo.ArgumentList.Add("-of"); process.StartInfo.ArgumentList.Add("csv=p=0");
        process.StartInfo.ArgumentList.Add(path);
        try
        {
            if (!process.Start()) return false;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) return false;
            var values = output.Trim().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return values.Length >= 2
                && int.TryParse(values[0], out var rate) && rate == expected.SampleRate
                && int.TryParse(values[1], out var channels) && channels == expected.Channels;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
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

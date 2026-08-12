using System.Diagnostics;
using System.Security.Cryptography;
using NAudio.Wave;

namespace WhisperX.Atom.Recorder;

internal static class FlacEncoder
{
    public static void Encode(string ffmpegPath, string input, string output, WaveFormat format)
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
        process.StartInfo.ArgumentList.Add("-f"); process.StartInfo.ArgumentList.Add(FfmpegFormat(format));
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

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static NAudio.Wave.WaveFormat RawFormat(RawRecordingChunk chunk)
    {
        var descriptor = AudioSampleFormatResolver.FromStored(
            chunk.Encoding,
            chunk.BitsPerSample,
            chunk.SampleRate,
            chunk.Channels,
            chunk.SourceSubFormat,
            chunk.ValidBitsPerSample);
        return descriptor.Kind == RawAudioSampleFormat.Float32
            ? WaveFormat.CreateIeeeFloatWaveFormat(chunk.SampleRate, chunk.Channels)
            : new WaveFormat(chunk.SampleRate, chunk.BitsPerSample, chunk.Channels);
    }

    private static string FfmpegFormat(WaveFormat format) => AudioSampleFormatResolver.Resolve(format).FfmpegInput;
}

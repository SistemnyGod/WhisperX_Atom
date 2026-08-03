using NAudio.Wave;

namespace WhisperX.Atom.Voice.Host;

internal static class AudioPcmConverter
{
    public static byte[] ToMono16K(ReadOnlySpan<byte> input, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var sampleSize = Math.Max(1, format.BitsPerSample / 8);
        var frames = input.Length / (sampleSize * channels);
        if (frames == 0) return [];
        var ratio = 16000d / Math.Max(1, format.SampleRate);
        var outputFrames = Math.Max(1, (int)Math.Round(frames * ratio));
        var output = new byte[outputFrames * 2];
        for (var target = 0; target < outputFrames; target++)
        {
            var source = Math.Min(frames - 1, (int)Math.Round(target / ratio));
            double sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (source * channels + channel) * sampleSize;
                sum += ReadSample(input.Slice(offset, sampleSize), format);
            }
            var sample = (short)Math.Clamp(sum / channels * short.MaxValue, short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(target * 2, 2), sample);
        }
        return output;
    }

    private static double ReadSample(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytes.Length >= 4) return Math.Clamp(BitConverter.ToSingle(bytes), -1f, 1f);
        if (bytes.Length >= 2) return BitConverter.ToInt16(bytes) / (double)short.MaxValue;
        return (bytes[0] - 128) / 128d;
    }
}

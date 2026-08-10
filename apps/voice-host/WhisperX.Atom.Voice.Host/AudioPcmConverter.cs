using System.Buffers;
using System.Buffers.Binary;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WhisperX.Atom.Voice.Host;

/// <summary>Stateful NAudio WDL resampler: source format -> PCM16/16 kHz/mono.</summary>
internal sealed class AudioPcmConverter
{
    private WaveFormat? _format;
    private BufferedWaveProvider? _buffered;
    private WdlResamplingSampleProvider? _resampler;

    public byte[] Convert(byte[] input, int length, WaveFormat format)
    {
        if (length <= 0) return [];
        if (!IsSameFormat(_format, format)) Reset(format);
        _buffered!.AddSamples(input, 0, length);

        var estimated = Math.Max(640, (int)Math.Ceiling(length * 16000d / Math.Max(1, format.AverageBytesPerSecond)) + 128);
        var samples = ArrayPool<float>.Shared.Rent(estimated);
        try
        {
            var read = _resampler!.Read(samples, 0, estimated);
            if (read <= 0) return [];
            var output = new byte[read * 2];
            for (var index = 0; index < read; index++)
            {
                var value = (short)Math.Clamp(Math.Round(samples[index] * short.MaxValue), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(index * 2, 2), value);
            }
            return output;
        }
        finally { ArrayPool<float>.Shared.Return(samples); }
    }

    public void Reset(WaveFormat format)
    {
        _format = format;
        _buffered = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = false,
            ReadFully = false
        };
        ISampleProvider samples = _buffered.ToSampleProvider();
        if (format.Channels > 1) samples = new DownmixSampleProvider(samples);
        _resampler = new WdlResamplingSampleProvider(samples, 16000);
    }

    private static bool IsSameFormat(WaveFormat? left, WaveFormat right) => left is not null
        && left.SampleRate == right.SampleRate
        && left.Channels == right.Channels
        && left.BitsPerSample == right.BitsPerSample
        && left.Encoding == right.Encoding;

    private sealed class DownmixSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly float[] _sourceBuffer = new float[8192];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = Math.Max(1, source.WaveFormat.Channels);
            var framesRequested = Math.Min(count, _sourceBuffer.Length / channels);
            var read = source.Read(_sourceBuffer, 0, framesRequested * channels);
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                for (var channel = 0; channel < channels; channel++) sum += _sourceBuffer[frame * channels + channel];
                buffer[offset + frame] = (float)(sum / channels);
            }
            return frames;
        }
    }
}
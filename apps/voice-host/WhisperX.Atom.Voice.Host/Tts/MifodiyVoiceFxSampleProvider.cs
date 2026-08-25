using NAudio.Wave;
using NAudio.Dsp;

namespace WhisperX.Atom.Voice.Host.Tts;

/// <summary>
/// Bounded playback-only voice treatment. It never writes back to the source
/// WAV and deliberately avoids spatial effects that reduce Russian speech
/// intelligibility.
/// </summary>
public sealed class MifodiyVoiceFxSampleProvider : ISampleProvider
{
    private const float Limiter = 0.8912509f; // -1 dBFS
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly BiQuadFilter[] _highPass;
    private readonly BiQuadFilter[] _presence;
    private readonly float[] _envelope;

    public MifodiyVoiceFxSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = Math.Max(1, source.WaveFormat.Channels);
        var sampleRate = Math.Max(1, source.WaveFormat.SampleRate);
        _highPass = Enumerable.Range(0, _channels)
            .Select(_ => BiQuadFilter.HighPassFilter(sampleRate, 70f, 0.707f)).ToArray();
        _presence = Enumerable.Range(0, _channels)
            .Select(_ => BiQuadFilter.PeakingEQ(sampleRate, 2800f, 0.8f, 2f)).ToArray();
        _envelope = new float[_channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            var channel = i % _channels;
            var index = offset + i;
            var input = float.IsFinite(buffer[index]) ? buffer[index] : 0f;

            var presence = _presence[channel].Transform(_highPass[channel].Transform(input));
            var envelope = MathF.Abs(presence);
            var smoothing = envelope > _envelope[channel] ? 0.25f : 0.04f;
            _envelope[channel] += (envelope - _envelope[channel]) * smoothing;
            const float threshold = 0.18f;
            const float ratio = 2.0f;
            if (_envelope[channel] > threshold)
            {
                var compressed = threshold + (_envelope[channel] - threshold) / ratio;
                presence *= compressed / MathF.Max(_envelope[channel], 0.0001f);
            }

            // Gentle saturation followed by a hard -1 dBFS limiter.
            var saturated = MathF.Tanh(presence * 1.04f) / MathF.Tanh(1.04f);
            buffer[index] = Math.Clamp(float.IsFinite(saturated) ? saturated : 0f, -Limiter, Limiter);
        }
        return read;
    }
}

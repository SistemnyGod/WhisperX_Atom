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
    private readonly BiQuadFilter[] _lowMid;
    private readonly BiQuadFilter[] _air;
    private readonly float[] _envelope;
    private readonly bool _jarvisRu;
    private readonly float _wetMix;

    public MifodiyVoiceFxSampleProvider(ISampleProvider source, string voiceProfile = TtsVoiceProfiles.MifodiyTech)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = Math.Max(1, source.WaveFormat.Channels);
        var sampleRate = Math.Max(1, source.WaveFormat.SampleRate);
        _jarvisRu = TtsVoiceProfiles.IsJarvisRu(voiceProfile);
        // Keep the Jarvis-style profile intentionally conservative.  The
        // previous all-wet chain made sibilants metallic on laptop speakers
        // and threw away too much of the natural Silero body.  It is a
        // playback colour, not a voice conversion model.
        _wetMix = _jarvisRu ? 0.30f : 0.45f;
        var highPassHz = _jarvisRu ? 58f : 70f;
        var presenceGain = _jarvisRu ? 1.2f : 2f;
        _highPass = Enumerable.Range(0, _channels)
            .Select(_ => BiQuadFilter.HighPassFilter(sampleRate, highPassHz, 0.707f)).ToArray();
        _presence = Enumerable.Range(0, _channels)
            .Select(_ => BiQuadFilter.PeakingEQ(sampleRate, _jarvisRu ? 2850f : 2800f, 0.8f, presenceGain)).ToArray();
        _lowMid = Enumerable.Range(0, _channels)
            .Select(_ => BiQuadFilter.PeakingEQ(sampleRate, _jarvisRu ? 165f : 190f, 0.9f, _jarvisRu ? 0.6f : 0f)).ToArray();
        _air = Enumerable.Range(0, _channels)
            .Select(_ => BiQuadFilter.PeakingEQ(sampleRate, _jarvisRu ? 7200f : 6500f, 0.9f, _jarvisRu ? 0.3f : 0f)).ToArray();
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
            presence = _air[channel].Transform(_lowMid[channel].Transform(presence));
            var envelope = MathF.Abs(presence);
            var smoothing = envelope > _envelope[channel] ? (_jarvisRu ? 0.20f : 0.25f) : 0.04f;
            _envelope[channel] += (envelope - _envelope[channel]) * smoothing;
            var threshold = _jarvisRu ? 0.22f : 0.18f;
            var ratio = _jarvisRu ? 1.6f : 2.0f;
            if (_envelope[channel] > threshold)
            {
                var compressed = threshold + (_envelope[channel] - threshold) / ratio;
                presence *= compressed / MathF.Max(_envelope[channel], 0.0001f);
            }

            // Blend with the dry signal so Russian consonants and vowels keep
            // the natural Silero articulation.  Saturation is almost neutral
            // and only protects peaks; it is not intended to sound distorted.
            var saturation = _jarvisRu ? 1.015f : 1.04f;
            var saturated = MathF.Tanh(presence * saturation) / MathF.Tanh(saturation);
            var output = input * (1f - _wetMix) + saturated * _wetMix;
            buffer[index] = Math.Clamp(float.IsFinite(output) ? output : 0f, -Limiter, Limiter);
        }
        return read;
    }
}

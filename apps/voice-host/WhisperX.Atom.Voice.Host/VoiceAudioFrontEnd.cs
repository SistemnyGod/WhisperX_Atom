using System.Buffers.Binary;

namespace WhisperX.Atom.Voice.Host;

/// <summary>
/// Derived far-field processing for the Voice Host ASR path only. Durable
/// Recorder PCM, FLAC and playable WAV never pass through this class.
/// </summary>
internal sealed class VoiceAudioFrontEnd
{
    private const double HighPassCutoffHz = 90d;
    private const double TargetRms = 0.10d;
    private const double MaxGain = 4.0d;
    private double _previousInput;
    private double _previousOutput;
    private double _noiseFloor = 0.003d;
    private double _gain = 1.0d;
    private double _manualGain = 1.0d;

    public int ManualGainDb { get; private set; }

    public void SetManualGainDb(int gainDb)
    {
        ManualGainDb = Math.Clamp(gainDb, 0, 18);
        _manualGain = Math.Pow(10d, ManualGainDb / 20d);
    }

    public void Process(Span<byte> pcm16)
    {
        if (pcm16.Length < 2) return;
        var sampleCount = pcm16.Length / 2;
        var alpha = 1d / (1d + 2d * Math.PI * HighPassCutoffHz / 16000d);
        var sumSquares = 0d;
        for (var i = 0; i < sampleCount; i++)
        {
            var input = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / (double)short.MaxValue;
            var filtered = alpha * (_previousOutput + input - _previousInput);
            _previousInput = input;
            _previousOutput = filtered;
            sumSquares += filtered * filtered;
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.Slice(i * 2, 2), (short)Math.Clamp(Math.Round(filtered * short.MaxValue), short.MinValue, short.MaxValue));
        }

        var rms = Math.Sqrt(sumSquares / Math.Max(1, sampleCount));
        if (rms <= _noiseFloor * 1.5d)
            _noiseFloor = Math.Clamp(_noiseFloor * 0.995d + rms * 0.005d, 0.0005d, 0.2d);
        var speechFloor = Math.Max(_noiseFloor * 2d, 0.004d);
        var targetGain = rms > speechFloor
            ? Math.Clamp(TargetRms / Math.Max(rms, 0.0001d), 1d, MaxGain)
            : 1d;
        _gain += (targetGain - _gain) * (targetGain > _gain ? 0.25d : 0.06d);
        _gain = Math.Clamp(_gain, 1d, MaxGain);

        for (var i = 0; i < sampleCount; i++)
        {
            var filtered = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / (double)short.MaxValue;
            var limited = Math.Clamp(filtered * _gain * _manualGain, -0.92d, 0.92d);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.Slice(i * 2, 2), (short)Math.Round(limited * short.MaxValue));
        }
    }

    public void Reset()
    {
        _previousInput = 0;
        _previousOutput = 0;
        _noiseFloor = 0.003d;
        _gain = 1d;
    }
}

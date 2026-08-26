using System.Buffers.Binary;

namespace WhisperX.Atom.Voice.Host;

internal sealed class VoiceActivityDetector
{
    private double _noiseFloor = 0.003;
    private double _threshold = 0.007;

    public double NoiseFloorRms => _noiseFloor;
    public double NoiseFloorDb => ToDb(_noiseFloor);
    public double ThresholdRms => _threshold;
    public double ThresholdDb => ToDb(_threshold);

    public bool IsSpeech(ReadOnlySpan<byte> pcm16, string sensitivity)
    {
        if (pcm16.Length < 2) return false;
        double sum = 0;
        var samples = pcm16.Length / 2;
        for (var offset = 0; offset < pcm16.Length - 1; offset += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(offset, 2)) / (double)short.MaxValue;
            sum += sample * sample;
        }
        var rms = Math.Sqrt(sum / samples);
        if (rms < _noiseFloor * 1.4) _noiseFloor = Math.Clamp(_noiseFloor * 0.98 + rms * 0.02, 0.0005, 0.1);
        _threshold = sensitivity switch
        {
            "high" => Math.Max(0.004, _noiseFloor * 1.8),
            "low" => Math.Max(0.012, _noiseFloor * 3.5),
            _ => Math.Max(0.007, _noiseFloor * 2.4)
        };
        return rms >= _threshold;
    }

    public void Reset()
    {
        _noiseFloor = 0.003;
        _threshold = 0.007;
    }

    public void ApplyNoiseFloor(double rms, string sensitivity)
    {
        if (!double.IsFinite(rms) || rms <= 0) return;
        _noiseFloor = Math.Clamp(rms, 0.0005d, 0.1d);
        _threshold = sensitivity switch
        {
            "high" => Math.Max(0.004d, _noiseFloor * 1.8d),
            "low" => Math.Max(0.012d, _noiseFloor * 3.5d),
            _ => Math.Max(0.007d, _noiseFloor * 2.4d)
        };
    }

    private static double ToDb(double value) => 20d * Math.Log10(Math.Max(value, 0.00001d));
}

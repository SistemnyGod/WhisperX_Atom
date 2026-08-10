using System.Buffers.Binary;

namespace WhisperX.Atom.Voice.Host;

internal sealed class VoiceActivityDetector
{
    private double _noiseFloor = 0.003;

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
        var threshold = sensitivity switch
        {
            "high" => Math.Max(0.004, _noiseFloor * 1.8),
            "low" => Math.Max(0.012, _noiseFloor * 3.5),
            _ => Math.Max(0.007, _noiseFloor * 2.4)
        };
        return rms >= threshold;
    }
}

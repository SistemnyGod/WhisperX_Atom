using System.Buffers.Binary;

namespace WhisperX.Atom.Voice.Host;

internal sealed record VoiceCalibrationResult(
    string Phase,
    int DurationMs,
    long Samples,
    double AverageRms,
    double Peak,
    double RmsDb,
    double PeakDb,
    double RecommendedVadThresholdDb);

/// <summary>Collects short-lived microphone statistics without retaining audio.</summary>
internal sealed class VoiceCalibrationAccumulator
{
    private readonly object _gate = new();
    private readonly string _phase;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private double _sumSquares;
    private double _peak;
    private long _samples;

    public VoiceCalibrationAccumulator(string phase) => _phase = phase;

    public void Add(ReadOnlySpan<byte> pcm16)
    {
        lock (_gate)
        {
            for (var offset = 0; offset + 1 < pcm16.Length; offset += 2)
            {
                var value = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(offset, 2)) / (double)short.MaxValue);
                _sumSquares += value * value;
                _peak = Math.Max(_peak, value);
                _samples++;
            }
        }
    }

    public VoiceCalibrationResult Complete()
    {
        lock (_gate)
        {
            var rms = _samples == 0 ? 0d : Math.Sqrt(_sumSquares / _samples);
            var threshold = Math.Max(0.004d, rms * 2.4d);
            return new VoiceCalibrationResult(
                _phase,
                Math.Max(0, (int)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds),
                _samples,
                rms,
                _peak,
                ToDb(rms),
                ToDb(_peak),
                ToDb(threshold));
        }
    }

    private static double ToDb(double value) => 20d * Math.Log10(Math.Max(value, 0.00001d));
}

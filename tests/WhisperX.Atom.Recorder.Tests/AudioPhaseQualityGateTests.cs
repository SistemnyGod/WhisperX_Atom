using WhisperX.Atom.Recorder;
using Xunit;

namespace WhisperX.Atom.Recorder.Tests;

public sealed class AudioPhaseQualityGateTests
{
    private static AudioQualityAssessment Quality(double noiseDb, double snrDb)
        => new(noiseDb, noiseDb + snrDb, -6, snrDb, 0, 0, 0, 0, 0, 0, 0, 0,
            AudioQualityGrade.GOOD, "ok");

    [Fact]
    public void QuietCompleteTwoPhaseWindowIsConfirmed()
    {
        var result = AudioPhaseQualityGate.Evaluate(Quality(-40, 12), 3, 10, 3, 10);

        Assert.True(result.Confirmed);
        Assert.Equal("SILENCE_THEN_SPEECH_CONFIRMED", result.Reason);
    }

    [Theory]
    [InlineData(-30, 12, "NOISE_WINDOW_NOT_QUIET")]
    [InlineData(-40, 4, "SPEECH_WINDOW_NOT_CONFIRMED")]
    public void WeakPhaseFailsClosed(double noiseDb, double snrDb, string reason)
    {
        var result = AudioPhaseQualityGate.Evaluate(Quality(noiseDb, snrDb), 3, 10, 3, 10);

        Assert.False(result.Confirmed);
        Assert.Equal(reason, result.Reason);
    }
}

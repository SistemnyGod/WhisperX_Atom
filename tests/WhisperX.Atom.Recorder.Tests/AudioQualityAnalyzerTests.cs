using WhisperX.Atom.Recorder;
using Xunit;

public sealed class AudioQualityAnalyzerTests
{
    [Fact]
    public void CleanSpeechWithQuietRoomIsGood()
    {
        var silence = new short[4800];
        var speech = Enumerable.Range(0, 48000)
            .Select(i => (short)(Math.Sin(i * 0.04) * 9000))
            .ToArray();

        var result = AudioQualityAnalyzer.AnalyzePcm16(speech, silence);

        Assert.True(result.EstimatedSnrDb >= 20);
        Assert.Equal(AudioQualityGrade.GOOD, result.Grade);
        Assert.Equal(0, result.NormalizedClippingRatio);
    }

    [Fact]
    public void ClippingAndDropoutAreBadOrCritical()
    {
        var speech = new short[48000];
        Array.Fill(speech, short.MaxValue);
        var result = AudioQualityAnalyzer.AnalyzePcm16(speech, new short[4800]);

        Assert.Contains(result.Grade, new[] { AudioQualityGrade.BAD, AudioQualityGrade.CRITICAL });
        Assert.True(result.NormalizedClippingRatio > 90);
    }

    [Fact]
    public void DiagnosticsPreserveNativeAndNormalizedClipping()
    {
        var diagnostics = new AudioGraphAttemptDiagnostics
        {
            NativeSampleCount = 1000,
            NativeOverRangeSampleCount = 2,
            NormalizedSampleCount = 1000,
            NormalizedClippedSampleCount = 1,
            NormalizedPeak = 0.8,
            NormalizedSampleSum = 0,
            NormalizedSquareSum = 40
        };

        var result = AudioQualityAnalyzer.FromDiagnostics(diagnostics, -50);

        Assert.Equal(0.2, result.NativeClippingRatio, 3);
        Assert.Equal(0.1, result.NormalizedClippingRatio, 3);
        Assert.Equal(AudioQualityGrade.BAD, result.Grade);
    }
}

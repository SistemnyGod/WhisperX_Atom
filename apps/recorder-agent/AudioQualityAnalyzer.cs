namespace WhisperX.Atom.Recorder;

public enum AudioQualityGrade
{
    GOOD,
    WARNING,
    BAD,
    CRITICAL
}

/// <summary>
/// Optional, diagnostic-only quality information. It is deliberately not
/// used to alter the canonical PCM stream. The record is additive to the
/// probe IPC contract so older Desktop builds can ignore it.
/// </summary>
public sealed record AudioQualityAssessment(
    double NoiseFloorDb,
    double SpeechRmsDb,
    double SpeechPeakDb,
    double EstimatedSnrDb,
    double CrestFactorDb,
    double NativeClippingRatio,
    double NormalizedClippingRatio,
    double DcOffset,
    double SilenceRatio,
    long DropoutCount,
    long ZeroRunCount,
    long LongestZeroRunMs,
    AudioQualityGrade Grade,
    string Recommendation,
    string AnalyzerVersion = "audio-quality-v1")
{
    public bool Passed => Grade is AudioQualityGrade.GOOD or AudioQualityGrade.WARNING;
}

/// <summary>
/// Pure calculations shared by Recorder Host, Desktop Room Check and tests.
/// Keeping this class independent of AudioGraph makes the acceptance metrics
/// deterministic and prevents the quality gate from mutating recording data.
/// </summary>
public static class AudioQualityAnalyzer
{
    public static AudioQualityAssessment FromDiagnostics(
        AudioGraphAttemptDiagnostics? diagnostics,
        double? noiseFloorDb = null,
        int sampleRate = 48000)
    {
        if (diagnostics is null)
            return Empty("NO_AUDIO_DIAGNOSTICS");

        var speechRmsDb = ToDb(diagnostics.NormalizedRmsLinear);
        var speechPeakDb = ToDb(diagnostics.NormalizedPeak);
        var floor = noiseFloorDb ?? speechRmsDb;
        var snr = speechRmsDb - floor;
        var crest = speechRmsDb <= -59.9 || speechPeakDb <= -59.9
            ? 0d
            : speechPeakDb - speechRmsDb;
        var longestZeroMs = sampleRate <= 0
            ? 0L
            : (long)Math.Round(diagnostics.LongestZeroRunSamples * 1000d / sampleRate);
        var dropoutCount = diagnostics.DropoutCount + diagnostics.PipelineOverruns;
        var zeroRuns = diagnostics.ZeroRunCount;
        var confirmedZeroRuns = longestZeroMs >= 250 ? zeroRuns : 0;
        var grade = GradeFor(
            diagnostics.NativeOverRangeRatio,
            diagnostics.NormalizedClippedRatio,
            snr,
            Math.Abs(diagnostics.NormalizedDcOffset),
            dropoutCount,
            confirmedZeroRuns);
        return new AudioQualityAssessment(
            floor,
            speechRmsDb,
            speechPeakDb,
            snr,
            crest,
            diagnostics.NativeOverRangeRatio * 100d,
            diagnostics.NormalizedClippedRatio * 100d,
            diagnostics.NormalizedDcOffset,
            diagnostics.NormalizedSilenceRatio * 100d,
            dropoutCount,
            zeroRuns,
            longestZeroMs,
            grade,
            RecommendationFor(grade, snr, diagnostics.NormalizedDcOffset,
                diagnostics.NativeOverRangeRatio, diagnostics.NormalizedClippedRatio,
                dropoutCount, confirmedZeroRuns));
    }

    public static AudioQualityAssessment AnalyzePcm16(
        ReadOnlySpan<short> speech,
        ReadOnlySpan<short> silence,
        int sampleRate = 48000)
    {
        var speechStats = Calculate(speech, sampleRate);
        var silenceStats = Calculate(silence, sampleRate);
        var speechRmsDb = ToDb(speechStats.Rms);
        var noiseDb = ToDb(silenceStats.Rms);
        var snr = speechRmsDb - noiseDb;
        var confirmedZeroRuns = speechStats.LongestZeroRunMs >= 250 ? speechStats.ZeroRunCount : 0;
        var grade = GradeFor(
            speechStats.NativeClippingRatio,
            speechStats.ClippingRatio,
            snr,
            Math.Abs(speechStats.DcOffset),
            speechStats.DropoutCount,
            confirmedZeroRuns);
        return new AudioQualityAssessment(
            noiseDb,
            speechRmsDb,
            ToDb(speechStats.Peak),
            snr,
            speechRmsDb <= -59.9 ? 0 : ToDb(speechStats.Peak) - speechRmsDb,
            speechStats.NativeClippingRatio * 100d,
            speechStats.ClippingRatio * 100d,
            speechStats.DcOffset,
            speechStats.SilenceRatio * 100d,
            speechStats.DropoutCount,
            speechStats.ZeroRunCount,
            speechStats.LongestZeroRunMs,
            grade,
            RecommendationFor(grade, snr, speechStats.DcOffset,
                speechStats.NativeClippingRatio, speechStats.ClippingRatio,
                speechStats.DropoutCount, confirmedZeroRuns));
    }

    public static AudioQualityAssessment Combine(
        AudioQualityAssessment noise,
        AudioQualityAssessment speech)
        => speech with
        {
            NoiseFloorDb = noise.SpeechRmsDb,
            EstimatedSnrDb = speech.SpeechRmsDb - noise.SpeechRmsDb,
            Grade = GradeFor(
                speech.NativeClippingRatio / 100d,
                speech.NormalizedClippingRatio / 100d,
                speech.SpeechRmsDb - noise.SpeechRmsDb,
                Math.Abs(speech.DcOffset),
                speech.DropoutCount,
                speech.ZeroRunCount),
            Recommendation = RecommendationFor(
                GradeFor(
                    speech.NativeClippingRatio / 100d,
                    speech.NormalizedClippingRatio / 100d,
                    speech.SpeechRmsDb - noise.SpeechRmsDb,
                    Math.Abs(speech.DcOffset),
                    speech.DropoutCount,
                    speech.ZeroRunCount),
                speech.SpeechRmsDb - noise.SpeechRmsDb,
                speech.DcOffset,
                speech.NativeClippingRatio / 100d,
                speech.NormalizedClippingRatio / 100d,
                speech.DropoutCount,
                speech.ZeroRunCount)
        };

    public static double ToDb(double linear)
        => linear <= 0 || double.IsNaN(linear) || double.IsInfinity(linear)
            ? -60d
            : Math.Clamp(20d * Math.Log10(linear), -60d, 6d);

    private static AudioQualityGrade GradeFor(
        double nativeClippingRatio,
        double normalizedClippingRatio,
        double snrDb,
        double dcOffset,
        long dropouts,
        long zeroRuns)
    {
        var clipping = Math.Max(nativeClippingRatio, normalizedClippingRatio);
        if (clipping > 0.01)
            return AudioQualityGrade.CRITICAL;
        if (clipping > 0.001 || snrDb < 12d || dcOffset > 0.03 || dropouts > 0)
            return AudioQualityGrade.BAD;
        if (clipping > 0.0001 || snrDb < 20d || zeroRuns > 0)
            return AudioQualityGrade.WARNING;
        return AudioQualityGrade.GOOD;
    }

    private static string RecommendationFor(
        AudioQualityGrade grade,
        double snrDb,
        double dcOffset,
        double nativeClip,
        double normalizedClip,
        long dropouts,
        long zeroRuns)
    {
        if (grade == AudioQualityGrade.CRITICAL || nativeClip > 0.01 || normalizedClip > 0.01)
            return "Снизьте Mic Boost/уровень микрофона и повторите проверку.";
        if (dropouts > 0 || zeroRuns > 0)
            return "Обнаружены пропуски сигнала: проверьте USB/драйвер и приблизьте микрофон.";
        if (Math.Abs(dcOffset) > 0.03)
            return "Проверьте аудиодрайвер и отключите аппаратные улучшения микрофона.";
        if (snrDb < 12d)
            return "Слишком шумно или тихо: приблизьте микрофон либо используйте профиль LARGE_ROOM.";
        if (snrDb < 20d)
            return "Сигнал пригоден, но для дальней дистанции рекомендуется LARGE_ROOM.";
        return "Уровень и непрерывность сигнала в норме.";
    }

    private static AudioQualityAssessment Empty(string recommendation) => new(
        -60, -60, -60, 0, 0, 0, 0, 0, 100, 0, 0, 0,
        AudioQualityGrade.BAD, recommendation);

    private static PcmStats Calculate(ReadOnlySpan<short> samples, int sampleRate)
    {
        if (samples.IsEmpty)
            return new();
        double sum = 0, square = 0, peak = 0;
        long clipped = 0, zeroRuns = 0, silentSamples = 0, longest = 0, current = 0;
        const double silence = 0.003;
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i] / 32768d;
            var abs = Math.Abs(value);
            peak = Math.Max(peak, abs);
            sum += value;
            square += value * value;
            if (abs >= 0.999969d) clipped++;
            if (abs <= silence) { silentSamples++; current++; }
            else if (current > 0) { zeroRuns++; longest = Math.Max(longest, current); current = 0; }
        }
        if (current > 0) { zeroRuns++; longest = Math.Max(longest, current); }
        return new(
            Math.Sqrt(square / samples.Length), peak, sum / samples.Length,
            (double)clipped / samples.Length,
            (double)silentSamples / Math.Max(1, samples.Length),
            zeroRuns,
            sampleRate <= 0 ? 0 : (long)Math.Round(longest * 1000d / sampleRate), 0);
    }

    private readonly record struct PcmStats(
        double Rms = 0,
        double Peak = 0,
        double DcOffset = 0,
        double ClippingRatio = 0,
        double SilenceRatio = 0,
        long ZeroRunCount = 0,
        long LongestZeroRunMs = 0,
        long DropoutCount = 0)
    {
        public double NativeClippingRatio => ClippingRatio;
    }
}

namespace WhisperX.Atom.Recorder;

public sealed record AudioPhaseQualityResult(bool Confirmed, string Reason)
{
    public static AudioPhaseQualityResult NotConfirmed(string reason)
        => new(false, reason);
}

/// <summary>
/// Validates that a RAW A/B capture really contains a quiet noise phase and a
/// separate speech phase. This is diagnostic-only and never changes PCM.
/// </summary>
public static class AudioPhaseQualityGate
{
    public const double MaxNoiseRmsDb = -35d;
    public const double MinimumSnrDb = 6d;

    public static AudioPhaseQualityResult Evaluate(
        AudioQualityAssessment? quality,
        double actualSilenceSeconds,
        double actualSpeechSeconds,
        double requestedSilenceSeconds,
        double requestedSpeechSeconds)
    {
        if (quality is null)
            return AudioPhaseQualityResult.NotConfirmed("QUALITY_MISSING");
        if (actualSilenceSeconds + 0.05d < requestedSilenceSeconds)
            return AudioPhaseQualityResult.NotConfirmed("NOISE_PHASE_INCOMPLETE");
        if (actualSpeechSeconds + 0.05d < requestedSpeechSeconds)
            return AudioPhaseQualityResult.NotConfirmed("SPEECH_PHASE_INCOMPLETE");
        if (quality.NoiseFloorDb > MaxNoiseRmsDb)
            return AudioPhaseQualityResult.NotConfirmed("NOISE_WINDOW_NOT_QUIET");
        if (quality.EstimatedSnrDb < MinimumSnrDb)
            return AudioPhaseQualityResult.NotConfirmed("SPEECH_WINDOW_NOT_CONFIRMED");
        return new AudioPhaseQualityResult(true, "SILENCE_THEN_SPEECH_CONFIRMED");
    }
}

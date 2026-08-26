namespace WhisperX.Atom.Voice.Host.Tts;

/// <summary>Stable, additive product profiles for Russian Mifodiy voices.</summary>
public static class TtsVoiceProfiles
{
    public const string MifodiyTech = "MIFODIY_TECH";
    public const string Clean = "CLEAN";
    /// <summary>Clean Silero AIDAR voice without technology FX.</summary>
    public const string AidarClean = "AIDAR_CLEAN";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim();
        if (string.Equals(normalized, AidarClean, StringComparison.OrdinalIgnoreCase)) return AidarClean;
        if (string.Equals(normalized, Clean, StringComparison.OrdinalIgnoreCase)) return Clean;
        return MifodiyTech;
    }

    public static bool IsMifodiyTech(string? value)
        => string.Equals(Normalize(value), MifodiyTech, StringComparison.Ordinal);

    public static bool IsTechnologyProfile(string? value)
        => Normalize(value) is MifodiyTech;

    public static bool IsAidarClean(string? value)
        => string.Equals(Normalize(value), AidarClean, StringComparison.Ordinal);

}

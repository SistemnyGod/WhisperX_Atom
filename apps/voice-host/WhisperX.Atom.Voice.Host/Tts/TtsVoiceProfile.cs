namespace WhisperX.Atom.Voice.Host.Tts;

/// <summary>Stable, additive product profiles for Mifodiy voices.</summary>
public static class TtsVoiceProfiles
{
    public const string MifodiyTech = "MIFODIY_TECH";
    /// <summary>Russian Jarvis-style treatment; this is not an actor/character clone.</summary>
    public const string JarvisRu = "JARVIS_RU";
    public const string Clean = "CLEAN";
    /// <summary>Clean Silero AIDAR voice without technology FX.</summary>
    public const string AidarClean = "AIDAR_CLEAN";
    /// <summary>Optional English Piper voice. It is never the Russian default.</summary>
    public const string JarvisEn = "JARVIS_EN";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim();
        if (string.Equals(normalized, AidarClean, StringComparison.OrdinalIgnoreCase)) return AidarClean;
        if (string.Equals(normalized, Clean, StringComparison.OrdinalIgnoreCase)) return Clean;
        if (string.Equals(normalized, JarvisRu, StringComparison.OrdinalIgnoreCase)) return JarvisRu;
        if (string.Equals(normalized, JarvisEn, StringComparison.OrdinalIgnoreCase)) return JarvisEn;
        return MifodiyTech;
    }

    public static bool IsMifodiyTech(string? value)
        => string.Equals(Normalize(value), MifodiyTech, StringComparison.Ordinal);

    public static bool IsTechnologyProfile(string? value)
        => Normalize(value) is MifodiyTech or JarvisRu;

    public static bool IsAidarClean(string? value)
        => string.Equals(Normalize(value), AidarClean, StringComparison.Ordinal);

    public static bool IsJarvisRu(string? value)
        => string.Equals(Normalize(value), JarvisRu, StringComparison.Ordinal);

    public static bool IsPiperJarvis(string? value)
        => string.Equals(Normalize(value), JarvisEn, StringComparison.Ordinal);
}

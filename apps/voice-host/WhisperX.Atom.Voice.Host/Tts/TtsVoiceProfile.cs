namespace WhisperX.Atom.Voice.Host.Tts;

/// <summary>Stable, additive product profiles for the Russian Mifodiy voice.</summary>
public static class TtsVoiceProfiles
{
    public const string MifodiyTech = "MIFODIY_TECH";
    public const string Clean = "CLEAN";

    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), Clean, StringComparison.OrdinalIgnoreCase)
            ? Clean
            : MifodiyTech;

    public static bool IsMifodiyTech(string? value)
        => string.Equals(Normalize(value), MifodiyTech, StringComparison.Ordinal);
}

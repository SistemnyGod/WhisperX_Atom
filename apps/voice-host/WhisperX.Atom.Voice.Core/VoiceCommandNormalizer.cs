using System.Globalization;

namespace WhisperX.Atom.Voice;

/// <summary>
/// Small, explicit normalization layer for natural command wording.
/// It only maps reviewed complete phrases; it never performs fuzzy matching
/// and it never removes safety-bearing words such as negation or time guards.
/// </summary>
public static class VoiceCommandNormalizer
{
    private static readonly string[] ProtectedWords =
    ["не", "нет", "если", "когда", "потом", "после", "позже", "только"];

    private static readonly IReadOnlyDictionary<string, string> ExactAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["пожалуйста начни запись"] = "начни запись",
            ["пожалуйста запусти запись"] = "запусти запись",
            ["начинай запись"] = "начни запись",
            ["давай начнем запись"] = "начни запись",
            ["давай запустим запись"] = "запусти запись",
            ["пожалуйста поставь запись на паузу"] = "поставь на паузу",
            ["поставь запись на паузу"] = "поставь на паузу",
            ["давай паузу"] = "поставь на паузу",
            ["приостанови запись"] = "приостанови запись",
            ["продолжай запись"] = "продолжи запись",
            ["давай продолжим запись"] = "продолжи запись",
            ["возобнови запись"] = "возобнови запись",
            ["заканчивай запись"] = "заверши запись",
            ["пожалуйста заверши запись"] = "заверши запись",
            ["заверши запись"] = "заверши запись",
            ["поставь запись на паузу пожалуйста"] = "поставь на паузу",
        };

    public static string NormalizeCandidate(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0 || ContainsProtectedWord(normalized)) return normalized;
        return ExactAliases.TryGetValue(normalized, out var mapped) ? mapped : normalized;
    }

    public static bool ContainsProtectedWord(string? value)
    {
        var normalized = Normalize(value);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Any(word => ProtectedWords.Contains(word, StringComparer.Ordinal));
    }

    /// <summary>
    /// Detects speech that appears to be trying to control Recorder but is
    /// not safe enough for an action. This is a local recovery route, never an
    /// authorization signal.
    /// </summary>
    public static bool LooksCommandShaped(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0 || !normalized.Contains("запис", StringComparison.Ordinal)) return false;
        return normalized.Contains("нач", StringComparison.Ordinal)
            || normalized.Contains("запуст", StringComparison.Ordinal)
            || normalized.Contains("останов", StringComparison.Ordinal)
            || normalized.Contains("останав", StringComparison.Ordinal)
            || normalized.Contains("заверш", StringComparison.Ordinal)
            || normalized.Contains("заканч", StringComparison.Ordinal)
            || normalized.Contains("пауз", StringComparison.Ordinal)
            || normalized.Contains("приостанов", StringComparison.Ordinal)
            || normalized.Contains("продолж", StringComparison.Ordinal)
            || normalized.Contains("возобнов", StringComparison.Ordinal);
    }

    private static string Normalize(string? value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Select(character => char.IsPunctuation(character) ? ' ' : character)
            .ToArray());
        return string.Join(' ', cleaned.Trim().ToLower(CultureInfo.GetCultureInfo("ru-RU"))
            .Replace('\u0451', '\u0435')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

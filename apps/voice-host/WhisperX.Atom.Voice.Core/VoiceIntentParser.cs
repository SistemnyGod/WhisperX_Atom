using System.Globalization;

namespace WhisperX.Atom.Voice;

/// <summary>Small deterministic parser used after Vosk/whisper.cpp recognition.</summary>
public sealed class VoiceIntentParser
{
    private static readonly string[] WakeWords = ["атом", "atom"];

    public bool HasWakeWord(string text)
    {
        var normalized = Normalize(text);
        return WakeWords.Any(word => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(word, StringComparer.OrdinalIgnoreCase));
    }

    public VoiceCommand Parse(string text, double confidence = 1.0)
    {
        var normalized = Normalize(text);
        var withoutWake = RemoveWakeWord(normalized);
        if (string.IsNullOrWhiteSpace(withoutWake))
            return new VoiceCommand(VoiceIntent.Unknown, text, confidence, CreatedAt: DateTimeOffset.UtcNow);

        var command = withoutWake switch
        {
            var value when Matches(value, "начни запись", "начать запись", "запись", "старт") => VoiceIntent.StartRecording,
            var value when Matches(value, "пауза", "поставь на паузу", "приостанови запись") => VoiceIntent.PauseRecording,
            var value when Matches(value, "продолжи", "продолжить", "возобнови запись") => VoiceIntent.ResumeRecording,
            var value when Matches(value, "метка", "поставь метку", "добавь метку") => VoiceIntent.AddMarker,
            var value when Matches(value, "решение", "отметь решение", "зафиксируй решение") => VoiceIntent.MarkDecision,
            var value when Matches(value, "поручение", "отметь поручение", "зафиксируй поручение", "задача") => VoiceIntent.MarkActionItem,
            var value when Matches(value, "статус", "состояние", "что происходит") => VoiceIntent.GetStatus,
            var value when Matches(value, "заверши", "завершить", "останови запись", "стоп") => VoiceIntent.StopRecording,
            var value when Matches(value, "да", "подтверждаю", "подтвердить", "подтверждение") => VoiceIntent.Confirm,
            var value when Matches(value, "нет", "отмена", "отмени", "не надо") => VoiceIntent.Cancel,
            _ => IsQuestion(withoutWake) ? VoiceIntent.HistoryQuestion : VoiceIntent.Unknown
        };

        var parameter = command is VoiceIntent.MarkDecision or VoiceIntent.MarkActionItem or VoiceIntent.HistoryQuestion
            ? ExtractParameter(withoutWake, command) : null;
        return new VoiceCommand(command, text, confidence, parameter, DateTimeOffset.UtcNow);
    }

    private static bool IsQuestion(string value) => value.Contains('?', StringComparison.Ordinal)
        || value.StartsWith("что ", StringComparison.Ordinal)
        || value.StartsWith("какие ", StringComparison.Ordinal)
        || value.StartsWith("кто ", StringComparison.Ordinal)
        || value.StartsWith("где ", StringComparison.Ordinal)
        || value.StartsWith("когда ", StringComparison.Ordinal)
        || value.StartsWith("сколько ", StringComparison.Ordinal)
        || value.StartsWith("прочитай ", StringComparison.Ordinal)
        || value.StartsWith("сделай ", StringComparison.Ordinal);

    private static bool Matches(string value, params string[] candidates) => candidates.Any(candidate =>
        value.Equals(candidate, StringComparison.Ordinal) || value.StartsWith(candidate + " ", StringComparison.Ordinal));

    private static string ExtractParameter(string value, VoiceIntent intent)
    {
        var prefixes = intent switch
        {
            VoiceIntent.MarkDecision => new[] { "зафиксируй решение", "отметь решение", "решение" },
            VoiceIntent.MarkActionItem => new[] { "зафиксируй поручение", "отметь поручение", "поручение", "задача" },
            _ => Array.Empty<string>()
        };
        foreach (var prefix in prefixes)
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return value[prefix.Length..].Trim();
        return value;
    }

    private static string RemoveWakeWord(string value)
    {
        foreach (var word in WakeWords)
            if (value.StartsWith(word, StringComparison.Ordinal)) return value[word.Length..].Trim(' ', ',', ':');
        return value;
    }

    private static string Normalize(string text) => string.Join(' ', text.Trim().ToLower(CultureInfo.GetCultureInfo("ru-RU"))
        .Replace('ё', 'е').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

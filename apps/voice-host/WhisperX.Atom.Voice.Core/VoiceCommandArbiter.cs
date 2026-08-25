namespace WhisperX.Atom.Voice;

public sealed record VoiceArbitrationResult(
    VoiceCommand Command,
    double Confidence,
    string Recognizer,
    string Route,
    string? NormalizationReason);

/// <summary>
/// Shared, deterministic arbitration between constrained and unrestricted
/// Vosk sessions.  It never performs an action or calls an assistant.
/// </summary>
public sealed class VoiceCommandArbiter(VoiceIntentParser parser)
{
    private readonly VoiceIntentParser _parser = parser;

    public VoiceArbitrationResult Resolve(
        string text,
        double confidence,
        string? grammarText,
        double grammarConfidence,
        double minimumConfidence = VoiceIntentParser.DefaultMinimumConfidence)
    {
        var unrestricted = _parser.Parse(text, confidence, minimumConfidence);
        if (!string.IsNullOrWhiteSpace(grammarText))
        {
            var input = _parser.HasWakeWordAtBoundary(grammarText) ? grammarText : $"Мифодий {grammarText}";
            var grammar = _parser.Parse(input, grammarConfidence, minimumConfidence);
            if (grammar.Intent is not (VoiceIntent.Unknown or VoiceIntent.AssistantQuery)
                && _parser.IsSafeRecorderCommand(text)
                && grammarConfidence >= minimumConfidence)
            {
                return new(grammar, grammar.Confidence, "VOSK_COMMAND_GRAMMAR", "LOCAL_COMMAND", "GRAMMAR_CANONICALIZED_ASR");
            }
        }

        return new(
            unrestricted,
            unrestricted.Confidence,
            "VOSK_UNRESTRICTED",
            unrestricted.Intent == VoiceIntent.AssistantQuery ? "ASSISTANT_QUERY" : "LOCAL_COMMAND",
            null);
    }
}

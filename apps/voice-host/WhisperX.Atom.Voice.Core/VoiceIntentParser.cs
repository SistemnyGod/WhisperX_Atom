namespace WhisperX.Atom.Voice;

/// <summary>
/// Deterministic intent gate used after Vosk/whisper.cpp recognition.
///
/// Recorder mutations remain a small, explicit Russian vocabulary.  Every
/// other confidently recognized non-empty utterance is deliberately classified
/// as an AssistantQuery after the wake word. Empty, low-confidence or
/// unrecognizable speech remains Unknown. This is the important safety
/// boundary: arbitrary speech can only become an LLM request; it can never
/// mutate the recorder unless it matched one of the explicit command forms
/// above.
/// </summary>
public sealed class VoiceIntentParser
{
    /// <summary>
    /// Default confidence floor for a recognized utterance. The runtime can
    /// provide a sensitivity-specific value, but the parser itself remains
    /// fail-closed when called directly by an IPC/self-test consumer.
    /// </summary>
    public const double DefaultMinimumConfidence = 0.55;

    // "Мифодий" is the product wake word. "Мефодий" is a common speech
    // recognition variant.  The old "Атом" alias is opt-in: it is useful for
    // development/rollout compatibility, but is too common for production
    // far-field listening and would otherwise create false activations.
    private static readonly string[] PrimaryWakeWords = ["мифодий", "мефодий"];
    private static readonly string[] LegacyWakeWords = ["атом", "atom"];
    private readonly bool _allowLegacyAtom;

    private static readonly string[] RecorderCommandCandidates =
    [
        "начни запись", "запусти запись", "заверши запись", "останови запись",
        "поставь на паузу", "приостанови запись", "продолжи запись", "возобнови запись",
        "поставь метку", "добавь метку", "отметь решение", "зафиксируй решение",
        "отметь поручение", "зафиксируй поручение"
    ];

    public VoiceIntentParser(bool allowLegacyAtom = true)
    {
        _allowLegacyAtom = allowLegacyAtom;
    }

    public bool AllowsLegacyAtom => _allowLegacyAtom;

    private IEnumerable<string> WakeWords => _allowLegacyAtom
        ? PrimaryWakeWords.Concat(LegacyWakeWords)
        : PrimaryWakeWords;

    public bool HasWakeWord(string text)
    {
        var normalized = VoiceCommandText.Normalize(text);
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => WakeWords.Contains(word, StringComparer.OrdinalIgnoreCase));
    }

    public VoiceCommand Parse(
        string text,
        double confidence = 1.0,
        double minimumConfidence = DefaultMinimumConfidence)
    {
        var normalized = VoiceCommandText.Normalize(text);
        var withoutWake = RemoveWakeWord(normalized);
        var commandCandidate = VoiceCommandText.NormalizeCommandCandidate(withoutWake, WakeWords, RecorderCommandCandidates);
        // Punctuation is intentionally removed for matching, but a question
        // marker remains a semantic guard.  «Останови запись?» is a question,
        // not permission to mutate Recorder.  This check happens before the
        // exact-command switch so punctuation cannot bypass the fail-closed
        // conversational boundary.
        var questionLike = ContainsQuestionMarker(text) || IsQuestion(withoutWake);
        var threshold = double.IsFinite(minimumConfidence)
            ? Math.Clamp(minimumConfidence, 0d, 1d)
            : DefaultMinimumConfidence;

        // Low-confidence or non-finite recognition is never allowed to reach
        // strict command matching. Only a confidently recognized command can
        // mutate Recorder, while a confidently recognized non-command becomes
        // AssistantQuery below.
        if (string.IsNullOrWhiteSpace(commandCandidate)
            || !double.IsFinite(confidence)
            || confidence < threshold)
            return new VoiceCommand(VoiceIntent.Unknown, text, confidence, CreatedAt: DateTimeOffset.UtcNow);

        // Keep this table intentionally narrow. Infinitives and bare nouns
        // are conversational text, not commands: for example, «остановить
        // запись» and «начать запись» must reach the Assistant rather than
        // mutate the Recorder. A command starts with an explicit imperative
        // pattern (or one of the exact status/confirmation tokens).
        var intent = commandCandidate switch
        {
            var value when (Matches(value, "начни запись") || Matches(value, "запусти запись")) && IsExact(value, "начни запись", "запусти запись") => VoiceIntent.StartRecording,
            var value when Matches(value, "поставь на паузу", "приостанови запись") && IsExact(value, "поставь на паузу", "приостанови запись") => VoiceIntent.PauseRecording,
            var value when Matches(value, "продолжи запись", "возобнови запись") && IsExact(value, "продолжи запись", "возобнови запись") => VoiceIntent.ResumeRecording,
            var value when Matches(value, "поставь метку", "добавь метку") && IsExact(value, "поставь метку", "добавь метку") => VoiceIntent.AddMarker,
            var value when Matches(value, "отметь решение", "зафиксируй решение") => VoiceIntent.MarkDecision,
            var value when Matches(value, "отметь поручение", "зафиксируй поручение") => VoiceIntent.MarkActionItem,
            var value when IsExact(value,
                "статус", "состояние", "запись идёт", "запись идет", "идёт запись", "идет запись",
                "сколько идёт запись", "сколько идет запись") => VoiceIntent.GetStatus,
            var value when Matches(value, "заверши запись", "останови запись") && IsExact(value, "заверши запись", "останови запись") => VoiceIntent.StopRecording,
            var value when Matches(value, "остановись", "замолчи", "прекрати говорить", "останови ответ") => VoiceIntent.StopSpeaking,
            var value when IsExact(value,
                "пока", "ну пока", "ладно пока", "до свидания", "до встречи",
                "всего доброго", "хорошего дня", "спокойной ночи", "увидимся",
                "спасибо пока", "спасибо до свидания") => VoiceIntent.Farewell,
            // Conversational follow-ups are intentionally exact. A longer
            // phrase such as «сделай ответ короче по ремонту» remains an
            // AssistantQuery and is not accidentally collapsed into a
            // modifier command.
            var value when IsExact(value,
                "повтори", "повтори ответ", "повтори последний ответ") => VoiceIntent.RepeatAnswer,
            var value when IsExact(value,
                "короче", "ответь короче", "коротко") => VoiceIntent.ShortenAnswer,
            var value when IsExact(value,
                "подробнее", "расскажи подробнее", "объясни подробнее") => VoiceIntent.ElaborateAnswer,
            var value when IsExact(value,
                "вернись к предыдущему вопросу", "повтори предыдущий вопрос") => VoiceIntent.PreviousQuestion,
            var value when IsExact(value,
                "состояние сервера", "сервер доступен", "сервер работает") => VoiceIntent.GetServerStatus,
            var value when IsExact(value,
                "состояние обработки", "статус обработки", "как идёт обработка", "как идет обработка",
                "какая стадия обработки", "стенограмма готова") => VoiceIntent.GetPipelineStatus,
            var value when IsExact(value,
                "состояние диска", "свободное место", "сколько места", "сколько осталось места") => VoiceIntent.GetStorageStatus,
            var value when Matches(value, "да", "подтверждаю", "подтвердить", "подтверждение") => VoiceIntent.Confirm,
            var value when Matches(value, "нет", "отмена", "отмени", "не надо") => VoiceIntent.Cancel,
            // AssistantQuery is the canonical conversational intent.
            // HistoryQuestion remains an enum alias for older IPC consumers.
            // Context (GENERAL_CHAT/CURRENT_MEETING/LIVE_MEETING) is resolved
            // centrally by the API, not in Voice Host.
            _ => IsAssistantUtterance(withoutWake) ? VoiceIntent.AssistantQuery : VoiceIntent.Unknown
        };
        // A question marker must never turn a Recorder mutation into an
        // action. Non-mutating local lookups (for example «запись идёт?» or
        // «состояние сервера?») remain valid fast paths.
        if (questionLike && IsRecorderMutation(intent))
            intent = IsAssistantUtterance(withoutWake) ? VoiceIntent.AssistantQuery : VoiceIntent.Unknown;

        var parameter = intent is VoiceIntent.MarkDecision or VoiceIntent.MarkActionItem
            ? ExtractParameter(commandCandidate, intent)
            : intent is VoiceIntent.AssistantQuery or VoiceIntent.Farewell
                ? ExtractParameter(withoutWake, intent)
                : null;
        return new VoiceCommand(intent, text, confidence, parameter, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Returns true only when the utterance starts with an approved
    /// imperative. Questions and infinitives remain conversational text.
    /// A true result is a local repeat/recovery path, never an Assistant query.
    /// </summary>
    public bool IsRecorderImperative(string text)
    {
        var normalized = VoiceCommandText.NormalizeCommandCandidate(RemoveWakeWord(VoiceCommandText.Normalize(text)), WakeWords, Array.Empty<string>());
        return RecorderCommandCandidates.Any(command =>
            normalized == command || normalized.StartsWith(command + " ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true only when bounded normalization produced one complete
    /// approved command. Unknown trailing words therefore cannot be promoted
    /// by the constrained grammar recognizer.
    /// </summary>
    public bool IsSafeRecorderCommand(string text)
    {
        var normalized = VoiceCommandText.NormalizeCommandCandidate(
            RemoveWakeWord(VoiceCommandText.Normalize(text)),
            WakeWords,
            RecorderCommandCandidates);
        return RecorderCommandCandidates.Contains(normalized, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> RecorderCommands => RecorderCommandCandidates;

    private static bool IsQuestion(string value) => value.Contains('?', StringComparison.Ordinal)
        || value.StartsWith("что ", StringComparison.Ordinal)
        || value.StartsWith("какие ", StringComparison.Ordinal)
        || value.StartsWith("какой ", StringComparison.Ordinal)
        || value.StartsWith("кто ", StringComparison.Ordinal)
        || value.StartsWith("где ", StringComparison.Ordinal)
        || value.StartsWith("когда ", StringComparison.Ordinal)
        || value.StartsWith("сколько ", StringComparison.Ordinal)
        || value.StartsWith("почему ", StringComparison.Ordinal)
        || value.StartsWith("зачем ", StringComparison.Ordinal)
        // Vosk normally returns lower-case text without a question mark.
        // These deterministic forms cover the natural requests a participant
        // makes during or after a meeting without making recorder actions
        // less strict.
        || value.StartsWith("расскажи ", StringComparison.Ordinal)
        || value.StartsWith("напомни ", StringComparison.Ordinal)
        || value.StartsWith("покажи ", StringComparison.Ordinal)
        || value.StartsWith("найди ", StringComparison.Ordinal)
        || value.StartsWith("объясни ", StringComparison.Ordinal)
        || value.StartsWith("прочитай ", StringComparison.Ordinal)
        || value.StartsWith("сделай ", StringComparison.Ordinal)
        // Conversational requests are routed to the Assistant as well. They
        // are intentionally not recorder commands, so they remain safe while
        // capture is active and when no meeting is open.
        || value is "привет" or "здравствуй" or "здравствуйте" or "добрый день" or "доброе утро" or "добрый вечер"
        || value is "скажи привет" or "как дела" or "как тебя зовут" or "кто ты" or "спасибо" or "спасибо мифодий"
        || value is "расскажи анекдот" or "пошути" or "поговори со мной"
        || value.StartsWith("скажи ", StringComparison.Ordinal)
        || value.StartsWith("пошути ", StringComparison.Ordinal)
        || value.StartsWith("поговори ", StringComparison.Ordinal)
        || value.StartsWith("переведи ", StringComparison.Ordinal)
        || value.StartsWith("напиши ", StringComparison.Ordinal);

    private static bool ContainsQuestionMarker(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && (value.Contains('?', StringComparison.Ordinal) || value.Contains('\uFF1F', StringComparison.Ordinal));

    private static bool IsRecorderMutation(VoiceIntent intent) => intent is
        VoiceIntent.StartRecording or VoiceIntent.PauseRecording or VoiceIntent.ResumeRecording
        or VoiceIntent.AddMarker or VoiceIntent.MarkDecision or VoiceIntent.MarkActionItem
        or VoiceIntent.StopRecording;

    private static bool IsAssistantUtterance(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (IsQuestion(value)) return true;
        // A short, confidently recognised phrase such as «погода» or
        // «привет» is still a valid conversational request.  Confidence is
        // checked by VoiceHostRuntime before this gate is executed, so this
        // fallback does not turn low-quality audio into a recorder action.
        return value.Length >= 2;
    }

    private static bool Matches(string value, params string[] candidates) => candidates.Any(candidate =>
        value.Equals(candidate, StringComparison.Ordinal) || value.StartsWith(candidate + " ", StringComparison.Ordinal));

    private static bool IsExact(string value, params string[] candidates) => candidates.Any(candidate =>
        value.Equals(candidate, StringComparison.Ordinal));

    private static string ExtractParameter(string value, VoiceIntent intent)
    {
        var prefixes = intent switch
        {
            VoiceIntent.MarkDecision => new[] { "зафиксируй решение", "отметь решение", "решение" },
            VoiceIntent.MarkActionItem => new[] { "зафиксируй поручение", "отметь поручение", "поручение", "задача" },
            _ => Array.Empty<string>()
        };
        if (intent == VoiceIntent.Farewell) return value;
        foreach (var prefix in prefixes)
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return value[prefix.Length..].Trim();
        return value;
    }

    private string RemoveWakeWord(string value)
    {
        foreach (var word in WakeWords.OrderByDescending(static word => word.Length))
            if (value.StartsWith(word, StringComparison.Ordinal)) return value[word.Length..].Trim(' ', ',', ':');
        return value;
    }

}

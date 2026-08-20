/// <summary>
/// Single server-side routing policy for text and voice Assistant requests.
/// Desktop and Voice Host only provide transport data; they never decide
/// whether a question belongs to GENERAL_CHAT, CURRENT_MEETING, or the
/// provisional LIVE_MEETING memory.
///
/// AUTO is retrieval-aware: wording is used only as a hint for the history
/// branch. Scope selection is confirmed by the same Russian FTS and quality
/// gates that the Assistant worker uses to build evidence.
/// </summary>
public sealed record AssistantModeResolutionRequest(
    string Question,
    string? RequestedMode = "AUTO",
    Guid? UserId = null,
    Guid? ActiveMeetingId = null,
    Guid? RecordingSessionId = null,
    string? CaptureState = null,
    Guid? ConversationId = null,
    string? PreviousResolvedMode = null,
    bool Privileged = false);

public sealed record AssistantModeResolution(
    string ResolvedMode,
    Guid? MeetingId,
    string Reason,
    double Confidence,
    Guid? ConversationId = null,
    string? ErrorCode = null,
    string? Clarification = null);

public sealed class AssistantModeResolver(UnifiedProductStore store)
{
    private static readonly HashSet<string> AllowedModes = new(StringComparer.Ordinal)
    {
        "AUTO", "GENERAL_CHAT", "MEETING_MEMORY", "MEETING_HISTORY", "CURRENT_MEETING", "LIVE_MEETING"
    };

    private static readonly HashSet<string> ActiveCaptureStates = new(StringComparer.Ordinal)
    {
        "STARTING", "RECORDING", "PAUSED", "AWAITING_AGENT_RECONNECT"
    };

    public async Task<AssistantModeResolution> ResolveAsync(
        AssistantModeResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var question = request.Question?.Trim() ?? string.Empty;
        if (question.Length is 0 or > 2000)
            return Invalid("ASSISTANT_QUERY_INVALID", request.ConversationId);

        var normalizedMode = NormalizeRequestedMode(request.RequestedMode);
        if (normalizedMode is null)
            return Invalid("ASSISTANT_MODE_INVALID", request.ConversationId);

        // A conversation is a server-owned scope hint. Reading it here also
        // makes follow-up questions work after Desktop has restarted, while
        // the user id keeps the lookup inside the authenticated RBAC boundary.
        var scopedMeetingId = request.ActiveMeetingId;
        var previousMode = NormalizePreviousMode(request.PreviousResolvedMode);
        if (request.ConversationId is Guid conversationId && request.UserId is Guid userId)
        {
            var conversation = await store.GetAssistantConversationAsync(conversationId, userId).ConfigureAwait(false);
            if (conversation is not null)
            {
                previousMode ??= NormalizePreviousMode(conversation.AssistantMode);
                if (scopedMeetingId is null && conversation.MeetingId is Guid conversationMeeting
                    && (conversation.AssistantMode is "CURRENT_MEETING" or "LIVE_MEETING"))
                    scopedMeetingId = conversationMeeting;
            }
        }
        else if (normalizedMode == "AUTO" && request.UserId is Guid latestUser)
        {
            // The server remains the source of truth after Desktop restart;
            // no voice-history JSON is required to recover a follow-up.
            var conversation = await store.GetLatestAssistantConversationAsync(latestUser, scopedMeetingId, normalizedMode).ConfigureAwait(false);
            if (conversation is not null)
            {
                previousMode = NormalizePreviousMode(conversation.AssistantMode);
                if (scopedMeetingId is null && conversation.MeetingId is Guid conversationMeeting)
                    scopedMeetingId = conversationMeeting;
            }
        }

        if (normalizedMode != "AUTO")
            return await ResolveExplicitAsync(normalizedMode, scopedMeetingId, request.ConversationId).ConfigureAwait(false);

        var activeRecording = false;
        var liveContextAvailable = false;
        if (scopedMeetingId is Guid activeMeeting)
        {
            activeRecording = IsActiveCaptureState(request.CaptureState)
                || await store.HasActiveRecordingAsync(activeMeeting, request.RecordingSessionId).ConfigureAwait(false);
            // FINALIZING is no longer an active capture state, but its
            // provisional rows remain eligible until V1 is ready.
            liveContextAvailable = activeRecording
                || await store.HasLiveMeetingContextAsync(activeMeeting, request.RecordingSessionId).ConfigureAwait(false);
        }

        return await ResolveAutoAsync(
            question,
            scopedMeetingId,
            activeRecording,
            liveContextAvailable,
            request.RecordingSessionId,
            previousMode,
            request.UserId,
            request.Privileged,
            request.ConversationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compatibility adapter for callers that used the old static routing
    /// helper. Production endpoints use ResolveAsync above, which performs
    /// retrieval probes and live-memory checks against PostgreSQL.
    /// </summary>
    public static AssistantRequestRoute ResolveStatic(string query, string? requestedMode, Guid? activeMeetingId, bool privileged)
    {
        var normalized = NormalizeRequestedMode(requestedMode);
        if (normalized is null) return new("", 0, "ASSISTANT_MODE_INVALID");
        var result = ResolveStaticPure(query?.Trim() ?? string.Empty, normalized, activeMeetingId);
        return new(result.ResolvedMode, result.Confidence, result.ErrorCode, result.Clarification);
    }

    internal static bool IsGeneralConversationQuestion(string text)
    {
        var value = NormalizeText(text);
        return value is "привет" or "здравствуй" or "здравствуйте" or "добрый день" or "доброе утро" or "добрый вечер"
            or "скажи привет" or "как дела" or "как тебя зовут" or "кто ты" or "спасибо" or "спасибо мифодий"
            or "расскажи анекдот" or "пошути" or "поговори со мной"
            || value.StartsWith("что такое ", StringComparison.Ordinal)
            || value.StartsWith("объясни ", StringComparison.Ordinal)
            || value.StartsWith("как работает ", StringComparison.Ordinal)
            || value.StartsWith("напиши ", StringComparison.Ordinal)
            || value.StartsWith("переведи ", StringComparison.Ordinal)
            || value.StartsWith("расскажи анекдот ", StringComparison.Ordinal)
            || value.StartsWith("пошути ", StringComparison.Ordinal);
    }

    internal static bool LooksLikeMeetingQuestion(string text)
    {
        var value = NormalizeText(text);
        return value.Contains("совещан", StringComparison.Ordinal)
            || value.Contains("встреч", StringComparison.Ordinal)
            || value.Contains("стенограмм", StringComparison.Ordinal)
            || value.Contains("запис", StringComparison.Ordinal)
            || value.Contains("решили", StringComparison.Ordinal)
            || value.Contains("договорились", StringComparison.Ordinal)
            || value.Contains("поручен", StringComparison.Ordinal)
            || value.Contains("какой срок", StringComparison.Ordinal)
            || value.Contains("кто отвечал", StringComparison.Ordinal)
            || value.Contains("обсуждал", StringComparison.Ordinal)
            || value.Contains("по ремонту", StringComparison.Ordinal)
            || value.Contains("по насосу", StringComparison.Ordinal)
            || value.Contains("по истории", StringComparison.Ordinal);
    }

    internal static bool LooksLikeHistoryQuestion(string text)
    {
        var value = NormalizeText(text);
        return value.Contains("по истории", StringComparison.Ordinal)
            || value.Contains("когда обсуждали", StringComparison.Ordinal)
            || value.Contains("в прошлых совещаниях", StringComparison.Ordinal)
            || value.Contains("раньше обсуждали", StringComparison.Ordinal)
            || value.Contains("на прошлой встрече", StringComparison.Ordinal)
            || value.Contains("из прошлых встреч", StringComparison.Ordinal);
    }

    internal static bool IsEllipticalFollowUp(string text)
    {
        var value = NormalizeText(text);
        if (value.Length == 0 || value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8)
            return false;
        return value is "почему" or "подробнее" or "а подробнее" or "а ещё" or "а еще" or "а кто" or "а когда"
            or "а почему" or "а срок" or "а ответственный" or "кто отвечает" or "кто ответственный"
            or "какой срок" or "срок точно";
    }

    private async Task<AssistantModeResolution> ResolveExplicitAsync(
        string requestedMode,
        Guid? meetingId,
        Guid? conversationId)
    {
        if (requestedMode == "GENERAL_CHAT")
            return new("GENERAL_CHAT", null, "explicit_general_mode", 0.98, conversationId);
        if (requestedMode == "MEETING_HISTORY")
            return new("MEETING_MEMORY", null, "explicit_history_mode", 0.94, conversationId);
        if (requestedMode == "MEETING_MEMORY")
            return new("MEETING_MEMORY", null, "explicit_memory_mode", 0.94, conversationId);
        if (requestedMode == "LIVE_MEETING")
        {
            if (meetingId is not Guid liveMeeting)
                return MeetingRequired("LIVE_MEETING", conversationId);
            if (!await store.HasLiveMeetingContextAsync(liveMeeting).ConfigureAwait(false))
                return new("LIVE_MEETING", liveMeeting, "live_context_missing", 0.98, conversationId,
                    "LIVE_MEETING_NOT_READY", "Пока нет свежего фрагмента текущего совещания для ответа.");
            return new("LIVE_MEETING", liveMeeting, "explicit_live_mode", 0.98, conversationId);
        }
        if (requestedMode == "CURRENT_MEETING")
        {
            if (meetingId is not Guid currentMeeting)
                return MeetingRequired("CURRENT_MEETING", conversationId);
            return new("CURRENT_MEETING", currentMeeting, "explicit_current_mode", 0.98, conversationId);
        }

        // ResolveExplicitAsync is never called for AUTO; keep this branch
        // defensive so future mode additions fail closed.
        return Invalid("ASSISTANT_MODE_INVALID", conversationId);
    }

    private async Task<AssistantModeResolution> ResolveAutoAsync(
        string question,
        Guid? meetingId,
        bool activeRecording,
        bool liveContextAvailable,
        Guid? recordingSessionId,
        string? previousMode,
        Guid? userId,
        bool privileged,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        var normalizedQuestion = NormalizeText(question);
        var explicitGeneral = normalizedQuestion.Contains("общий вопрос", StringComparison.Ordinal)
            || normalizedQuestion.Contains("вне совещания", StringComparison.Ordinal)
            || normalizedQuestion.Contains("просто объясни", StringComparison.Ordinal);
        if (explicitGeneral)
            return new("GENERAL_CHAT", null, "explicit_general_request", 0.98, conversationId);

        // Greetings, definitions and other clearly general utterances should
        // not be captured by a coincidental word match in the current/live
        // transcript while a meeting is recording. Meeting-shaped wording
        // still wins (for example, "что решили по насосу").
        if (IsGeneralConversationQuestion(normalizedQuestion)
            && !LooksLikeMeetingQuestion(normalizedQuestion))
            return new("GENERAL_CHAT", null, "general_question", 0.94, conversationId);

        var historyHint = LooksLikeHistoryQuestion(normalizedQuestion);
        var elliptical = IsEllipticalFollowUp(normalizedQuestion);

        // A short follow-up inherits the existing conversation scope before
        // retrieval. The worker still has to find transcript/live evidence;
        // assistant history is never accepted as a fact source.
        if (elliptical && previousMode == "MEETING_MEMORY")
            return new("MEETING_MEMORY", null, "followup_scope_inherited", 0.82, conversationId);
        if (elliptical && previousMode == "CURRENT_MEETING" && meetingId is Guid currentFollowUpScope)
            return new("CURRENT_MEETING", currentFollowUpScope, "followup_scope_inherited", 0.84, conversationId);
        if (elliptical && previousMode == "LIVE_MEETING" && meetingId is Guid liveFollowUpScope && liveContextAvailable)
            return new("LIVE_MEETING", liveFollowUpScope, "followup_scope_inherited", 0.86, conversationId);

        // For a non-elliptical follow-up, inherit only after a fresh strong
        // retrieval match. A normal question cannot pull an arbitrary
        // meeting into the answer merely because a chat id was supplied.
        if (meetingId is Guid followUpMeeting)
        {
            if (liveContextAvailable && previousMode == "LIVE_MEETING")
            {
                var liveFollowUp = await store.ProbeLiveAssistantContextAsync(followUpMeeting, userId, privileged, question, recordingSessionId, cancellationToken).ConfigureAwait(false);
                if (liveFollowUp.HasStrongMatch)
                    return new("LIVE_MEETING", followUpMeeting, "followup_live_retrieval_match", 0.97, conversationId);
            }
            if (!activeRecording && previousMode == "CURRENT_MEETING")
            {
                var currentFollowUp = await store.ProbeCurrentMeetingAssistantContextAsync(followUpMeeting, userId, privileged, question, cancellationToken).ConfigureAwait(false);
                if (currentFollowUp.HasStrongMatch)
                    return new("CURRENT_MEETING", followUpMeeting, "followup_current_retrieval_match", 0.96, conversationId);
            }
        }
        if (previousMode == "MEETING_MEMORY")
        {
            var memoryFollowUp = await store.ProbeMeetingMemoryAssistantContextAsync(userId, privileged, question, cancellationToken).ConfigureAwait(false);
            if (memoryFollowUp.HasStrongMatch)
                return new("MEETING_MEMORY", null, "followup_history_retrieval_match", 0.94, conversationId);
        }

        // During recording live evidence gets the first chance. No live hit
        // is not an error: general conversation remains available while
        // capture is running, so routing continues to canonical/current and
        // then to the normal-chat fallback.
        if (liveContextAvailable && meetingId is Guid liveMeeting)
        {
            var live = await store.ProbeLiveAssistantContextAsync(liveMeeting, userId, privileged, question, recordingSessionId, cancellationToken).ConfigureAwait(false);
            if (live.HasStrongMatch)
                return new("LIVE_MEETING", liveMeeting, "live_retrieval_match", 0.95, conversationId);
        }

        if (meetingId is Guid currentMeeting)
        {
            var current = await store.ProbeCurrentMeetingAssistantContextAsync(currentMeeting, userId, privileged, question, cancellationToken).ConfigureAwait(false);
            if (current.HasStrongMatch)
                return new("CURRENT_MEETING", currentMeeting, "current_retrieval_match", 0.93, conversationId);
        }

        if (historyHint)
        {
            var history = await store.ProbeMeetingMemoryAssistantContextAsync(userId, privileged, question, cancellationToken).ConfigureAwait(false);
            if (history.HasStrongMatch)
                return new("MEETING_MEMORY", null, "history_retrieval_match", 0.90, conversationId);
            // A history-shaped question must stay in a meeting scope even
            // when retrieval finds nothing.  Falling back to GENERAL_CHAT
            // would let Qwen answer from world knowledge and make an
            // ungrounded meeting claim instead of returning NO_EVIDENCE.
            return new("MEETING_MEMORY", null, "history_no_evidence", 0.42, conversationId);
        }

        // Preserve the same fail-closed behaviour for a meeting-shaped
        // question when no meeting is currently selected.  If there are
        // usable transcripts the worker searches the user's history; if not,
        // the API rejects the request as context-not-ready.
        if (LooksLikeMeetingQuestion(normalizedQuestion))
            return new("MEETING_MEMORY", null, "meeting_question_without_scope", 0.40, conversationId);

        return new(
            "GENERAL_CHAT",
            null,
            IsGeneralConversationQuestion(normalizedQuestion) ? "general_conversation_fallback" : "general_fallback",
            IsGeneralConversationQuestion(normalizedQuestion) ? 0.76 : 0.60,
            conversationId);
    }

    private static AssistantModeResolution ResolveStaticPure(string question, string requestedMode, Guid? activeMeetingId)
    {
        var text = NormalizeText(question);
        if (requestedMode == "GENERAL_CHAT") return new("GENERAL_CHAT", null, "explicit_general_mode", 0.98);
        if (requestedMode is "MEETING_HISTORY" or "MEETING_MEMORY") return new("MEETING_MEMORY", null, "explicit_memory_mode", 0.94);
        if (requestedMode == "LIVE_MEETING") return activeMeetingId is Guid live ? new("LIVE_MEETING", live, "explicit_live_mode", 0.98) : MeetingRequired("LIVE_MEETING", null);
        if (requestedMode == "CURRENT_MEETING") return activeMeetingId is Guid current ? new("CURRENT_MEETING", current, "explicit_current_mode", 0.98) : MeetingRequired("CURRENT_MEETING", null);
        if (text.Contains("общий вопрос", StringComparison.Ordinal) || IsGeneralConversationQuestion(text))
            return new("GENERAL_CHAT", null, "general_conversation_fallback", 0.76);
        if (LooksLikeHistoryQuestion(text)) return new("MEETING_MEMORY", null, "history_hint_compatibility", 0.65);
        if (activeMeetingId is Guid meeting && LooksLikeMeetingQuestion(text)) return new("CURRENT_MEETING", meeting, "meeting_scope_compatibility", 0.60);
        if (LooksLikeMeetingQuestion(text)) return new("MEETING_MEMORY", null, "meeting_question_without_scope", 0.40);
        return new("GENERAL_CHAT", null, "general_fallback", 0.60);
    }

    private static string? NormalizeRequestedMode(string? requestedMode)
    {
        var value = string.IsNullOrWhiteSpace(requestedMode) ? "AUTO" : requestedMode.Trim().ToUpperInvariant();
        return AllowedModes.Contains(value) ? value : null;
    }

    private static string? NormalizePreviousMode(string? mode)
    {
        var value = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim().ToUpperInvariant();
        return value is "GENERAL_CHAT" or "MEETING_MEMORY" or "CURRENT_MEETING" or "LIVE_MEETING" ? value : null;
    }

    private static bool IsActiveCaptureState(string? captureState)
        => captureState is not null && ActiveCaptureStates.Contains(captureState.Trim().ToUpperInvariant());

    private static AssistantModeResolution MeetingRequired(string mode, Guid? conversationId)
        => new(mode, null, "meeting_context_required", 0.55, conversationId,
            "ASSISTANT_MEETING_REQUIRED", "Откройте нужное совещание, чтобы я отвечал по его контексту.");

    private static AssistantModeResolution Invalid(string errorCode, Guid? conversationId)
        => new("", null, "invalid_request", 0, conversationId, errorCode);

    private static string NormalizeText(string? text)
    {
        var cleaned = new string((text ?? string.Empty).Select(character => char.IsPunctuation(character) ? ' ' : character).ToArray());
        return string.Join(' ', cleaned.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

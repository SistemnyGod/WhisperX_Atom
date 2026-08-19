using System.IO.Pipes;
using System.Text.Json;
using WhisperX.Atom.Recorder;
using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>Desktop-owned broker for managed Voice Host intents.</summary>
public sealed class DesktopVoiceBrokerServer : IAsyncDisposable
{
    private readonly RecordingCommandService _commands;
    private readonly IBackendService _backend;
    private readonly ActiveMeetingContext _activeMeeting;
    private readonly VoiceAssistantConversationStore _voiceConversations;
    private readonly AssistantDeliveryStore _assistantDelivery;
    private readonly Action<Exception>? _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly object _commandCacheGate = new();
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, BrokerResponse Response)> _commandCache = new(StringComparer.Ordinal);
    private Task? _loop;
    private Task? _assistantDeliveryLoop;

    public event Action<Guid, Guid?>? AssistantResultAvailable;

    public DesktopVoiceBrokerServer(RecordingCommandService commands, IBackendService backend, ActiveMeetingContext activeMeeting, VoiceAssistantConversationStore voiceConversations, AssistantDeliveryStore? assistantDelivery = null, Action<Exception>? log = null)
    {
        _commands = commands;
        _backend = backend;
        _activeMeeting = activeMeeting;
        _voiceConversations = voiceConversations;
        _assistantDelivery = assistantDelivery ?? new AssistantDeliveryStore();
        _log = log;
    }

    public void Start()
    {
        _loop ??= Task.Run(RunAsync);
        _assistantDeliveryLoop ??= Task.Run(DeliverAssistantResultsAsync);
    }

    public void ClearAssistantState()
    {
        _assistantDelivery.ClearRuntimeEntries();
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                "WhisperXAtomDesktopVoiceBroker", PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                var response = string.IsNullOrWhiteSpace(line)
                    ? new BrokerResponse(false, "VOICE_COMMAND_REJECTED", Detail: "invalid_request")
                    : await HandleAsync(JsonDocument.Parse(line).RootElement, _shutdown.Token).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (Exception ex) { _log?.Invoke(ex); }
        }
    }

    private async Task<BrokerResponse> HandleAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("command", out var commandElement)
            || (commandElement.GetString() is not "EXECUTE_INTENT" and not "RECORD_EVENT" and not "ASSISTANT_QUESTION" and not "ASSISTANT_RESULT" and not "ASSISTANT_PLAYBACK_FINISHED"))
            return new(false, "VOICE_COMMAND_REJECTED", Detail: "unsupported_command");

        if (string.Equals(commandElement.GetString(), "ASSISTANT_PLAYBACK_FINISHED", StringComparison.OrdinalIgnoreCase))
        {
            var playbackQuery = root.TryGetProperty("queryId", out var playbackQueryElement) ? playbackQueryElement.GetString() : null;
            var playbackState = root.TryGetProperty("playbackState", out var playbackStateElement) ? playbackStateElement.GetString() : null;
            if (!Guid.TryParse(playbackQuery, out var playbackQueryId))
                return new(false, "VOICE_COMMAND_REJECTED", Detail: "query_id_invalid");
            if (string.Equals(playbackState, "PLAYED", StringComparison.OrdinalIgnoreCase)) _assistantDelivery.MarkDelivered(playbackQueryId);
            else if (string.Equals(playbackState, "CANCELLED", StringComparison.OrdinalIgnoreCase)) _assistantDelivery.MarkCancelled(playbackQueryId);
            else _assistantDelivery.MarkAmbiguous(playbackQueryId);
            var delivery = _assistantDelivery.Get(playbackQueryId);
            AssistantResultAvailable?.Invoke(playbackQueryId, Guid.TryParse(delivery?.ConversationId, out var conversationId) ? conversationId : null);
            return new(true, RecorderState: "ASSISTANT_PLAYBACK_FINISHED", QueryId: playbackQuery);
        }

        if (string.Equals(commandElement.GetString(), "ASSISTANT_QUESTION", StringComparison.OrdinalIgnoreCase))
        {
            var question = root.TryGetProperty("question", out var questionElement) ? questionElement.GetString() : null;
            var assistantTraceId = root.TryGetProperty("traceId", out var assistantTraceElement) ? assistantTraceElement.GetString() : null;
            var assistantCommandId = root.TryGetProperty("commandId", out var assistantCommandElementId) ? assistantCommandElementId.GetString() : null;
            if (!string.IsNullOrWhiteSpace(assistantCommandId))
            {
                lock (_commandCacheGate)
                {
                    var expired = _commandCache.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(item => item.Key).ToArray();
                    foreach (var key in expired) _commandCache.Remove(key);
                    if (_commandCache.TryGetValue(assistantCommandId, out var cached)) return cached.Response;
                }
            }
            var assistantTestMode = root.TryGetProperty("testMode", out var assistantTestElement) && assistantTestElement.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(question)) return new(false, "VOICE_COMMAND_REJECTED", Detail: "question_missing", TraceId: assistantTraceId, CommandId: assistantCommandId);
            if (assistantTestMode)
            {
                var test = new BrokerResponse(true, RecorderState: "TEST_ONLY", SpokenText: "Тест вопроса завершён", Detail: question, TraceId: assistantTraceId, CommandId: assistantCommandId);
                CacheCommand(assistantCommandId, test);
                return test;
            }
            if (!_backend.HasSession)
                return new(false, "VOICE_ASSISTANT_DESKTOP_REQUIRED", Detail: "desktop_api_session_missing", TraceId: assistantTraceId, CommandId: assistantCommandId);
            var requestedMode = root.TryGetProperty("requestedMode", out var modeElement) ? modeElement.GetString() : "AUTO";
            // A question must never compete with active capture. This applies
            // to general chat as well: Voice Host shares the same microphone
            // and TTS timeline as Recorder, while meeting answers must wait
            // for a completed transcript in any case.
            try
            {
                var recorderStatus = await _commands.StatusAsync(cancellationToken).ConfigureAwait(false);
                if (recorderStatus.State is "Recording" or "Paused" or "Starting" or "Finalizing"
                    || recorderStatus.SessionStatus?.CaptureState is "RECORDING" or "PAUSED")
                    return new(false, "ASSISTANT_RECORDING_ACTIVE", recorderStatus.State, TraceId: assistantTraceId, CommandId: assistantCommandId);
            }
            catch (Exception ex)
            {
                _log?.Invoke(ex);
                return new(false, "VOICE_RECORDER_UNAVAILABLE", Detail: "recording_state_unavailable", TraceId: assistantTraceId, CommandId: assistantCommandId);
            }
            var currentUser = await _backend.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
            if (currentUser is null)
                return new(false, "VOICE_ASSISTANT_DESKTOP_REQUIRED", Detail: "desktop_user_missing", TraceId: assistantTraceId, CommandId: assistantCommandId);
            var activeMeetingId = _activeMeeting.MeetingId;
            var scopedMode = VoiceScopeFor(requestedMode, activeMeetingId);
            var conversationId = _voiceConversations.Get(currentUser.Id, scopedMode, scopedMode == "CURRENT_MEETING" ? activeMeetingId : null);
            var accepted = await _backend.CreateAssistantRequestAsync(question, requestedMode, activeMeetingId, conversationId, "VOICE", assistantCommandId, assistantTraceId, cancellationToken).ConfigureAwait(false);
            if (accepted is null)
                return new(false, "VOICE_ASSISTANT_UNAVAILABLE", Detail: "assistant_request_rejected", TraceId: assistantTraceId, CommandId: assistantCommandId);
            if (Guid.TryParse(accepted.ConversationId, out var acceptedConversation))
                _voiceConversations.Set(currentUser.Id, accepted.ResolvedMode, Guid.TryParse(accepted.MeetingId, out var acceptedMeeting) ? acceptedMeeting : null, acceptedConversation);
            if (Guid.TryParse(accepted.QueryId, out var queryId))
            {
                if (!_assistantDelivery.TryAdd(queryId, currentUser.Id.ToString(), assistantCommandId, assistantTraceId, accepted.ResolvedMode, accepted.ConversationId))
                    return new(false, "VOICE_ASSISTANT_QUEUE_FULL", Detail: "assistant_delivery_capacity", TraceId: assistantTraceId, CommandId: assistantCommandId);
            }
            var assistantResponse = new BrokerResponse(true, RecorderState: "ASSISTANT_QUEUED", SpokenText: "Вопрос принят, отвечу после обработки", Detail: accepted.ResolvedMode, TraceId: assistantTraceId, CommandId: assistantCommandId, QueryId: accepted.QueryId, AssistantStatus: accepted.Status, ResolvedMode: accepted.ResolvedMode);
            CacheCommand(assistantCommandId, assistantResponse);
            return assistantResponse;
        }

        if (string.Equals(commandElement.GetString(), "ASSISTANT_RESULT", StringComparison.OrdinalIgnoreCase))
        {
            var queryText = root.TryGetProperty("queryId", out var queryElement) ? queryElement.GetString() : null;
            var resultTraceId = root.TryGetProperty("traceId", out var resultTraceElement) ? resultTraceElement.GetString() : null;
            var resultCommandId = root.TryGetProperty("commandId", out var resultCommandElementId) ? resultCommandElementId.GetString() : null;
            if (!Guid.TryParse(queryText, out var queryId)) return new(false, "VOICE_COMMAND_REJECTED", Detail: "query_id_invalid", TraceId: resultTraceId, CommandId: resultCommandId);
            if (!_backend.HasSession) return new(false, "VOICE_ASSISTANT_DESKTOP_REQUIRED", TraceId: resultTraceId, CommandId: resultCommandId);
            var query = await _backend.GetAssistantQueryAsync(queryId, cancellationToken).ConfigureAwait(false);
            if (query is null) return new(false, "VOICE_ASSISTANT_UNAVAILABLE", Detail: "query_not_found", TraceId: resultTraceId, CommandId: resultCommandId);
            var terminal = query.Status is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING" or "NEEDS_REVIEW" or "FAILED" or "NO_EVIDENCE" or "GROUNDING_REJECTED" or "LLM_UNAVAILABLE";
            var assistantSpoken = terminal && !string.IsNullOrWhiteSpace(query.VoiceAnswer ?? query.Answer) ? query.VoiceAnswer ?? query.Answer : null;
            return new(terminal && assistantSpoken is not null, terminal && assistantSpoken is not null ? null : query.ErrorCode ?? (terminal ? "ASSISTANT_NO_GROUNDED_ANSWER" : null), query.Status, SpokenText: assistantSpoken, Detail: query.Status, TraceId: resultTraceId, CommandId: resultCommandId, QueryId: queryText, AssistantStatus: query.Status, ResolvedMode: query.AssistantMode);
        }

        if (string.Equals(commandElement.GetString(), "RECORD_EVENT", StringComparison.OrdinalIgnoreCase))
        {
            var eventType = root.TryGetProperty("eventType", out var typeElement) ? typeElement.GetString() : null;
            var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement : default;
            var eventTraceId = root.TryGetProperty("traceId", out var eventTrace) ? eventTrace.GetString() : null;
            var localSessionId = root.TryGetProperty("localSessionId", out var sessionElement) ? sessionElement.GetString() : null;
            try
            {
                // Recorder IPC v6 accepts the event payload. Keep this call
                // best-effort so telemetry never blocks capture or TTS.
                var client = new WhisperX.Atom.Desktop.AgentPipeClient();
                // Preserve the broker trace in the durable event payload so
                // the server can correlate VOICE_COMMAND/TTS intervals with
                // the originating EXECUTE_INTENT request.
                var durablePayload = eventTraceId is null
                    ? payload
                    : JsonSerializer.SerializeToElement(new { traceId = eventTraceId, payload }, _json);
                var eventAck = await client.SendAsync("VOICE_EVENT", new { eventType, payload = durablePayload, localSessionId }, cancellationToken).ConfigureAwait(false);
                return new(eventAck.Ok, eventAck.Error, eventAck.State, eventAck.SessionId, eventAck.SessionStatus?.LocalFinalizeState, TraceId: eventTraceId);
            }
            catch (Exception ex) { return new(false, "VOICE_RECORDER_UNAVAILABLE", Detail: ex.GetType().Name, TraceId: eventTraceId); }
        }

        var intent = root.TryGetProperty("intent", out var intentElement) ? intentElement.GetString() : null;
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        var traceId = root.TryGetProperty("traceId", out var traceElement) ? traceElement.GetString() : null;
        var commandId = root.TryGetProperty("commandId", out var commandIdElement) ? commandIdElement.GetString() : null;
        var testMode = root.TryGetProperty("testMode", out var testElement) && testElement.ValueKind == JsonValueKind.True;
        if (!string.IsNullOrWhiteSpace(commandId))
        {
            lock (_commandCacheGate)
            {
                var expired = _commandCache.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(item => item.Key).ToArray();
                foreach (var key in expired) _commandCache.Remove(key);
                if (_commandCache.TryGetValue(commandId, out var cached)) return cached.Response;
            }
        }
        if (string.IsNullOrWhiteSpace(intent)) return new(false, "VOICE_COMMAND_REJECTED", Detail: "intent_missing", TraceId: traceId, CommandId: commandId);
        if (testMode)
        {
            var testResponse = new BrokerResponse(true, RecorderState: "TEST_ONLY", SpokenText: "Тест распознавания завершён", Detail: $"intent={intent};text={text}", TraceId: traceId, CommandId: commandId);
            CacheCommand(commandId, testResponse);
            return testResponse;
        }

        AgentIpcResponse response;
        try
        {
            response = intent.ToUpperInvariant() switch
            {
                "STARTRECORDING" => await _commands.StartAsync("Голосовая запись", null, cancellationToken: cancellationToken).ConfigureAwait(false),
                "STOPRECORDING" => await _commands.StopAsync(cancellationToken).ConfigureAwait(false),
                "PAUSERECORDING" => await _commands.PauseAsync(cancellationToken).ConfigureAwait(false),
                "RESUMERECORDING" => await _commands.ResumeAsync(cancellationToken).ConfigureAwait(false),
                "ADDMARKER" => await _commands.MarkerAsync("MARKER", cancellationToken).ConfigureAwait(false),
                "MARKDECISION" => await _commands.MarkerAsync("DECISION", cancellationToken).ConfigureAwait(false),
                "MARKACTIONITEM" => await _commands.MarkerAsync("ACTION_ITEM", cancellationToken).ConfigureAwait(false),
                "GETSTATUS" => await _commands.StatusAsync(cancellationToken).ConfigureAwait(false),
                _ => new AgentIpcResponse(false, "ERROR", null, "VOICE_COMMAND_REJECTED", null)
            };
        }
        catch (RecorderIpcException ex)
        {
            return new(false, ex.ErrorCode, Detail: ex.Message, TraceId: traceId, CommandId: commandId);
        }
        catch (IOException ex)
        {
            return new(false, "VOICE_RECORDER_UNAVAILABLE", Detail: ex.Message, TraceId: traceId, CommandId: commandId);
        }
        catch (Exception ex)
        {
            return new(false, "VOICE_DESKTOP_BROKER_UNAVAILABLE", Detail: ex.GetType().Name, TraceId: traceId, CommandId: commandId);
        }

        var localReady = response.SessionStatus?.LocalFinalizeState;
        var spoken = response.Ok
            ? intent.ToUpperInvariant() switch
            {
                "STARTRECORDING" => "Запись начата",
                "STOPRECORDING" when string.Equals(localReady, "LOCAL_READY", StringComparison.OrdinalIgnoreCase) => "Запись остановлена и сохранена",
                "STOPRECORDING" when string.Equals(localReady, "RECOVERY_PENDING", StringComparison.OrdinalIgnoreCase) => "Запись остановлена; локальный файл восстанавливается",
                "STOPRECORDING" when string.Equals(localReady, "LOCAL_FAILED", StringComparison.OrdinalIgnoreCase) => "Запись остановлена, аудио не удалось подтвердить",
                "STOPRECORDING" => "Запись остановлена, локальное сохранение продолжается",
                "PAUSERECORDING" => "Запись приостановлена",
                "RESUMERECORDING" => "Запись продолжена",
                "ADDMARKER" => "Метка установлена",
                "MARKDECISION" => "Решение отмечено",
                "MARKACTIONITEM" => "Поручение отмечено",
                "GETSTATUS" => $"Состояние записи: {response.State}",
                _ => "Команда выполнена"
            }
            : null;
        var brokerResponse = new BrokerResponse(response.Ok, response.Error ?? (response.Ok ? null : "VOICE_COMMAND_REJECTED"), response.State,
            response.SessionId, localReady, spoken, response.ErrorDetail, traceId, commandId);
        CacheCommand(commandId, brokerResponse);
        return brokerResponse;
    }

    private void CacheCommand(string? commandId, BrokerResponse response)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;
        lock (_commandCacheGate)
        {
            if (_commandCache.Count >= 256)
            {
                var oldest = _commandCache.OrderBy(item => item.Value.ExpiresAt).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(oldest.Key)) _commandCache.Remove(oldest.Key);
            }
            _commandCache[commandId] = (DateTimeOffset.UtcNow.AddMinutes(10), response);
        }
    }

    private async Task DeliverAssistantResultsAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var user = await _backend.GetCurrentUserAsync(_shutdown.Token).ConfigureAwait(false);
                foreach (var item in _assistantDelivery.GetForUser(user?.Id.ToString()))
                    await TryDeliverAssistantResultAsync(item, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (Exception ex) { _log?.Invoke(ex); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
        }
    }

    private async Task TryDeliverAssistantResultAsync(AssistantDeliveryEntry pending, CancellationToken cancellationToken)
    {
        if (!_backend.HasSession) return;
        var query = await _backend.GetAssistantQueryAsync(pending.QueryId, cancellationToken).ConfigureAwait(false);
        if (query is null) return;
        var terminal = query.Status is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING" or "NEEDS_REVIEW" or "FAILED" or "NO_EVIDENCE" or "GROUNDING_REJECTED" or "LLM_UNAVAILABLE";
        if (!terminal) return;
        AssistantResultAvailable?.Invoke(pending.QueryId, Guid.TryParse(pending.ConversationId, out var resultConversation) ? resultConversation : null);
        var voiceAnswer = !string.IsNullOrWhiteSpace(query.VoiceAnswer ?? query.Answer)
            ? query.VoiceAnswer ?? query.Answer
            : AssistantErrorSpeech(query.ErrorCode ?? query.Status);
        if (string.IsNullOrWhiteSpace(voiceAnswer))
        {
            _assistantDelivery.MarkCompletedWithoutSpeech(pending.QueryId);
            AssistantResultAvailable?.Invoke(pending.QueryId, Guid.TryParse(pending.ConversationId, out var noSpeechConversation) ? noSpeechConversation : null);
            return;
        }
        // Claim before sending. If Desktop terminates after this point, the
        // ambiguous dispatch is deliberately not replayed after restart.
        var reconcilingAcceptedPlayback = pending.State == AssistantDeliveryState.Accepted;
        if (!reconcilingAcceptedPlayback && !_assistantDelivery.TryClaim(pending.QueryId)) return;
        try
        {
            var response = await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync("SPEAK_ASSISTANT_RESULT", new
            {
                queryId = pending.QueryId,
                commandId = pending.CommandId,
                traceId = pending.TraceId,
                voiceAnswer,
                status = query.Status
            }, cancellationToken).ConfigureAwait(false);
            if (response.Ok)
            {
                // A duplicate queryId is acknowledged by Voice Host without
                // replaying speech. Its playbackState tells us whether the
                // original request already finished, was cancelled, or is
                // still only accepted in the queue.
                var playbackState = response.Data is JsonElement data
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("playbackState", out var state)
                    ? state.GetString()
                    : null;
                switch (playbackState?.ToUpperInvariant())
                {
                    case "PLAYED": _assistantDelivery.MarkDelivered(pending.QueryId); break;
                    case "CANCELLED": _assistantDelivery.MarkCancelled(pending.QueryId); break;
                    case "FAILED":
                    case "AMBIGUOUS":
                    case "RESERVED": _assistantDelivery.MarkAmbiguous(pending.QueryId); break;
                    default: _assistantDelivery.MarkAccepted(pending.QueryId); break;
                }
            }
            else if (response.Error is "VOICE_HOST_BUSY" or "VOICE_ASSISTANT_QUEUE_FULL" or "VOICE_PLAYBACK_LEDGER_UNAVAILABLE")
                _assistantDelivery.ResetToPending(pending.QueryId);
            else
                _assistantDelivery.MarkAmbiguous(pending.QueryId);
        }
        catch (VoiceHostIpcException ex) when (!ex.RequestWritten) { _assistantDelivery.ResetToPending(pending.QueryId); }
        catch (VoiceHostIpcException) { _assistantDelivery.MarkAmbiguous(pending.QueryId); }
        catch (IOException) { _assistantDelivery.MarkAmbiguous(pending.QueryId); }
        catch (TimeoutException) { _assistantDelivery.MarkAmbiguous(pending.QueryId); }
    }

    private static string? AssistantErrorSpeech(string? errorCode) => errorCode switch
    {
        "NO_EVIDENCE" => "В стенограмме не найден подтверждённый ответ.",
        "LOW_TRANSCRIPT_QUALITY" => "Сначала проверьте качество стенограммы.",
        "GROUNDING_REJECTED" => "Не удалось подтвердить ответ по стенограмме.",
        _ => null
    };

    private static string VoiceScopeFor(string? requestedMode, Guid? activeMeetingId)
    {
        var mode = (requestedMode ?? "AUTO").Trim().ToUpperInvariant();
        if (mode == "MEETING_HISTORY") return "MEETING_MEMORY";
        if (mode is "CURRENT_MEETING" or "GENERAL_CHAT" or "MEETING_MEMORY") return mode;
        return activeMeetingId.HasValue ? "CURRENT_MEETING" : "GENERAL_CHAT";
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { if (_assistantDeliveryLoop is not null) await _assistantDeliveryLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _shutdown.Dispose();
    }

    private sealed record BrokerResponse(bool Ok, string? ErrorCode = null, string? RecorderState = null,
        string? LocalSessionId = null, string? LocalFinalizeState = null, string? SpokenText = null, string? Detail = null, string? TraceId = null, string? CommandId = null, string? QueryId = null, string? AssistantStatus = null, string? ResolvedMode = null);
}

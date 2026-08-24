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
    private readonly Action<string>? _messageLog;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly object _commandCacheGate = new();
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, BrokerResponse Response)> _commandCache = new(StringComparer.Ordinal);
    private Task? _loop;
    private Task? _assistantDeliveryLoop;

    public event Action<Guid, Guid?>? AssistantResultAvailable;

    public DesktopVoiceBrokerServer(RecordingCommandService commands, IBackendService backend, ActiveMeetingContext activeMeeting, VoiceAssistantConversationStore voiceConversations, AssistantDeliveryStore? assistantDelivery = null, Action<Exception>? log = null, Action<string>? messageLog = null)
    {
        _commands = commands;
        _backend = backend;
        _activeMeeting = activeMeeting;
        _voiceConversations = voiceConversations;
        _assistantDelivery = assistantDelivery ?? new AssistantDeliveryStore();
        _log = log;
        _messageLog = messageLog;
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
            || (commandElement.GetString() is not "EXECUTE_INTENT" and not "RECORD_EVENT" and not "LIVE_ASR_SEGMENTS" and not "ASSISTANT_QUESTION" and not "ASSISTANT_RESULT" and not "ASSISTANT_PLAYBACK_FINISHED"))
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

        if (string.Equals(commandElement.GetString(), "LIVE_ASR_SEGMENTS", StringComparison.OrdinalIgnoreCase))
        {
            if (!_backend.HasSession) return new(false, "VOICE_ASSISTANT_DESKTOP_REQUIRED", Detail: "desktop_api_session_missing");
            var liveMeetingId = _activeMeeting.MeetingId;
            if (liveMeetingId is null) return new(false, "LIVE_MEETING_REQUIRED", Detail: "active_meeting_missing");
            if (!root.TryGetProperty("segments", out var segmentsElement) || segmentsElement.ValueKind != JsonValueKind.Array)
                return new(false, "VOICE_COMMAND_REJECTED", Detail: "live_segments_missing");
            List<DesktopLiveMeetingSegment>? segments;
            try { segments = JsonSerializer.Deserialize<List<DesktopLiveMeetingSegment>>(segmentsElement.GetRawText(), _json); }
            catch (JsonException) { segments = null; }
            if (segments is null || segments.Count == 0 || segments.Count > 64)
                return new(false, "VOICE_COMMAND_REJECTED", Detail: "live_segments_invalid");
            Guid? recordingSessionId = null;
            if (root.TryGetProperty("recordingSessionId", out var sessionElement)
                && sessionElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(sessionElement.GetString(), out var parsedSession))
                recordingSessionId = parsedSession;
            var accepted = await _backend.PublishLiveMeetingSegmentsAsync(liveMeetingId.Value, recordingSessionId, segments, cancellationToken).ConfigureAwait(false);
            return accepted
                ? new(true, RecorderState: "LIVE_ASR_ACCEPTED", Detail: $"segments={segments.Count}")
                : new(false, "LIVE_MEETING_NOT_ACTIVE", Detail: "live_asr_not_ready");
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
            // Voice questions always use AUTO.  The recording state below is
            // context only; it is never promoted to LIVE_MEETING by Desktop.
            // The authenticated API owns that decision after retrieval.
            const string requestedMode = "AUTO";
            var captureContext = await ReadCaptureContextAsync(cancellationToken).ConfigureAwait(false);
            var activeMeetingId = _activeMeeting.MeetingId;
            // Voice Host and Desktop are transport layers only.  Do not ask
            // Recorder for state here and do not classify the question with a
            // second heuristic.  The authenticated API owns AUTO routing and
            // the live-context safety gate (including ACTIVE_RECORDING →
            // LIVE_MEETING).  This keeps general conversation available while
            // capture is running and prevents client/server routing drift.
            var currentUser = await _backend.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
            if (currentUser is null)
                return new(false, "VOICE_ASSISTANT_DESKTOP_REQUIRED", Detail: "desktop_user_missing", TraceId: assistantTraceId, CommandId: assistantCommandId);
            // The API is the sole owner of AUTO routing. Desktop only carries
            // an opaque conversation key so voice and text clients share the
            // same server-side mode resolver.
            var requestMeetingId = captureContext.IsActive && captureContext.MeetingId is Guid captureMeeting
                ? captureMeeting
                : activeMeetingId ?? captureContext.MeetingId;
            var conversationId = GetVoiceConversationId(currentUser.Id, requestedMode, requestMeetingId);
            if (!string.IsNullOrWhiteSpace(assistantCommandId)
                && !_assistantDelivery.TryAddCommand(assistantCommandId, currentUser.Id.ToString(), assistantTraceId))
                return new(false, "VOICE_ASSISTANT_QUEUE_FULL", Detail: "assistant_delivery_capacity", TraceId: assistantTraceId, CommandId: assistantCommandId);
            DesktopAssistantRequestAccepted? accepted;
            using var acceptanceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acceptanceTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                accepted = await _backend.CreateAssistantRequestAsync(
                    question,
                    requestedMode,
                    requestMeetingId,
                    conversationId,
                    "VOICE",
                    assistantCommandId,
                    assistantTraceId,
                    captureContext.RecordingSessionId,
                    captureContext.CaptureState,
                    previousResolvedMode: null,
                    cancellationToken: acceptanceTimeout.Token).ConfigureAwait(false);
            }
            catch (DesktopApiException exception)
            {
                var error = MapAssistantApiError(exception);
                var rejected = new BrokerResponse(false, error,
                    Detail: exception.Message, TraceId: exception.TraceId ?? assistantTraceId, CommandId: assistantCommandId);
                CacheCommand(assistantCommandId, rejected);
                return rejected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var reconciled = await ReconcileAssistantRequestAsync(assistantCommandId, cancellationToken).ConfigureAwait(false);
                if (reconciled is not null) { accepted = reconciled; goto accepted_request; }
                return new BrokerResponse(true, RecorderState: "ASSISTANT_RECONCILING", Detail: "assistant_acceptance_pending", TraceId: assistantTraceId, CommandId: assistantCommandId);
            }
            catch (HttpRequestException)
            {
                var reconciled = await ReconcileAssistantRequestAsync(assistantCommandId, cancellationToken).ConfigureAwait(false);
                if (reconciled is not null) { accepted = reconciled; goto accepted_request; }
                return new BrokerResponse(true, RecorderState: "ASSISTANT_RECONCILING", Detail: "assistant_acceptance_pending", TraceId: assistantTraceId, CommandId: assistantCommandId);
            }
        accepted_request:
            if (accepted is null)
            {
                var rejected = new BrokerResponse(false, "VOICE_ASSISTANT_REQUEST_REJECTED", Detail: "assistant_request_rejected", TraceId: assistantTraceId, CommandId: assistantCommandId);
                CacheCommand(assistantCommandId, rejected);
                return rejected;
            }
            if (Guid.TryParse(accepted.ConversationId, out var acceptedConversation))
            {
                var acceptedMeeting = Guid.TryParse(accepted.MeetingId, out var parsedMeeting) ? parsedMeeting : (Guid?)null;
                // AUTO is a transport key, not a client-side mode decision.
                // Keep the resolved key as a compatibility entry for older
                // Desktop state, while follow-ups use the opaque AUTO key.
                _voiceConversations.Set(currentUser.Id, "AUTO", acceptedMeeting, acceptedConversation);
                _voiceConversations.Set(currentUser.Id, accepted.ResolvedMode, acceptedMeeting, acceptedConversation);
            }
            if (Guid.TryParse(accepted.QueryId, out var queryId))
            {
                if (!string.IsNullOrWhiteSpace(assistantCommandId))
                    _assistantDelivery.BindQuery(assistantCommandId, queryId, accepted.ResolvedMode, accepted.ConversationId);
                else if (!_assistantDelivery.TryAdd(queryId, currentUser.Id.ToString(), assistantCommandId, assistantTraceId, accepted.ResolvedMode, accepted.ConversationId))
                    return new(false, "VOICE_ASSISTANT_QUEUE_FULL", Detail: "assistant_delivery_capacity", TraceId: assistantTraceId, CommandId: assistantCommandId);
            }
            // Acceptance is intentionally silent.  The grounded result is
            // delivered later through ASSISTANT_RESULT; speaking a generic
            // acknowledgement here makes older Voice Hosts talk over the
            // user's conversation and creates a false second response.
            var assistantResponse = new BrokerResponse(true, RecorderState: "ASSISTANT_QUEUED", SpokenText: null, Detail: accepted.ResolvedMode, TraceId: assistantTraceId, CommandId: assistantCommandId, QueryId: accepted.QueryId, AssistantStatus: accepted.Status, ResolvedMode: accepted.ResolvedMode);
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
            // A durable request can be committed before the read replica or
            // API lookup path exposes the query.  It is not an assistant
            // outage and must not be converted into the generic
            // "Помощник временно недоступен" speech.  Keep the command in
            // reconciliation; the delivery loop will retry by commandId and
            // the exactly-once playback ledger remains authoritative.
            if (query is null)
                return new(true, RecorderState: "ASSISTANT_RECONCILING", Detail: "assistant_query_not_visible_yet", TraceId: resultTraceId, CommandId: resultCommandId, QueryId: queryText, AssistantStatus: "QUEUED");
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

        if (intent.ToUpperInvariant() is "GETSERVERSTATUS" or "GETPIPELINESTATUS" or "GETSTORAGESTATUS")
        {
            var localStatus = await GetLocalStatusAsync(intent.ToUpperInvariant(), traceId, commandId, cancellationToken).ConfigureAwait(false);
            CacheCommand(commandId, localStatus);
            return localStatus;
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

    private async Task<BrokerResponse> GetLocalStatusAsync(string intent, string? traceId, string? commandId, CancellationToken cancellationToken)
    {
        try
        {
            switch (intent)
            {
                case "GETSERVERSTATUS":
                {
                    if (!_backend.HasSession)
                        return new(false, "VOICE_ASSISTANT_AUTH_REQUIRED", SpokenText: "Для проверки сервера требуется вход в Desktop.", TraceId: traceId, CommandId: commandId);
                    var status = await _backend.GetSystemStatusAsync(cancellationToken).ConfigureAwait(false);
                    if (status is null)
                        return new(false, "VOICE_ASSISTANT_SERVER_UNREACHABLE", SpokenText: "Сервер недоступен.", TraceId: traceId, CommandId: commandId);
                    return new(true, RecorderState: status.Ready ? "READY" : "DEGRADED",
                        SpokenText: status.Ready ? "Сервер доступен и готов." : "Сервер отвечает, но ещё не готов.",
                        Detail: $"postgres={status.Postgres}", TraceId: traceId, CommandId: commandId);
                }
                case "GETPIPELINESTATUS":
                {
                    if (!_backend.HasSession)
                        return new(false, "VOICE_ASSISTANT_AUTH_REQUIRED", SpokenText: "Для проверки обработки требуется вход в Desktop.", TraceId: traceId, CommandId: commandId);
                    var readiness = await _backend.GetProcessingReadinessAsync(cancellationToken).ConfigureAwait(false);
                    if (readiness is null)
                        return new(false, "VOICE_ASSISTANT_SERVER_UNREACHABLE", SpokenText: "Состояние обработки пока недоступно.", TraceId: traceId, CommandId: commandId);
                    var busy = readiness.Components.ValueKind == JsonValueKind.Object
                        && readiness.Components.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object
                            && p.Value.TryGetProperty("status", out var s)
                            && string.Equals(s.GetString(), "BUSY", StringComparison.OrdinalIgnoreCase));
                    var spoken = !readiness.Ready
                        ? "Обработка требует восстановления."
                        : busy ? "Обработка доступна, часть GPU сейчас занята." : "Обработка готова.";
                    return new(true, RecorderState: readiness.Ready ? "READY" : "DEGRADED", SpokenText: spoken,
                        Detail: busy ? "BUSY" : "READY", TraceId: traceId, CommandId: commandId);
                }
                case "GETSTORAGESTATUS":
                {
                    var health = await _commands.StatusAsync(cancellationToken).ConfigureAwait(false);
                    if (!health.Ok || health.Health is null)
                        return new(false, health.Error ?? "VOICE_RECORDER_UNAVAILABLE", SpokenText: "Состояние локального хранилища недоступно.", TraceId: traceId, CommandId: commandId);
                    var freeGb = health.Health.FreeBytes / 1024d / 1024d / 1024d;
                    return new(true, RecorderState: "READY", SpokenText: $"Свободно на диске: {freeGb:0.0} гигабайт.",
                        Detail: $"freeBytes={health.Health.FreeBytes};totalBytes={health.Health.TotalBytes}", TraceId: traceId, CommandId: commandId);
                }
                default:
                    return new(false, "VOICE_COMMAND_REJECTED", TraceId: traceId, CommandId: commandId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return new(false, "VOICE_STATUS_UNAVAILABLE", Detail: exception.GetType().Name, TraceId: traceId, CommandId: commandId);
        }
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

    private async Task<AssistantCaptureContext> ReadCaptureContextAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Recorder health is context enrichment, not a prerequisite for
            // a conversational answer. Keep an unavailable pipe from making
            // "Мифодий, привет" wait on the IPC timeout.
            using var contextCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            contextCts.CancelAfter(TimeSpan.FromSeconds(1));
            var response = await _commands.StatusAsync(contextCts.Token).ConfigureAwait(false);
            var state = response.SessionStatus?.CaptureState ?? response.State;
            var normalized = string.IsNullOrWhiteSpace(state) ? "UNKNOWN" : state.Trim().ToUpperInvariant();
            var isActive = normalized is "STARTING" or "RECORDING" or "PAUSED" or "AWAITING_AGENT_RECONNECT" or "FINALIZING";
            return new AssistantCaptureContext(
                response.MeetingId ?? response.SessionStatus?.MeetingId,
                Guid.TryParse(response.SessionId ?? response.SessionStatus?.SessionId, out var sessionId) ? sessionId : null,
                normalized,
                isActive);
        }
        catch
        {
            // A missing Recorder IPC must not block normal conversation. The API
            // still receives UNKNOWN and can consult its durable session row.
            return new AssistantCaptureContext(null, null, "UNKNOWN", false);
        }
    }

    private async Task<DesktopAssistantRequestAccepted?> ReconcileAssistantRequestAsync(string? commandId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commandId) || !_backend.HasSession) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var accepted = await _backend.GetAssistantRequestByCommandAsync(commandId, timeout.Token).ConfigureAwait(false);
                if (accepted is not null) return accepted;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
            catch (HttpRequestException) { }
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
        }
        return null;
    }

    private async Task ReconcilePendingAssistantRequestsAsync(string? userId, CancellationToken cancellationToken)
    {
        if (!_backend.HasSession) return;
        foreach (var item in _assistantDelivery.GetForReconciliation(userId))
        {
            if (string.IsNullOrWhiteSpace(item.CommandId)) continue;
            try
            {
                var accepted = await _backend.GetAssistantRequestByCommandAsync(item.CommandId, cancellationToken).ConfigureAwait(false);
                if (accepted is not null && Guid.TryParse(accepted.QueryId, out var queryId))
                    _assistantDelivery.BindQuery(item.CommandId, queryId, accepted.ResolvedMode, accepted.ConversationId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log?.Invoke(ex); }
        }
    }

    private async Task DeliverAssistantResultsAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var user = await _backend.GetCurrentUserAsync(_shutdown.Token).ConfigureAwait(false);
                if (user is null)
                {
                    LogMessage("ASSISTANT_DELIVERY_AUTH_OR_USER_UNAVAILABLE");
                    await Task.Delay(TimeSpan.FromSeconds(3), _shutdown.Token).ConfigureAwait(false);
                    continue;
                }
                await ReconcilePendingAssistantRequestsAsync(user.Id.ToString(), _shutdown.Token).ConfigureAwait(false);
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
        if (pending.QueryId is not Guid pendingQueryId) return;
        var query = await _backend.GetAssistantQueryAsync(pendingQueryId, cancellationToken).ConfigureAwait(false);
        if (query is null) return;
        var terminal = query.Status is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING" or "NEEDS_REVIEW" or "FAILED" or "NO_EVIDENCE" or "GROUNDING_REJECTED" or "LLM_UNAVAILABLE";
        if (!terminal) return;
        LogMessage($"ASSISTANT_DELIVERY_TERMINAL queryId={pendingQueryId:N} status={query.Status} error={query.ErrorCode ?? "none"}");
        AssistantResultAvailable?.Invoke(pendingQueryId, Guid.TryParse(pending.ConversationId, out var resultConversation) ? resultConversation : null);
        // A worker must never be able to turn a failed grounding decision
        // into a confident spoken fact by populating VoiceAnswer.  For these
        // terminal statuses the broker owns the wording and only emits the
        // short, explicitly negative response below.  The full answer stays
        // available in Desktop for diagnostics/review, but is not sent to
        // Voice Host.
        var groundingRejected = query.Status is not ("READY" or "ANSWERED" or "ANSWERED_WITH_WARNING");
        var voiceAnswer = groundingRejected
            ? AssistantErrorSpeech(query.ErrorCode ?? query.Status)
            : !string.IsNullOrWhiteSpace(query.VoiceAnswer ?? query.Answer)
                ? query.VoiceAnswer ?? query.Answer
                : AssistantErrorSpeech(query.ErrorCode ?? query.Status);
        if (string.IsNullOrWhiteSpace(voiceAnswer))
        {
            _assistantDelivery.MarkCompletedWithoutSpeech(pendingQueryId);
            AssistantResultAvailable?.Invoke(pendingQueryId, Guid.TryParse(pending.ConversationId, out var noSpeechConversation) ? noSpeechConversation : null);
            return;
        }
        // Claim before sending. If Desktop terminates after this point, the
        // ambiguous dispatch is deliberately not replayed after restart.
        var reconcilingAcceptedPlayback = pending.State == AssistantDeliveryState.Accepted;
        if (!reconcilingAcceptedPlayback && !_assistantDelivery.TryClaim(pendingQueryId)) return;
        try
        {
            var response = await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync("SPEAK_ASSISTANT_RESULT", new
            {
                queryId = pendingQueryId,
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
                    case "PLAYED": _assistantDelivery.MarkDelivered(pendingQueryId); LogMessage($"ASSISTANT_DELIVERY_PLAYED queryId={pendingQueryId:N}"); break;
                    case "CANCELLED": _assistantDelivery.MarkCancelled(pendingQueryId); LogMessage($"ASSISTANT_DELIVERY_CANCELLED queryId={pendingQueryId:N}"); break;
                    case "FAILED":
                    case "AMBIGUOUS":
                    case "RESERVED": _assistantDelivery.MarkAmbiguous(pendingQueryId); LogMessage($"ASSISTANT_DELIVERY_AMBIGUOUS queryId={pendingQueryId:N} playbackState={playbackState}"); break;
                    default: _assistantDelivery.MarkAccepted(pendingQueryId); LogMessage($"ASSISTANT_DELIVERY_ACCEPTED queryId={pendingQueryId:N} playbackState={playbackState ?? "missing"}"); break;
                }
            }
            else if (response.Error is "VOICE_HOST_BUSY" or "VOICE_ASSISTANT_QUEUE_FULL" or "VOICE_PLAYBACK_LEDGER_UNAVAILABLE")
            {
                _assistantDelivery.ResetToPending(pendingQueryId);
                LogMessage($"ASSISTANT_DELIVERY_RETRYABLE_FAILURE queryId={pendingQueryId:N} error={response.Error}");
            }
            else
            {
                _assistantDelivery.MarkAmbiguous(pendingQueryId);
                LogMessage($"ASSISTANT_DELIVERY_AMBIGUOUS queryId={pendingQueryId:N} error={response.Error ?? "unknown"}");
            }
        }
        catch (VoiceHostIpcException ex) when (!ex.RequestWritten)
        {
            _assistantDelivery.ResetToPending(pendingQueryId);
            LogMessage($"ASSISTANT_DELIVERY_RETRYABLE_IPC queryId={pendingQueryId:N} detail={ex.Message}");
        }
        catch (VoiceHostIpcException ex) when (ex.RequestWritten)
        {
            // The write may have reached Voice Host even though the response
            // was lost. Query the idempotency tombstone before declaring the
            // dispatch ambiguous; this recovers PLAYED/ACCEPTED without ever
            // synthesizing the answer a second time.
            if (!await ReconcileAssistantPlaybackAsync(pendingQueryId, cancellationToken).ConfigureAwait(false))
            {
                _assistantDelivery.MarkAmbiguous(pendingQueryId);
                LogMessage($"ASSISTANT_DELIVERY_AMBIGUOUS_IPC queryId={pendingQueryId:N} detail={ex.Message}");
            }
        }
        catch (IOException ex)
        {
            _assistantDelivery.MarkAmbiguous(pendingQueryId);
            LogMessage($"ASSISTANT_DELIVERY_AMBIGUOUS_IO queryId={pendingQueryId:N} detail={ex.Message}");
        }
        catch (TimeoutException ex)
        {
            _assistantDelivery.MarkAmbiguous(pendingQueryId);
            LogMessage($"ASSISTANT_DELIVERY_AMBIGUOUS_TIMEOUT queryId={pendingQueryId:N} detail={ex.Message}");
        }
    }

    private void LogMessage(string message) => _messageLog?.Invoke(message);

    private async Task<bool> ReconcileAssistantPlaybackAsync(Guid queryId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var response = await new WhisperX.Atom.Desktop.VoiceHostClient().SendAsync(
                "ASSISTANT_PLAYBACK_STATUS",
                new { queryId },
                timeout.Token).ConfigureAwait(false);
            if (!response.Ok || response.Data is not JsonElement data || data.ValueKind != JsonValueKind.Object)
                return false;
            var playbackState = data.TryGetProperty("playbackState", out var state) ? state.GetString()?.ToUpperInvariant() : null;
            switch (playbackState)
            {
                case "PLAYED":
                    _assistantDelivery.MarkDelivered(queryId);
                    LogMessage($"ASSISTANT_DELIVERY_RECONCILED queryId={queryId:N} playbackState=PLAYED");
                    return true;
                case "CANCELLED":
                    _assistantDelivery.MarkCancelled(queryId);
                    LogMessage($"ASSISTANT_DELIVERY_RECONCILED queryId={queryId:N} playbackState=CANCELLED");
                    return true;
                case "ACCEPTED":
                case "RESERVED":
                case "PLAYBACK_STARTED":
                    _assistantDelivery.MarkAccepted(queryId);
                    LogMessage($"ASSISTANT_DELIVERY_RECONCILED queryId={queryId:N} playbackState={playbackState}");
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            LogMessage($"ASSISTANT_DELIVERY_RECONCILE_FAILED queryId={queryId:N} detail={exception.GetType().Name}");
            return false;
        }
    }

    private static string? AssistantErrorSpeech(string? errorCode) => errorCode switch
    {
        "NO_EVIDENCE" => "В стенограмме не найден подтверждённый ответ.",
        "LIVE_MEETING_NOT_READY" => "Пока нет свежего фрагмента совещания для ответа.",
        "LOW_TRANSCRIPT_QUALITY" => "Сначала проверьте качество стенограммы.",
        "GROUNDING_REJECTED" => "Не удалось подтвердить ответ по стенограмме.",
        "NEEDS_REVIEW" => "Ответ требует проверки по стенограмме.",
        "ASSISTANT_WAITING_FOR_GPU" => "Мифодий ждёт освобождения GPU и продолжит обработку автоматически.",
        "ASSISTANT_GPU_BUSY_TIMEOUT" or "ASSISTANT_QUEUE_TIMEOUT" => "Мифодий не дождался освобождения GPU. Повторите вопрос позже.",
        "ASSISTANT_LLM_UNAVAILABLE" => "Локальная модель Мифодия сейчас недоступна.",
        "LOCAL_COMMAND_REQUIRED" => "Это команда записи. Скажите «Мифодий, начни запись» или «Мифодий, останови запись».",
        "VOICE_COMMAND_REPEAT_REQUIRED" => "Не уверен, что команда распознана. Повторите её. Запись не изменена.",
        "ASSISTANT_NO_GROUNDED_ANSWER" => "Не удалось получить подтверждённый ответ.",
        "LLM_UNAVAILABLE" => "Помощник временно недоступен.",
        "FAILED" => "Не удалось получить подтверждённый ответ.",
        _ => null
    };

    private static string MapAssistantApiError(DesktopApiException exception) => exception.ErrorCode switch
    {
        "backend_unavailable" => "VOICE_ASSISTANT_SERVER_UNREACHABLE",
        "backend_timeout" => "VOICE_ASSISTANT_ACCEPTANCE_TIMEOUT",
        "authentication_required" => "VOICE_ASSISTANT_AUTH_REQUIRED",
        "LOCAL_COMMAND_REQUIRED" => "LOCAL_COMMAND_REQUIRED",
        "ASSISTANT_WAITING_FOR_GPU" => "ASSISTANT_WAITING_FOR_GPU",
        "ASSISTANT_GPU_BUSY_TIMEOUT" => "ASSISTANT_GPU_BUSY_TIMEOUT",
        "LLM_UNAVAILABLE" => "ASSISTANT_LLM_UNAVAILABLE",
        _ when exception.StatusCode >= 500 => "VOICE_ASSISTANT_SERVER_ERROR",
        _ => exception.ErrorCode
    };

    private Guid? GetVoiceConversationId(Guid userId, string? requestedMode, Guid? meetingId)
    {
        var normalized = string.IsNullOrWhiteSpace(requestedMode)
            ? "AUTO"
            : requestedMode.Trim().ToUpperInvariant();
        return _voiceConversations.Get(userId, normalized, meetingId)
            ?? (normalized == "AUTO" ? _voiceConversations.Get(userId, "AUTO", null) : null);
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
    private sealed record AssistantCaptureContext(Guid? MeetingId, Guid? RecordingSessionId, string CaptureState, bool IsActive);
}

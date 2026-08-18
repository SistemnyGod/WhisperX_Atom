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
    private readonly Action<Exception>? _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly object _commandCacheGate = new();
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, BrokerResponse Response)> _commandCache = new(StringComparer.Ordinal);
    private Task? _loop;

    public DesktopVoiceBrokerServer(RecordingCommandService commands, IBackendService backend, ActiveMeetingContext activeMeeting, Action<Exception>? log = null)
    {
        _commands = commands;
        _backend = backend;
        _activeMeeting = activeMeeting;
        _log = log;
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

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
            || (commandElement.GetString() is not "EXECUTE_INTENT" and not "RECORD_EVENT" and not "ASSISTANT_QUESTION" and not "ASSISTANT_RESULT"))
            return new(false, "VOICE_COMMAND_REJECTED", Detail: "unsupported_command");

        if (string.Equals(commandElement.GetString(), "ASSISTANT_QUESTION", StringComparison.OrdinalIgnoreCase))
        {
            var question = root.TryGetProperty("question", out var questionElement) ? questionElement.GetString() : null;
            var assistantTraceId = root.TryGetProperty("traceId", out var assistantTraceElement) ? assistantTraceElement.GetString() : null;
            var assistantCommandId = root.TryGetProperty("commandId", out var assistantCommandElementId) ? assistantCommandElementId.GetString() : null;
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
            var accepted = await _backend.CreateAssistantRequestAsync(question, requestedMode, _activeMeeting.MeetingId, null, "VOICE", assistantCommandId, assistantTraceId, cancellationToken).ConfigureAwait(false);
            if (accepted is null)
                return new(false, "VOICE_ASSISTANT_UNAVAILABLE", Detail: "assistant_request_rejected", TraceId: assistantTraceId, CommandId: assistantCommandId);
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

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _shutdown.Dispose();
    }

    private sealed record BrokerResponse(bool Ok, string? ErrorCode = null, string? RecorderState = null,
        string? LocalSessionId = null, string? LocalFinalizeState = null, string? SpokenText = null, string? Detail = null, string? TraceId = null, string? CommandId = null, string? QueryId = null, string? AssistantStatus = null, string? ResolvedMode = null);
}

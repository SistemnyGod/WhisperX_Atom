using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhisperX.Atom.Recorder;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>Desktop-owned broker for managed Voice Host intents.</summary>
public sealed class DesktopVoiceBrokerServer : IAsyncDisposable
{
    private readonly RecordingCommandService _commands;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private Task? _loop;

    public DesktopVoiceBrokerServer(RecordingCommandService commands, ILogger? logger = null)
    {
        _commands = commands;
        _logger = logger;
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
            catch (Exception ex) { _logger?.LogDebug(ex, "Desktop voice broker request failed."); }
        }
    }

    private async Task<BrokerResponse> HandleAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("command", out var commandElement)
            || !string.Equals(commandElement.GetString(), "EXECUTE_INTENT", StringComparison.OrdinalIgnoreCase))
            return new(false, "VOICE_COMMAND_REJECTED", Detail: "unsupported_command");

        var intent = root.TryGetProperty("intent", out var intentElement) ? intentElement.GetString() : null;
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        var testMode = root.TryGetProperty("testMode", out var testElement) && testElement.ValueKind == JsonValueKind.True;
        if (string.IsNullOrWhiteSpace(intent)) return new(false, "VOICE_COMMAND_REJECTED", Detail: "intent_missing");
        if (testMode)
            return new(true, RecorderState: "TEST_ONLY", SpokenText: "Тест распознавания завершён", Detail: $"intent={intent};text={text}");

        AgentIpcResponse response;
        try
        {
            response = intent.ToUpperInvariant() switch
            {
                "STARTRECORDING" => await _commands.StartAsync("Голосовая запись", null, cancellationToken).ConfigureAwait(false),
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
            return new(false, ex.ErrorCode, Detail: ex.Message);
        }
        catch (IOException ex)
        {
            return new(false, "VOICE_RECORDER_UNAVAILABLE", Detail: ex.Message);
        }

        var localReady = response.SessionStatus?.LocalFinalizeState;
        var spoken = response.Ok
            ? intent.ToUpperInvariant() switch
            {
                "STARTRECORDING" => "Запись начата",
                "STOPRECORDING" when string.Equals(localReady, "LOCAL_READY", StringComparison.OrdinalIgnoreCase) => "Запись остановлена и сохранена",
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
        return new(response.Ok, response.Error ?? (response.Ok ? null : "VOICE_COMMAND_REJECTED"), response.State,
            response.SessionId, localReady, spoken, response.ErrorDetail);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _shutdown.Dispose();
    }

    private sealed record BrokerResponse(bool Ok, string? ErrorCode = null, string? RecorderState = null,
        string? LocalSessionId = null, string? LocalFinalizeState = null, string? SpokenText = null, string? Detail = null);
}

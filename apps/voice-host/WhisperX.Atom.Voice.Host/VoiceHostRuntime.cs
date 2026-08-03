using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhisperX.Atom.Recorder;
using WhisperX.Atom.Voice;

namespace WhisperX.Atom.Voice.Host;

public sealed class VoiceHostRuntime : IAsyncDisposable
{
    private readonly VoiceStateMachine _state = new();
    private readonly VoiceIntentParser _parser = new();
    private readonly VoiceAudioCapture _audio;
    private readonly RecorderPipeClient _recorder = new();
    private readonly SpeechResponder _speech = new();
    private readonly IVoiceRecognizer? _vosk;
    private readonly IVoiceRecognizer? _whisper;
    private readonly ILogger<VoiceHostRuntime>? _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly object _audioGate = new();
    private readonly MemoryStream _recognitionBuffer = new();
    private DateTimeOffset _lastRecognition = DateTimeOffset.MinValue;
    private VoiceCommand? _pendingStop;
    private bool _disposed;

    public VoiceHostRuntime(ILogger<VoiceHostRuntime>? logger = null)
    {
        _logger = logger;
        _audio = new VoiceAudioCapture(_state);
        _audio.PcmAvailable += OnPcmAvailable;
        var root = AppContext.BaseDirectory;
        var model = Environment.GetEnvironmentVariable("ATOM_VOSK_MODEL") ?? Path.Combine(root, "Models", "Voice", "vosk-model-small-ru-0.22");
        var whisperExe = Environment.GetEnvironmentVariable("ATOM_WHISPER_CLI") ?? Path.Combine(root, "whisper-cli.exe");
        var whisperModel = Environment.GetEnvironmentVariable("ATOM_WHISPER_MODEL") ?? Path.Combine(root, "Models", "Voice", "ggml-base-q5_k.bin");
        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "VoiceTemp");
        try { if (Directory.Exists(model)) _vosk = new VoskRecognizer(model, grammar: ["атом", "запись", "пауза", "продолжи", "метка", "решение", "поручение", "статус", "заверши", "да", "нет"]); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Vosk voice model is unavailable; voice commands remain disabled until the model is installed."); }
        _whisper = new WhisperCppRecognizer(whisperExe, whisperModel, temp);
    }

    public VoiceHostSnapshot Snapshot => _state.Snapshot;
    public bool QuietMode { get => _speech.QuietMode; set => _speech.QuietMode = value; }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VoiceHostRuntime));
        _state.Enable(true);
        try { _audio.Start(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Voice microphone could not be opened."); }
    }

    public void SetEnabled(bool enabled)
    {
        _state.Enable(enabled);
        if (enabled && !_audio.IsRunning) { try { _audio.Start(); } catch (Exception ex) { _logger?.LogWarning(ex, "Voice microphone start failed."); } }
        if (!enabled) _audio.Stop();
    }

    public void SetPushToTalk(bool enabled) => _state.SetPushToTalk(enabled);

    public async Task<VoiceResponse> SubmitTextAsync(string text, bool pushToTalk = false, CancellationToken cancellationToken = default)
    {
        if (!_state.Snapshot.Enabled) return new VoiceResponse("Голосовой помощник выключен", false, false);
        var normalized = pushToTalk && !_parser.HasWakeWord(text) ? "Атом " + text : text;
        if (!_parser.HasWakeWord(normalized)) return new VoiceResponse("Нужна кодовая фраза «Атом»", true, false);
        if (!_state.TryWake()) return new VoiceResponse("Помощник занят", true, false);
        _state.BeginCapture();
        _state.BeginRecognition(normalized);
        var command = _parser.Parse(normalized);
        return await ExecuteAsync(command, cancellationToken);
    }

    public async Task<VoiceHostResponse> HandleIpcAsync(string command, JsonElement payload, CancellationToken cancellationToken)
    {
        switch (command.Trim().ToUpperInvariant())
        {
            case "STATUS": return new VoiceHostResponse(true, Snapshot);
            case "ENABLE": SetEnabled(ReadBool(payload, "enabled", true)); return new VoiceHostResponse(true, Snapshot);
            case "PUSH_TO_TALK": return new VoiceHostResponse(true, await SubmitTextAsync(ReadString(payload, "text") ?? "", true, cancellationToken));
            case "TEXT": return new VoiceHostResponse(true, await SubmitTextAsync(ReadString(payload, "text") ?? "", false, cancellationToken));
            case "SET_SENSITIVITY": return new VoiceHostResponse(true, Snapshot);
            case "QUIET_MODE": QuietMode = ReadBool(payload, "enabled", false); return new VoiceHostResponse(true, Snapshot);
            case "TEST_TTS": _speech.Test(); return new VoiceHostResponse(true, new { ok = true });
            default: return new VoiceHostResponse(false, Error: "unsupported_command");
        }
    }

    private void OnPcmAvailable(ReadOnlyMemory<byte> pcm)
    {
        if (_vosk is null || !_state.Snapshot.Enabled || DateTimeOffset.UtcNow - _lastRecognition < TimeSpan.FromMilliseconds(650)) return;
        lock (_audioGate)
        {
            _recognitionBuffer.Write(pcm.Span);
            if (_recognitionBuffer.Length < 16000 * 2 * 0.65) return;
            var bytes = _recognitionBuffer.ToArray();
            _recognitionBuffer.SetLength(0);
            _lastRecognition = DateTimeOffset.UtcNow;
            _ = RecognizeWakeAsync(bytes);
        }
    }

    private async Task RecognizeWakeAsync(byte[] bytes)
    {
        try
        {
            var text = await _vosk!.RecognizeAsync(bytes, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(text) && _parser.HasWakeWord(text))
                await SubmitTextAsync(text, false, CancellationToken.None);
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Wake-word recognition failed."); }
    }

    private async Task<VoiceResponse> ExecuteAsync(VoiceCommand command, CancellationToken cancellationToken)
    {
        if (command.Intent == VoiceIntent.StopRecording && _pendingStop is null)
        {
            _pendingStop = command;
            _state.RequestConfirmation(command);
            return await RespondAsync("Подтвердите остановку записи: скажите «Атом, подтверждаю»", cancellationToken, false);
        }
        if (_state.Snapshot.State == VoiceHostState.Confirming)
        {
            if (command.Intent == VoiceIntent.Cancel)
            {
                _pendingStop = null; _state.CancelConfirmation();
                return await RespondAsync("Остановка отменена", cancellationToken);
            }
            if (command.Intent != VoiceIntent.Confirm) return await RespondAsync("Ожидаю подтверждение или отмену", cancellationToken, false);
            command = _pendingStop ?? command;
            _pendingStop = null;
        }
        if (!_state.TryExecute(command)) return await RespondAsync("Команда недоступна в текущем состоянии", cancellationToken, false);

        try
        {
            var response = command.Intent switch
            {
                VoiceIntent.StartRecording => await SendRecorderAsync("START", new { }, cancellationToken),
                VoiceIntent.PauseRecording => await SendRecorderAsync("PAUSE", new { }, cancellationToken),
                VoiceIntent.ResumeRecording => await SendRecorderAsync("RESUME", new { }, cancellationToken),
                VoiceIntent.StopRecording => await SendRecorderAsync("STOP", new { }, cancellationToken),
                VoiceIntent.AddMarker => await SendRecorderAsync("MARKER", new { label = command.Parameter }, cancellationToken),
                VoiceIntent.MarkDecision => await SendRecorderAsync("DECISION", new { label = command.Parameter }, cancellationToken),
                VoiceIntent.MarkActionItem => await SendRecorderAsync("ACTION_ITEM", new { label = command.Parameter }, cancellationToken),
                VoiceIntent.GetStatus => await SendRecorderAsync("STATUS", new { }, cancellationToken),
                VoiceIntent.HistoryQuestion => new VoiceResponse("Запрос по истории будет передан локальному серверу", true, true),
                _ => new VoiceResponse("Команда не распознана", true, false)
            };
            return await RespondAsync(response.Text, cancellationToken, response.Success);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Voice command failed: {Intent}", command.Intent);
            return await RespondAsync("Не удалось выполнить команду. Проверьте состояние сервиса.", cancellationToken, false);
        }
    }

    private async Task<VoiceResponse> SendRecorderAsync(string command, object payload, CancellationToken cancellationToken)
    {
        var result = await _recorder.SendAsync(command, payload, cancellationToken);
        if (!result.Ok) return new VoiceResponse(result.Error ?? "Команда отклонена", true, false);
        var text = command switch
        {
            "START" => "Запись начата",
            "PAUSE" => "Запись приостановлена",
            "RESUME" => "Запись продолжена",
            "STOP" => "Совещание завершено",
            "MARKER" => "Метка установлена",
            "DECISION" => "Решение отмечено",
            "ACTION_ITEM" => "Поручение отмечено",
            "STATUS" => $"Состояние записи: {result.State}",
            _ => "Команда выполнена"
        };
        return new VoiceResponse(text, true, true);
    }

    private async Task<VoiceResponse> RespondAsync(string text, CancellationToken cancellationToken, bool success = true)
    {
        _state.TryRespond(text);
        try { await _speech.SpeakAsync(text, cancellationToken); }
        finally { _state.FinishResponse(); await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ContinueWith(_ => { }); _state.FinishCooldown(); }
        return new VoiceResponse(text, true, success);
    }

    private static string? ReadString(JsonElement payload, string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool ReadBool(JsonElement payload, string name, bool fallback) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static VoiceHostResponse Error(string error) => new(false, Error: error);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _audio.Dispose();
        _vosk?.Dispose();
        _whisper?.Dispose();
        _speech.Dispose();
        await Task.CompletedTask;
    }
}

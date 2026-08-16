using System.Text.Json;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WhisperX.Atom.Recorder;
using WhisperX.Atom.Voice;

namespace WhisperX.Atom.Voice.Host;

public sealed class VoiceHostRuntime : IAsyncDisposable
{
    private static readonly string BuildIdentity = typeof(VoiceHostRuntime).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .Select(attribute => attribute.InformationalVersion)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? typeof(VoiceHostRuntime).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    private static readonly bool ExactWakeWordOption = string.Equals(
        Environment.GetEnvironmentVariable("ATOM_VOSK_EXACT_WAKE_WORD"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("ATOM_VOSK_EXACT_WAKE_WORD"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly bool ExactWakeWordRequested = ExactWakeWordOption
        && !Path.GetFileName(Environment.GetEnvironmentVariable("ATOM_VOSK_MODEL") ?? "vosk-model-small-ru-0.22")
            .Contains("vosk-model-small-ru-0.22", StringComparison.OrdinalIgnoreCase);
    private static readonly string WakeWordMode = ExactWakeWordOption && !ExactWakeWordRequested
        ? "PHONETIC_FALLBACK_MODEL_NO_EXACT_TOKEN"
        : ExactWakeWordRequested ? "EXACT_PLUS_PHONETIC" : "PHONETIC_FALLBACK";

    private static readonly string[] WakeGrammar = CreateWakeGrammar();

    private static string[] CreateWakeGrammar()
    {
        var phrases = new List<string>
        {
            // The bundled small RU model has “мефодий” in its vocabulary and
            // commonly decodes the spoken “мифодий” into that phonetic variant.
            // Keep the canonical spelling in the parser while using the model
            // vocabulary here to avoid Vosk silently dropping the primary token.
            "мефодий", "мефодий начни запись", "мефодий запись", "мефодий пауза", "мефодий продолжи", "мефодий продолжи запись",
            "мефодий поставь на паузу", "мефодий приостанови запись", "мефодий статус", "мефодий заверши запись", "мефодий останови запись", "мефодий подтверждаю", "мефодий отмена",
            "мефодий останови запись", "атом начни запись", "атом останови запись", "атом подтверждаю", "[unk]"
        };
        if (ExactWakeWordRequested)
        {
            // A custom/larger model can opt into the canonical spelling. The
            // phonetic fallback remains in the grammar for compatibility.
            phrases.AddRange(["мифодий", "мифодий начни запись", "мифодий останови запись", "мифодий подтверждаю"]);
        }
        return phrases.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static readonly string[] CommandGrammar =
    [
        "начни запись", "запись", "пауза", "продолжи", "продолжи запись", "поставь на паузу",
        "приостанови запись", "поставь метку", "добавь метку",
        "отметь решение", "зафиксируй решение", "отметь поручение", "зафиксируй поручение",
        "статус", "заверши запись", "останови запись", "подтверждаю", "отмена", "да", "нет", "[unk]"
    ];
    internal static IReadOnlyList<string> WakePhrases => WakeGrammar;
    internal static IReadOnlyList<string> CommandPhrases => CommandGrammar;
    private readonly VoiceStateMachine _state = new();
    private readonly VoiceIntentParser _parser = new();
    private readonly VoiceAudioCapture _audio;
    private readonly RecorderPipeClient _recorder = new();
    private readonly SpeechResponder _speech = new();
    private readonly VoiceAssistantClient _assistant = new();
    private readonly VoskRecognizer? _wakeRecognizer;
    private readonly VoskRecognizer? _commandRecognizer;
    private readonly ILogger<VoiceHostRuntime>? _logger;
    private readonly Channel<VoiceAudioBlock> _audioQueue = Channel.CreateBounded<VoiceAudioBlock>(new BoundedChannelOptions(20)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _recognitionGate = new();
    private readonly object _pttGate = new();
    private readonly MemoryStream _pttBuffer = new();
    private readonly VoiceActivityDetector _vad = new();
    private readonly PcmFrameAssembler _frameAssembler = new();
    private AudioPcmConverter? _converter;
    private Task? _audioWorker;
    private DateTimeOffset _wakeStartedAt;
    private DateTimeOffset _commandStartedAt;
    private DateTimeOffset _lastSpeechAt;
    private string? _pendingRecognizedText;

    private bool _commandSession;
    private bool _speechSeen;
    private double _pendingConfidenceSum;
    private int _pendingConfidenceSegments;
    private bool _pttCapturing;
    private bool _disposed;
    private bool _modelReady;
    private bool _modelIntegrityReady;
    private bool _nativeRuntimeReady;
    private bool _recorderPipeReady;
    private bool _microphoneReady;
    private int _microphoneRecoveryScheduled;
    private int _recorderRecoveryScheduled;
    private string _sensitivity = "balanced";
    private string? _lastIntent;
    private string? _modelError;
    private string? _microphoneError;
    private string? _recorderPipeError;
    private string? _lastErrorCode;
    private string? _lastTraceId;
    private readonly string _wakeWordMode = WakeWordMode;
    private VoiceIntentBrokerClient? _desktopBroker;
    private string? _microphoneDeviceId;
    private double? _lastCommandLatencyMs;
    private double? _wakeLatencyMs;
    private double? _intentLatencyMs;
    private double? _recorderAckLatencyMs;
    private DateTimeOffset _wakeSpeechStartedAt;
    private long _audioQueueDrops;
    private int _audioQueueDepth;
    private int _wakePartialHits;
    private int _audioQueueOverflow;
    private VoiceCommand? _pendingStop;

    public VoiceHostRuntime(ILogger<VoiceHostRuntime>? logger = null)
    {
        _logger = logger;
        _audio = new VoiceAudioCapture();
        _audio.AudioAvailable += OnAudioAvailable;
        _audio.CaptureError += OnCaptureError;
        _speech.Error += OnSpeechError;

        var root = AppContext.BaseDirectory;
        var configuredModelPath = Environment.GetEnvironmentVariable("ATOM_VOSK_MODEL");
        var assetsRoot = string.IsNullOrWhiteSpace(configuredModelPath)
            ? Path.Combine(root, "Models", "Voice")
            : Path.GetDirectoryName(Path.GetFullPath(configuredModelPath))!;
        var modelPath = configuredModelPath
            ?? Path.Combine(assetsRoot, "vosk-model-small-ru-0.22");
        var assetStatus = VoiceAssetVerifier.Check(assetsRoot, modelPath);
        _modelIntegrityReady = assetStatus.IntegrityReady;
        _modelError = assetStatus.ErrorCode;
        try
        {
            if (_modelIntegrityReady)
            {
                _wakeRecognizer = new VoskRecognizer(modelPath, grammar: WakeGrammar);
                _commandRecognizer = _wakeRecognizer.CreateSession(CommandGrammar);
                _nativeRuntimeReady = true;
                _modelReady = true;
            }
            else
            {
                _lastErrorCode = _modelError;
                _logger?.LogWarning("Vosk assets are unavailable or invalid: {ModelPath}, error={Error}", modelPath, _modelError);
            }
        }
        catch (Exception ex)
        {
            _modelError = "VOICE_MODEL_LOAD_FAILED";
            _lastErrorCode = _modelError;
            _logger?.LogWarning(ex, "Vosk voice model or native runtime could not be loaded.");
        }
    }

    public VoiceHostSnapshot Snapshot
    {
        get
        {
            _state.TouchHeartbeat();
            return _state.Snapshot with
            {
                IsSpeaking = _speech.IsSpeaking,
                ModelReady = _modelReady,
                ModelIntegrityReady = _modelIntegrityReady,
                NativeRuntimeReady = _nativeRuntimeReady,
                MicrophoneReady = _microphoneReady,
                RecorderPipeReady = _recorderPipeReady,
                RecorderPipeError = _recorderPipeError,
                Sensitivity = _sensitivity,
                LastIntent = _lastIntent,
                LastErrorCode = _lastErrorCode,
                WakeLatencyMs = _wakeLatencyMs,
                IntentLatencyMs = _intentLatencyMs,
                RecorderAckLatencyMs = _recorderAckLatencyMs,
                TotalLatencyMs = _lastCommandLatencyMs,
                LastCommandLatencyMs = _lastCommandLatencyMs,
                AudioQueueDepth = Volatile.Read(ref _audioQueueDepth),
                AudioQueueDrops = Interlocked.Read(ref _audioQueueDrops),
                EffectiveMicrophoneName = _audio.DeviceName,
                MicrophonePeak = _audio.Telemetry.Peak,
                MicrophoneRms = _audio.Telemetry.Rms,
                MicrophoneClipping = _audio.Telemetry.Clipping,
                AudioSignalState = _audio.Telemetry.SignalState,
                AudioTelemetrySequence = _audio.Telemetry.Sequence,
                LastAudioAtUtc = _audio.Telemetry.AtUtc,
                BuildIdentity = BuildIdentity,
                WakeWordMode = _wakeWordMode,
                ProcessId = Environment.ProcessId,
                LastTraceId = _lastTraceId
            };
        }
    }

    public bool QuietMode { get => _speech.QuietMode; set => _speech.QuietMode = value; }
    public Task WaitForShutdownAsync() => _shutdownRequested.Task;

    public void ConfigureManagedBroker(string? pipeName = null) =>
        _desktopBroker = new VoiceIntentBrokerClient(string.IsNullOrWhiteSpace(pipeName) ? VoiceHostIpc.DesktopBrokerPipeName : pipeName);

    public void ConfigureMicrophone(string? deviceId) => _microphoneDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();

    public async Task<VoiceHostDoctorResult> RunDoctorAsync(CancellationToken cancellationToken = default, bool stopAfter = true)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VoiceHostRuntime));
        Start();
        await ProbeRecorderPipeAsync(cancellationToken);
        ApplyReadinessState();
        var snapshot = Snapshot;
        var readinessError = FirstReadinessError();
        if (stopAfter) SetEnabled(false);
        return new VoiceHostDoctorResult(
            snapshot.State.ToString(),
            _modelReady,
            _modelIntegrityReady,
            _nativeRuntimeReady,
            snapshot.MicrophoneReady,
            _recorderPipeReady,
            _modelError,
            _microphoneError,
            _recorderPipeError,
            readinessError);
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VoiceHostRuntime));
        _state.BeginStartup();
        if (!_modelReady) _state.SetDegraded(_lastErrorCode ?? "VOICE_MODEL_MISSING");
        _audioWorker ??= Task.Run(AudioWorkerAsync);
        try
        {
            _audio.Start(_microphoneDeviceId);
            _microphoneReady = true;
            _microphoneError = null;
            ApplyReadinessState();
            ScheduleRecorderRecovery();
        }
        catch (Exception ex)
        {
            OnCaptureError(ex);
        }
    }

    public void SetEnabled(bool enabled)
    {
        _state.Enable(enabled);
        if (enabled)
        {
            _audioWorker ??= Task.Run(AudioWorkerAsync);
            if (!_audio.IsRunning)
            {
                try { _audio.Start(_microphoneDeviceId); _microphoneReady = true; _microphoneError = null; }
                catch (Exception ex) { OnCaptureError(ex); }
            }
            ApplyReadinessState();
            ScheduleRecorderRecovery();
        }
        else
        {
            _audio.Stop();
            _microphoneReady = false;
            ResetRecognitionSessions();
            DrainAudioQueue();
        }
    }

    public void SetPushToTalk(bool enabled) => _state.SetPushToTalk(enabled);

    public async Task<VoiceResponse> SubmitTextAsync(string text, bool pushToTalk = false, CancellationToken cancellationToken = default, double confidence = 1.0)
    {
        if (!_state.Snapshot.Enabled) return new VoiceResponse("Голосовой помощник выключен", false, false);
        if (_state.Snapshot.State == VoiceHostState.Degraded)
            return new VoiceResponse("\u0413\u043e\u043b\u043e\u0441\u043e\u0432\u043e\u0439 \u043f\u043e\u043c\u043e\u0449\u043d\u0438\u043a \u0440\u0430\u0431\u043e\u0442\u0430\u0435\u0442 \u0432 \u0440\u0435\u0436\u0438\u043c\u0435 DEGRADED: \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u0435 \u043c\u043e\u0434\u0435\u043b\u044c \u0438 \u043f\u0440\u043e\u0432\u0435\u0440\u044c\u0442\u0435 \u043c\u0438\u043a\u0440\u043e\u0444\u043e\u043d.", true, false);
        var normalized = pushToTalk && !_parser.HasWakeWord(text) ? "Мифодий " + text : text;
        if (!_parser.HasWakeWord(normalized)) return new VoiceResponse("Нужна кодовая фраза «Мифодий»", true, false);
        var command = _parser.Parse(normalized, confidence);
        if (confidence < MinimumConfidence() || command.Intent == VoiceIntent.Unknown || pushToTalk && command.Intent == VoiceIntent.HistoryQuestion)
            return await RespondAsync("Команда не распознана", cancellationToken, false);
        if (_state.Snapshot.State == VoiceHostState.Confirming && command.Intent is VoiceIntent.Confirm or VoiceIntent.Cancel)
            return await ExecuteAsync(command, cancellationToken);
        if (_state.Snapshot.State == VoiceHostState.Listening)
        {
            if (!_state.TryWake()) return new VoiceResponse("Помощник занят", true, false);
            _state.BeginCapture();
        }
        if (_state.Snapshot.State is VoiceHostState.Capturing or VoiceHostState.WakeDetected)
            _state.BeginRecognition(normalized);
        return await ExecuteAsync(command, cancellationToken);
    }

    public async Task<VoiceHostResponse> HandleIpcAsync(string command, JsonElement payload, CancellationToken cancellationToken)
    {
        var normalizedCommand = command.Trim().ToUpperInvariant();
        if (_state.Snapshot.State == VoiceHostState.Starting
            && normalizedCommand is not ("STATUS" or "DOCTOR" or "SHUTDOWN"))
            return new VoiceHostResponse(false, Error: "RECORDER_HOST_NOT_INITIALIZED");
        switch (normalizedCommand)
        {
            case "STATUS": return new VoiceHostResponse(true, Snapshot);
            case "DOCTOR": return new VoiceHostResponse(true, await RunDoctorAsync(cancellationToken, stopAfter: false));
            case "ENABLE": SetEnabled(ReadBool(payload, "enabled", true)); return new VoiceHostResponse(true, Snapshot);
            case "PUSH_TO_TALK_BEGIN": return new VoiceHostResponse(BeginPushToTalk(), Snapshot);
            case "PUSH_TO_TALK_END": return new VoiceHostResponse(true, await EndPushToTalkAsync(cancellationToken));
            case "PUSH_TO_TALK": return new VoiceHostResponse(true, await SubmitTextAsync(ReadString(payload, "text") ?? "", true, cancellationToken));
            case "TEXT": return new VoiceHostResponse(true, await SubmitTextAsync(ReadString(payload, "text") ?? "", false, cancellationToken));
            case "SET_SENSITIVITY": SetSensitivity(ReadString(payload, "sensitivity")); return new VoiceHostResponse(true, Snapshot);
            case "QUIET_MODE": QuietMode = ReadBool(payload, "enabled", false); return new VoiceHostResponse(true, Snapshot);
            case "TEST_TTS": return new VoiceHostResponse(_speech.Test(), new { ok = true });
            case "SHUTDOWN": _shutdownRequested.TrySetResult(); SetEnabled(false); return new VoiceHostResponse(true, Snapshot);
            case "TEST_SPEECH":
            {
                var text = ReadString(payload, "text") ?? string.Empty;
                var confidence = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("confidence", out var confidenceValue) && confidenceValue.TryGetDouble(out var parsed) ? parsed : 1.0;
                var parsedCommand = _parser.Parse(text, confidence);
                return new VoiceHostResponse(true, new
                {
                    recognizedText = text,
                    intent = parsedCommand.Intent.ToString(),
                    confidence = parsedCommand.Confidence,
                    wakeWord = _parser.HasWakeWord(text),
                    testMode = true
                });
            }
            default: return new VoiceHostResponse(false, Error: "unsupported_command");
        }
    }

    private void OnAudioAvailable(VoiceAudioBlock block)
    {
        if (_audioQueue.Writer.TryWrite(block))
        {
            Interlocked.Increment(ref _audioQueueDepth);
            return;
        }
        block.Dispose();
        Interlocked.Increment(ref _audioQueueDrops);
        Interlocked.Exchange(ref _audioQueueOverflow, 1);
        _lastErrorCode = "AUDIO_QUEUE_OVERFLOW";
    }

    private string? FirstReadinessError() =>
        _modelError ?? (!_modelIntegrityReady ? "VOICE_MODEL_INTEGRITY_FAILED" : null)
        ?? (!_nativeRuntimeReady ? "VOICE_NATIVE_RUNTIME_UNAVAILABLE" : null)
        ?? _microphoneError ?? (!_microphoneReady ? "VOICE_MICROPHONE_UNAVAILABLE" : null)
        ?? _recorderPipeError ?? (!_recorderPipeReady ? "VOICE_RECORDER_UNAVAILABLE" : null);

    private void ApplyReadinessState()
    {
        if (!_state.Snapshot.Enabled) return;
        var error = FirstReadinessError();
        _lastErrorCode = error;
        if (error is not null) _state.SetDegraded(error);
        else if (_state.Snapshot.State is VoiceHostState.Degraded or VoiceHostState.Starting) _state.MarkReady();
    }

    private async Task ProbeRecorderPipeAsync(CancellationToken cancellationToken)
    {
        if (_desktopBroker is not null)
        {
            _recorderPipeReady = true;
            _recorderPipeError = null;
            return;
        }
        try
        {
            var response = await _recorder.SendAsync("STATUS", new { }, cancellationToken);
            if (response.ProtocolVersion != AgentIpcProtocol.Version)
            {
                _recorderPipeReady = false;
                _recorderPipeError = "RECORDER_PROTOCOL_MISMATCH";
                return;
            }
            _recorderPipeReady = response.Ok;
            _recorderPipeError = response.Ok ? null : "RECORDER_PIPE_REJECTED";
        }
        catch (UnauthorizedAccessException) { _recorderPipeReady = false; _recorderPipeError = "RECORDER_PIPE_ACCESS_DENIED"; }
        catch (TimeoutException) { _recorderPipeReady = false; _recorderPipeError = "RECORDER_PIPE_TIMEOUT"; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { _recorderPipeReady = false; _recorderPipeError = "RECORDER_PIPE_TIMEOUT"; }
        catch (IOException) { _recorderPipeReady = false; _recorderPipeError = "RECORDER_PIPE_UNAVAILABLE"; }
        catch (JsonException) { _recorderPipeReady = false; _recorderPipeError = "RECORDER_PROTOCOL_MISMATCH"; }
    }

    private void ScheduleRecorderRecovery()
    {
        if (_disposed || !_state.Snapshot.Enabled || Interlocked.Exchange(ref _recorderRecoveryScheduled, 1) != 0) return;
        _ = RecoverRecorderPipeAsync();
    }

    private async Task RecoverRecorderPipeAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested && _state.Snapshot.Enabled)
            {
                await ProbeRecorderPipeAsync(_shutdown.Token);
                ApplyReadinessState();
                if (_recorderPipeReady) return;
                await Task.Delay(TimeSpan.FromSeconds(2), _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { Volatile.Write(ref _recorderRecoveryScheduled, 0); }
    }
    private void OnSpeechError(Exception exception)
    {
        _lastErrorCode = "TTS_ERROR";
        _logger?.LogWarning(exception, "Voice response playback failed.");
    }

    private void OnCaptureError(Exception exception)
    {
        _microphoneReady = false;
        _microphoneError = "VOICE_MICROPHONE_UNAVAILABLE";
        _lastErrorCode = _microphoneError;
        _state.SetDegraded(_microphoneError);
        _logger?.LogWarning(exception, "Voice microphone capture failed.");
        if (_state.Snapshot.Enabled && !_disposed && Interlocked.Exchange(ref _microphoneRecoveryScheduled, 1) == 0)
            _ = RecoverMicrophoneAsync();
    }

    private async Task RecoverMicrophoneAsync()
    {
        try
        {
            foreach (var delay in new[] { 250, 1000, 3000 })
            {
                await Task.Delay(delay, _shutdown.Token);
                if (!_state.Snapshot.Enabled || _disposed) return;
                try
                {
                    _audio.Stop();
                    _audio.Start(_microphoneDeviceId);
                    _microphoneReady = true;
                    _microphoneError = null;
                    ApplyReadinessState();
                    return;
                }
                catch (Exception ex) { _logger?.LogDebug(ex, "Voice microphone recovery attempt failed."); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { Volatile.Write(ref _microphoneRecoveryScheduled, 0); }
    }    private async Task AudioWorkerAsync()
    {
        try
        {
            await foreach (var block in _audioQueue.Reader.ReadAllAsync(_shutdown.Token))
            {
                Interlocked.Decrement(ref _audioQueueDepth);
                using (block)
                {
                    if (Interlocked.Exchange(ref _audioQueueOverflow, 0) != 0)
                    {
                        ResetRecognitionSessions();
                        if (_state.Snapshot.State is not (VoiceHostState.Disabled or VoiceHostState.Degraded))
                            _state.ReturnToListening("audio-queue-overflow");
                    }
                    var converter = _converter ??= new AudioPcmConverter();
                    var pcm = converter.Convert(block.Buffer, block.Length, block.Format);
                    if (pcm.Length == 0) continue;

                    var handledByPtt = false;
                    lock (_pttGate)
                    {
                        if (_pttCapturing)
                        {
                            var remaining = 128000 - (int)_pttBuffer.Length;
                            if (remaining > 0) _pttBuffer.Write(pcm, 0, Math.Min(remaining, pcm.Length));
                            if (_pttBuffer.Length >= 128000) _lastErrorCode = "PUSH_TO_TALK_LIMIT_REACHED";
                            handledByPtt = true;
                        }
                    }
                    if (handledByPtt) continue;
                    await _frameAssembler.PushAsync(pcm, frame => ProcessPcmAsync(frame, _shutdown.Token));
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lastErrorCode = "AUDIO_WORKER_FAILED";
            _logger?.LogError(ex, "Voice audio worker stopped unexpectedly.");
        }
    }

    private async Task ProcessPcmAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        var snapshot = _state.Snapshot;
        if (!_modelReady || !snapshot.Enabled || _speech.IsBusy || snapshot.State == VoiceHostState.Cooldown) return;

        var now = DateTimeOffset.UtcNow;
        var speech = _vad.IsSpeech(pcm, _sensitivity);
        if (!_commandSession && speech && _wakeSpeechStartedAt == default)
        {
            _wakeSpeechStartedAt = now;
            _commandStartedAt = now;
        }
        if (_commandSession)
        {
            VoiceRecognitionResult result;
            lock (_recognitionGate) result = _commandRecognizer!.Accept(pcm);
            if (speech)
            {
                _speechSeen = true;
                _lastSpeechAt = now;
            }
            if (result.IsEndpoint && !string.IsNullOrWhiteSpace(result.Text))
            {
                _pendingRecognizedText = AppendText(_pendingRecognizedText, result.Text);
                if (result.Confidence > 0) { _pendingConfidenceSum += result.Confidence; _pendingConfidenceSegments++; }
            }

            var silence = _speechSeen && now - _lastSpeechAt >= TimeSpan.FromMilliseconds(400);
            var timeout = now - _commandStartedAt >= TimeSpan.FromSeconds(4);
            if (silence || timeout)
                await FinishCommandSessionAsync(cancellationToken);
            return;
        }

        VoiceRecognitionResult wakeResult;
        lock (_recognitionGate) wakeResult = _wakeRecognizer!.Accept(pcm);
        var partial = wakeResult.Partial;
        if (!string.IsNullOrWhiteSpace(partial) && _parser.HasWakeWord(partial) && _state.Snapshot.State == VoiceHostState.Listening)
        {
            _wakePartialHits++;
            if (_wakePartialHits >= 2 && _state.TryWake())
            {
                _wakeStartedAt = now;
                _wakeLatencyMs = _wakeSpeechStartedAt == default ? null : (now - _wakeSpeechStartedAt).TotalMilliseconds;
            }
        }
        else if (string.IsNullOrWhiteSpace(partial)) _wakePartialHits = 0;

        if (wakeResult.IsEndpoint && !string.IsNullOrWhiteSpace(wakeResult.Text))
        {
            var text = wakeResult.Text!;
            lock (_recognitionGate) _wakeRecognizer.ResetSession();
            if (!_parser.HasWakeWord(text) || text.Contains("[unk]", StringComparison.OrdinalIgnoreCase))
            {
                ResetRecognitionSessions();
                _state.ReturnToListening("wake-unknown");
                return;
            }
            var command = _parser.Parse(text, wakeResult.Confidence);
            if (wakeResult.Confidence < MinimumConfidence())
            {
                ResetRecognitionSessions();
                _state.ReturnToListening("wake-low-confidence");
                return;
            }
            if (IsWakeOnly(text))
            {
                BeginCommandSession();
                await RespondAsync("Слушаю", cancellationToken, true, VoiceHostState.Capturing);
            }
            else if (command.Intent != VoiceIntent.Unknown)
            {
                EnsureRecognitionState(text);
                _intentLatencyMs = Math.Max(0, (DateTimeOffset.UtcNow - now).TotalMilliseconds);
                await ExecuteAsync(command, cancellationToken);
            }
            else
            {
                ResetRecognitionSessions();
                await RespondAsync("Команда не распознана", cancellationToken, false);
            }
        }
        else if (_state.Snapshot.State == VoiceHostState.WakeDetected && now - _wakeStartedAt >= TimeSpan.FromSeconds(4))
        {
            ResetRecognitionSessions();
            _state.ReturnToListening("wake-timeout");
        }
    }

    private void BeginCommandSession()
    {
        lock (_recognitionGate) _commandRecognizer!.ResetSession();
        _commandSession = true;
        _pendingRecognizedText = null;
        _pendingConfidenceSum = 0;
        _pendingConfidenceSegments = 0;
        _speechSeen = false;
        _commandStartedAt = DateTimeOffset.UtcNow;
        _lastSpeechAt = _commandStartedAt;
        if (_state.Snapshot.State == VoiceHostState.WakeDetected) _state.BeginCapture();
    }

    private async Task FinishCommandSessionAsync(CancellationToken cancellationToken)
    {
        VoiceRecognitionResult tail;
        lock (_recognitionGate) tail = _commandRecognizer!.FinalizeSessionResult();
        var text = AppendText(_pendingRecognizedText, tail.Text);
        if (!string.IsNullOrWhiteSpace(tail.Text) && tail.Confidence > 0) { _pendingConfidenceSum += tail.Confidence; _pendingConfidenceSegments++; }
        var confidence = _pendingConfidenceSegments == 0 ? 0 : _pendingConfidenceSum / _pendingConfidenceSegments;
        _commandSession = false;
        _pendingRecognizedText = null;
        _intentLatencyMs = _speechSeen ? Math.Max(0, (DateTimeOffset.UtcNow - _lastSpeechAt).TotalMilliseconds) : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            ResetRecognitionSessions();
            _state.ReturnToListening("command-empty");
            await RespondAsync("Команда не распознана", cancellationToken, false);
            return;
        }
        var normalized = _parser.HasWakeWord(text) ? text : "Мифодий " + text;
        var command = _parser.Parse(normalized, confidence);
        if (confidence < MinimumConfidence() || command.Intent == VoiceIntent.Unknown)
        {
            ResetRecognitionSessions();
            _state.ReturnToListening("command-unknown");
            await RespondAsync("Команда не распознана", cancellationToken, false);
            return;
        }
        EnsureRecognitionState(normalized);
        _lastCommandLatencyMs = (DateTimeOffset.UtcNow - _commandStartedAt).TotalMilliseconds;
        await ExecuteAsync(command, cancellationToken);
    }

    private bool BeginPushToTalk()
    {
        var snapshot = _state.Snapshot;
        if (!snapshot.Enabled || !_modelReady || _speech.IsBusy) return false;
        if (snapshot.State == VoiceHostState.Listening)
        {
            if (!_state.TryWake() || !_state.BeginCapture()) return false;
        }
        else if (snapshot.State != VoiceHostState.Confirming) return false;
        lock (_pttGate)
        {
            _pttBuffer.SetLength(0);
            _pttCapturing = true;
        }
        return true;
    }

    private async Task<VoiceResponse> EndPushToTalkAsync(CancellationToken cancellationToken)
    {
        byte[] audio;
        lock (_pttGate)
        {
            _pttCapturing = false;
            audio = _pttBuffer.ToArray();
            _pttBuffer.SetLength(0);
        }
        if (audio.Length < 1600)
        {
            _state.ReturnToListening("push-to-talk-empty");
            return await RespondAsync("Не удалось распознать команду", cancellationToken, false);
        }

        VoiceRecognitionResult recognition;
        lock (_recognitionGate) recognition = _commandRecognizer?.RecognizeBatchResult(audio) ?? new VoiceRecognitionResult(null, null, true, 0);
        var text = recognition.Text;
        if (string.IsNullOrWhiteSpace(text) || recognition.Confidence < MinimumConfidence())
        {
            _state.ReturnToListening("push-to-talk-unrecognized");
            return await RespondAsync("Не удалось распознать команду", cancellationToken, false);
        }
        var normalized = _parser.HasWakeWord(text) ? text : "Мифодий " + text;
        if (_state.Snapshot.State != VoiceHostState.Confirming) EnsureRecognitionState(normalized);
        return await SubmitTextAsync(normalized, true, cancellationToken, recognition.Confidence);
    }

    private async Task<VoiceResponse> ExecuteAsync(VoiceCommand command, CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var traceId = Guid.NewGuid().ToString("N");
            _lastTraceId = traceId;
            _lastIntent = command.Intent.ToString();
            // A complete, high-confidence stop phrase is safe to execute
            // immediately. Short/uncertain phrases still require confirmation.
            if (command.Intent == VoiceIntent.StopRecording && _pendingStop is null && command.Confidence < 0.70)
            {
                _pendingStop = command;
                _state.RequestConfirmation(command);
                _ = ExpireConfirmationAsync(command.CreatedAt ?? DateTimeOffset.UtcNow);
                return await RespondAsync("Подтвердите остановку записи: скажите «Мифодий, подтверждаю»", cancellationToken, false, VoiceHostState.Confirming);
            }
            if (_state.Snapshot.State == VoiceHostState.Confirming)
            {
                if (command.Intent == VoiceIntent.Cancel)
                {
                    _pendingStop = null;
                    _state.CancelConfirmation();
                    return await RespondAsync("Остановка отменена", cancellationToken);
                }
                if (command.Intent != VoiceIntent.Confirm)
                    return await RespondAsync("Ожидаю подтверждение или отмену", cancellationToken, false, VoiceHostState.Confirming);
                command = _pendingStop ?? command;
                _pendingStop = null;
            }
            if (!_state.TryExecute(command)) return await RespondAsync("Команда недоступна в текущем состоянии", cancellationToken, false);

            _ = TryRecordVoiceEventAsync("VOICE_COMMAND", new { intent = command.Intent.ToString(), text = command.Text, parameter = command.Parameter, traceId });
            var response = command.Intent switch
            {
                VoiceIntent.StartRecording => await SendRecorderAsync("START", new { }, cancellationToken, traceId),
                VoiceIntent.PauseRecording => await SendRecorderAsync("PAUSE", new { }, cancellationToken, traceId),
                VoiceIntent.ResumeRecording => await SendRecorderAsync("RESUME", new { }, cancellationToken, traceId),
                VoiceIntent.StopRecording => await SendRecorderAsync("STOP", new { }, cancellationToken, traceId),
                VoiceIntent.AddMarker => await SendRecorderAsync("MARKER", new { label = command.Parameter }, cancellationToken, traceId),
                VoiceIntent.MarkDecision => await SendRecorderAsync("DECISION", new { label = command.Parameter }, cancellationToken, traceId),
                VoiceIntent.MarkActionItem => await SendRecorderAsync("ACTION_ITEM", new { label = command.Parameter }, cancellationToken, traceId),
                VoiceIntent.GetStatus => await SendRecorderAsync("STATUS", new { }, cancellationToken, traceId),
                VoiceIntent.HistoryQuestion => await AskHistoryAsync(command.Parameter ?? command.Text, cancellationToken),
                _ => new VoiceResponse("Команда не распознана", true, false)
            };
            _lastCommandLatencyMs = _commandStartedAt == default ? null : (DateTimeOffset.UtcNow - _commandStartedAt).TotalMilliseconds;
            return await RespondAsync(response.Text, cancellationToken, response.Success);
        }
        catch (Exception ex)
        {
            _lastErrorCode = "COMMAND_FAILED";
            _logger?.LogWarning(ex, "Voice command failed: {Intent}", command.Intent);
            return await RespondAsync("Не удалось выполнить команду. Проверьте состояние сервиса.", cancellationToken, false);
        }
        finally { _executionGate.Release(); }
    }

    private async Task ExpireConfirmationAsync(DateTimeOffset createdAt)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), _shutdown.Token);
            if (_state.Snapshot.State == VoiceHostState.Confirming && _pendingStop?.CreatedAt == createdAt)
            {
                _pendingStop = null;
                _state.CancelConfirmation();
                await RespondAsync("Остановка отменена: время подтверждения истекло", CancellationToken.None, false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task<VoiceResponse> SendRecorderAsync(string command, object payload, CancellationToken cancellationToken, string? traceId = null)
    {
        var started = Stopwatch.GetTimestamp();
        if (_desktopBroker is not null
            && command is ("START" or "STOP" or "PAUSE" or "RESUME" or "MARKER" or "DECISION" or "ACTION_ITEM" or "STATUS"))
        {
            var intent = command switch
            {
                "START" => VoiceIntent.StartRecording,
                "STOP" => VoiceIntent.StopRecording,
                "PAUSE" => VoiceIntent.PauseRecording,
                "RESUME" => VoiceIntent.ResumeRecording,
                "STATUS" => VoiceIntent.GetStatus,
                "MARKER" => VoiceIntent.AddMarker,
                "DECISION" => VoiceIntent.MarkDecision,
                _ => VoiceIntent.MarkActionItem
            };
            var broker = await _desktopBroker.ExecuteAsync(intent.ToString(), _pendingRecognizedText ?? command, 1.0, false, cancellationToken, traceId);
            _lastTraceId = broker.TraceId ?? traceId ?? _lastTraceId;
            _recorderAckLatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!broker.Ok)
            {
                _lastErrorCode = broker.ErrorCode ?? "VOICE_COMMAND_REJECTED";
                return new VoiceResponse(VoiceErrorText(_lastErrorCode), true, false);
            }
            _recorderPipeReady = true;
            _recorderPipeError = null;
            return new VoiceResponse(broker.SpokenText ?? command switch
            {
                "START" => "Запись начата",
                "PAUSE" => "Запись приостановлена",
                "RESUME" => "Запись продолжена",
                "STOP" => "Запись остановлена и сохранена",
                "STATUS" => $"Состояние записи: {broker.RecorderState}",
                "MARKER" => "Метка установлена",
                "DECISION" => "Решение отмечено",
                "ACTION_ITEM" => "Поручение отмечено",
                _ => "Команда выполнена"
            }, true, true);
        }
        var result = await _recorder.SendAsync(command, payload, cancellationToken);
        _recorderAckLatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _recorderPipeReady = true;
        _recorderPipeError = null;
        if (result.ProtocolVersion != AgentIpcProtocol.Version)
        {
            _recorderPipeReady = false;
            _recorderPipeError = "RECORDER_PROTOCOL_MISMATCH";
            return new VoiceResponse("Версия Recorder Service несовместима с приложением", true, false);
        }
        if (!result.Ok) return new VoiceResponse(VoiceErrorText(result.Error ?? "VOICE_RECORDER_UNAVAILABLE"), true, false);
        var text = command switch
        {
            "START" => "Запись начата",
            "PAUSE" => "Запись приостановлена",
            "RESUME" => "Запись продолжена",
            "STOP" => "Запись остановлена и сохранена",
            "MARKER" => "Метка установлена",
            "DECISION" => "Решение отмечено",
            "ACTION_ITEM" => "Поручение отмечено",
            "STATUS" => $"Состояние записи: {result.State}",
            _ => "Команда выполнена"
        };
        return new VoiceResponse(text, true, true);
    }

    private static string VoiceErrorText(string code) => code switch
    {
        "VOICE_DESKTOP_BROKER_UNAVAILABLE" => "Приложение Desktop не отвечает.",
        "VOICE_RECORDER_UNAVAILABLE" => "Recorder недоступен.",
        "VOICE_MODEL_MISSING" => "Не найдена модель распознавания голоса.",
        "VOICE_MODEL_INTEGRITY_FAILED" => "Модель распознавания повреждена.",
        "VOICE_NATIVE_RUNTIME_UNAVAILABLE" => "Не доступен native runtime Vosk.",
        "VOICE_MICROPHONE_UNAVAILABLE" => "Микрофон недоступен или запрещён Windows.",
        "VOICE_COMMAND_REJECTED" => "Команда отклонена текущим состоянием записи.",
        "RECORDER_HOST_NOT_INITIALIZED" => "Recorder ещё запускается, повторите команду через несколько секунд.",
        "VOICE_HOST_NOT_INITIALIZED" => "Мифодий ещё запускается, повторите команду через несколько секунд.",
        "NO_AUDIO_CAPTURED" => "Аудио не было захвачено, запись не сохранена.",
        _ => "Не удалось выполнить голосовую команду."
    };

    private async Task<VoiceResponse> AskHistoryAsync(string question, CancellationToken cancellationToken)
    {
        var result = await _assistant.EnqueueAsync(question, cancellationToken);
        if (result.Status is "QUEUED" or "RUNNING")
        {
            _ = CompleteHistoryQuestionAsync(result.QueryId);
            return new VoiceResponse("Запрос принят, отвечу после обработки.", true, true);
        }
        if (result.Status is "FAILED")
            return result.ErrorCode == "VOICE_HOST_TOKEN_NOT_CONFIGURED"
                ? new VoiceResponse("Доступ к локальному серверу не настроен.", true, false)
                : new VoiceResponse("Не удалось получить ответ по истории совещаний.", true, false);
        var answer = result.VoiceAnswer ?? result.Answer;
        return string.IsNullOrWhiteSpace(answer)
            ? new VoiceResponse("В готовых совещаниях нет подтверждённого ответа.", true, false)
            : new VoiceResponse(answer, true, result.Status == "READY");
    }

    private async Task CompleteHistoryQuestionAsync(Guid queryId)
    {
        try
        {
            var result = await _assistant.WaitAsync(queryId, CancellationToken.None);
            var answer = result.VoiceAnswer ?? result.Answer;
            if (result.Status is "READY" && !string.IsNullOrWhiteSpace(answer) && _state.Snapshot.State == VoiceHostState.Listening)
                await RespondAsync(answer, CancellationToken.None, true);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Voice assistant query completion failed: {QueryId}", queryId); }
    }

    private async Task<VoiceResponse> RespondAsync(string text, CancellationToken cancellationToken, bool success = true, VoiceHostState? returnState = null)
    {
        var target = returnState ?? (_state.Snapshot.State == VoiceHostState.Confirming ? VoiceHostState.Confirming : VoiceHostState.Listening);
        _state.TryRespond(text, target);
        var enqueued = _speech.TryEnqueue(text);
        if (enqueued)
        {
            _ = TryRecordVoiceEventAsync("SYSTEM_RESPONSE_STARTED", new { text });
            _ = CompleteResponseAfterPlaybackAsync();
        }
        else
        {
            _state.FinishResponse();
            if (_state.Snapshot.State == VoiceHostState.Cooldown) _ = CompleteCooldownAsync();
        }
        await Task.CompletedTask;
        return new VoiceResponse(text, enqueued, success);
    }

    private async Task CompleteResponseAfterPlaybackAsync()
    {
        try
        {
            while (_speech.IsBusy && !_shutdown.IsCancellationRequested) await Task.Delay(20, _shutdown.Token);
            _ = TryRecordVoiceEventAsync("SYSTEM_RESPONSE_FINISHED", new { });
            _state.FinishResponse();
            DrainAudioQueue();
            if (_state.Snapshot.State == VoiceHostState.Cooldown) await CompleteCooldownAsync();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    private async Task CompleteCooldownAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), _shutdown.Token);
            _state.FinishCooldown();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task DrainAfterSpeechAsync()
    {
        try
        {
            while (_speech.IsBusy && !_shutdown.IsCancellationRequested)
                await Task.Delay(50, _shutdown.Token);
            DrainAudioQueue();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    private void EnsureRecognitionState(string text)
    {
        if (_state.Snapshot.State == VoiceHostState.Listening) _state.TryWake();
        if (_state.Snapshot.State == VoiceHostState.WakeDetected) _state.BeginCapture();
        if (_state.Snapshot.State == VoiceHostState.Capturing) _state.BeginRecognition(text);
        else if (_state.Snapshot.State == VoiceHostState.Recognizing) _state.BeginRecognition(text);
    }

    private void ResetRecognitionSessions()
    {
        lock (_recognitionGate)
        {
            _wakeRecognizer?.ResetSession();
            _commandRecognizer?.ResetSession();
        }
        _commandSession = false;
        _pendingRecognizedText = null;
        _pendingConfidenceSum = 0;
        _pendingConfidenceSegments = 0;
        _speechSeen = false;
        _wakePartialHits = 0;
        _wakeSpeechStartedAt = default;
        _commandStartedAt = default;
        _frameAssembler.Reset();
    }

    private void DrainAudioQueue()
    {
        while (_audioQueue.Reader.TryRead(out var block))
        {
            Interlocked.Decrement(ref _audioQueueDepth);
            block.Dispose();
        }
        _frameAssembler.Reset();
    }
    private void SetSensitivity(string? value)
    {
        var normalized = (value ?? "balanced").Trim().ToLowerInvariant();
        _sensitivity = normalized is "high" or "low" or "balanced" ? normalized : "balanced";
    }

    private double MinimumConfidence() => _sensitivity switch
    {
        "high" => 0.68,
        "low" => 0.45,
        _ => 0.55
    };

    private static bool IsWakeOnly(string text)
    {
        var normalized = text.Trim().TrimEnd('.', ',', '!', '?').ToLowerInvariant();
        return normalized is "мифодий" or "мефодий" or "атом" or "atom";
    }

    private static string? AppendText(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second.Trim();
        if (string.IsNullOrWhiteSpace(second)) return first.Trim();
        return $"{first.Trim()} {second.Trim()}";
    }

    private async Task TryRecordVoiceEventAsync(string eventType, object payload)
    {
        try
        {
            if (_desktopBroker is not null)
                await _desktopBroker.RecordEventAsync(eventType, payload, CancellationToken.None);
            else
                await _recorder.SendAsync("VOICE_EVENT", new { eventType, payload }, CancellationToken.None);
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Recorder event could not be persisted: {EventType}", eventType); }
    }

    private static string? ReadString(JsonElement payload, string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool ReadBool(JsonElement payload, string name, bool fallback) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _audio.Stop();
        _audioQueue.Writer.TryComplete();
        _shutdown.Cancel();
        try { if (_audioWorker is not null) await _audioWorker.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _commandRecognizer?.Dispose();
        _wakeRecognizer?.Dispose();
        _assistant.Dispose();
        _speech.Dispose();
        _executionGate.Dispose();
        _shutdown.Dispose();
        _pttBuffer.Dispose();
    }
}

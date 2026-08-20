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
    private static readonly string[] CancelGrammar =
    [
        "мефодий остановись", "мефодий замолчи", "мефодий прекрати говорить", "мефодий останови ответ",
        "атом остановись", "атом замолчи", "атом прекрати говорить", "атом останови ответ",
        "мифодий остановись", "мифодий замолчи", "мифодий прекрати говорить", "мифодий останови ответ", "[unk]"
    ];

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

    // Recorder actions stay deterministic in VoiceIntentParser.  The audio
    // recognizer used after a wake word intentionally has no grammar: a
    // meeting question cannot be represented by the short command grammar.
    internal static IReadOnlyList<string> WakePhrases => WakeGrammar;
    private readonly VoiceStateMachine _state = new();
    private readonly VoiceIntentParser _parser = new();
    private readonly VoiceAudioCapture _audio;
    private readonly RecorderPipeClient _recorder = new();
    private readonly SpeechResponder _speech = new();
    private readonly VoskRecognizer? _wakeRecognizer;
    private readonly VoskRecognizer? _utteranceRecognizer;
    private readonly VoskRecognizer? _cancelRecognizer;
    private readonly VoskRecognizer? _liveRecognizer;
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
    private readonly SemaphoreSlim _audioOperationGate = new(1, 1);
    private readonly object _recognitionGate = new();
    private readonly SemaphoreSlim _livePublishGate = new(1, 1);
    private readonly object _pttGate = new();
    private readonly MemoryStream _pttBuffer = new();
    private readonly VoiceActivityDetector _vad = new();
    private readonly PcmFrameAssembler _frameAssembler = new();
    // Two seconds of 16 kHz mono PCM16 (64,000 bytes) are retained only in
    // memory so the first words after the wake word are available to the
    // unrestricted recognizer. The buffer is never persisted or transmitted.
    private readonly VoiceRingBuffer _preRoll = new(64_000);
    private AudioPcmConverter? _converter;
    private Task? _audioWorker;
    private Task? _liveStatusPollTask;
    private DateTimeOffset _wakeStartedAt;
    private DateTimeOffset _commandStartedAt;
    private DateTimeOffset _lastSpeechAt;
    private DateTimeOffset _liveRecordingStartedAt;
    private string? _pendingRecognizedText;
    private Guid? _liveRecordingSessionId;

    private bool _commandSession;
    private int _liveRecordingActive;
    private int _liveRecordingPaused;
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
    private string? _microphoneErrorDetail;
    private string? _recorderPipeError;
    private string? _lastErrorCode;
    private string? _lastTraceId;
    private string? _lastCommandId;
    private string? _lastCommandFingerprint;
    private DateTimeOffset _lastCommandAtUtc;
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
    private readonly object _assistantTombstoneGate = new();
    private readonly string _assistantTombstonePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperXAtom", "Assistant", "voice-playback-tombstones.json");
    private Dictionary<string, AssistantPlaybackTombstone> _assistantTombstones = new(StringComparer.Ordinal);

    public VoiceHostRuntime(ILogger<VoiceHostRuntime>? logger = null)
    {
        _logger = logger;
        LoadAssistantTombstones();
        _audio = new VoiceAudioCapture();
        _audio.AudioAvailable += OnAudioAvailable;
        _audio.CaptureError += OnCaptureError;
        _speech.Error += OnSpeechError;
        _logger?.LogInformation(
            "Voice response engine initialized. Mode={Mode}, Voice={Voice}, Culture={Culture}",
            _speech.UsesPreRecordedResponses ? "PRERECORDED" : "WINDOWS_TTS",
            _speech.VoiceName,
            _speech.VoiceCulture);

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
                _utteranceRecognizer = _wakeRecognizer.CreateUnrestrictedSession();
                _cancelRecognizer = new VoskRecognizer(modelPath, grammar: CancelGrammar);
                _liveRecognizer = _wakeRecognizer.CreateUnrestrictedSession();
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
                RequestedMicrophoneDeviceId = _microphoneDeviceId,
                EffectiveMicrophoneDeviceId = _audio.DeviceId,
                MicrophonePeak = _audio.Telemetry.Peak,
                MicrophoneRms = _audio.Telemetry.Rms,
                MicrophoneClipping = _audio.Telemetry.Clipping,
                AudioSignalState = _audio.Telemetry.SignalState,
                AudioTelemetrySequence = _audio.Telemetry.Sequence,
                LastAudioAtUtc = _audio.Telemetry.AtUtc,
                BuildIdentity = BuildIdentity,
                WakeWordMode = _wakeWordMode,
                ProcessId = Environment.ProcessId,
                LastTraceId = _lastTraceId,
                LastCommandId = _lastCommandId,
                MicrophoneErrorDetail = _microphoneErrorDetail,
                RequestedVoiceName = _speech.RequestedVoiceName,
                EffectiveVoiceName = _speech.VoiceName,
                EffectiveVoiceCulture = _speech.VoiceCulture,
                VoiceFallbackUsed = _speech.VoiceFallbackUsed,
                SpeechQueueDepth = _speech.QueueDepth,
                SpeechQueueDrops = _speech.QueueDrops,
                RestartState = _lastErrorCode is "VOICE_HOST_RESTART_LIMIT" or "VOICE_HOST_RESTART_FAILED" ? "DEGRADED" : null
            };
        }
    }

    public bool QuietMode { get => _speech.QuietMode; set => _speech.QuietMode = value; }
    public VoiceAudioTelemetry AudioTelemetry => _audio.Telemetry;
    public Task WaitForShutdownAsync() => _shutdownRequested.Task;

    public void ConfigureManagedBroker(string? pipeName = null)
    {
        _desktopBroker = new VoiceIntentBrokerClient(string.IsNullOrWhiteSpace(pipeName) ? VoiceHostIpc.DesktopBrokerPipeName : pipeName);
        // Desktop owns Recorder IPC in managed mode; the broker itself is the
        // readiness boundary and avoids a transient degraded state while the
        // background legacy-pipe probe is still running.
        _recorderPipeReady = true;
        _recorderPipeError = null;
    }

    public void ConfigureMicrophone(string? deviceId) => _microphoneDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();

    public async Task<VoiceHostResponse> ConfigureAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestedDevice = ReadString(payload, "microphoneDeviceId");
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("enabled", out var enabled)
                && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                SetEnabled(enabled.GetBoolean());
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("quietMode", out var quiet)
                && quiet.ValueKind is JsonValueKind.True or JsonValueKind.False)
                QuietMode = quiet.GetBoolean();
            var sensitivity = ReadString(payload, "sensitivity");
            if (!string.IsNullOrWhiteSpace(sensitivity)) SetSensitivity(sensitivity);
            var voiceName = ReadString(payload, "voiceName");
            var voiceRate = ReadNullableInt(payload, "voiceRate");
            var voiceVolume = ReadNullableInt(payload, "voiceVolume");
            if (!_speech.ConfigureVoice(voiceName, voiceRate, voiceVolume))
            {
                _lastErrorCode = "VOICE_RUSSIAN_VOICE_UNAVAILABLE";
                // Never report a healthy listening host when the configured
                // response voice is unavailable.  Continuing here used to
                // make CONFIGURE succeed while the first command silently
                // had no safe Russian TTS response.
                return new VoiceHostResponse(false, Snapshot, _lastErrorCode);
            }

            var normalized = string.IsNullOrWhiteSpace(requestedDevice) ? _microphoneDeviceId : requestedDevice.Trim();
            if (!string.Equals(normalized, _microphoneDeviceId, StringComparison.OrdinalIgnoreCase) || !_audio.IsRunning)
            {
                _microphoneDeviceId = normalized;
                if (_audio.IsRunning)
                {
                    _audio.Stop();
                    ResetRecognitionSessions();
                    DrainAudioQueue();
                }
                if (_state.Snapshot.Enabled)
                {
                    try
                    {
                        _audio.Start(_microphoneDeviceId);
                        _microphoneReady = true;
                        _microphoneError = null;
                        _microphoneErrorDetail = null;
                    }
                    catch (Exception ex)
                    {
                        OnCaptureError(ex);
                        return new VoiceHostResponse(false, Snapshot, "VOICE_MICROPHONE_UNAVAILABLE");
                    }
                }
            }
            ApplyReadinessState();
            return new VoiceHostResponse(_microphoneReady || !_state.Snapshot.Enabled, Snapshot);
        }
        finally { _audioOperationGate.Release(); }
    }

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
        if (_desktopBroker is not null) _liveStatusPollTask ??= Task.Run(LiveStatusPollAsync);
        try
        {
            _audio.Start(_microphoneDeviceId);
            _microphoneReady = true;
            _microphoneError = null;
            _microphoneErrorDetail = null;
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
                try { _audio.Start(_microphoneDeviceId); _microphoneReady = true; _microphoneError = null; _microphoneErrorDetail = null; }
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

    private async Task LiveStatusPollAsync()
    {
        while (!_shutdown.IsCancellationRequested && _desktopBroker is not null)
        {
            try
            {
                var status = await _desktopBroker.ExecuteAsync(
                    "GetStatus",
                    "live-status",
                    1,
                    false,
                    _shutdown.Token).ConfigureAwait(false);
                if (status.Ok)
                {
                    var active = status.RecorderState is "Recording" or "Paused" or "Starting" or "Finalizing";
                    if (active)
                    {
                        var wasActive = Volatile.Read(ref _liveRecordingActive) == 1;
                        Interlocked.Exchange(ref _liveRecordingActive, 1);
                        Interlocked.Exchange(ref _liveRecordingPaused, status.RecorderState == "Paused" ? 1 : 0);
                        if (Guid.TryParse(status.LocalSessionId, out var sessionId)) _liveRecordingSessionId = sessionId;
                        if (!wasActive || _liveRecordingStartedAt == default)
                        {
                            _liveRecordingStartedAt = DateTimeOffset.UtcNow;
                            lock (_recognitionGate) _liveRecognizer?.ResetSession();
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _liveRecordingActive, 0);
                        Interlocked.Exchange(ref _liveRecordingPaused, 0);
                        _liveRecordingSessionId = null;
                        _liveRecordingStartedAt = default;
                        lock (_recognitionGate) _liveRecognizer?.ResetSession();
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger?.LogDebug(ex, "Live recording status poll failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
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
        if (confidence < MinimumConfidence())
        {
            _lastErrorCode = "VOICE_CONFIDENCE_TOO_LOW";
            return await RespondAsync(VoiceErrorText(_lastErrorCode), cancellationToken, false);
        }
        if (command.Intent == VoiceIntent.Unknown)
        {
            _lastErrorCode = "VOICE_COMMAND_REJECTED";
            return await RespondAsync("Команда не распознана", cancellationToken, false);
        }
        if (_state.Snapshot.State == VoiceHostState.Confirming && command.Intent is VoiceIntent.Confirm or VoiceIntent.Cancel)
            return await ExecuteAsync(command, cancellationToken);
        if (_state.Snapshot.State == VoiceHostState.Listening)
        {
            if (!_state.TryWake())
            {
                _lastErrorCode = "VOICE_HOST_BUSY";
                return new VoiceResponse(VoiceErrorText(_lastErrorCode), false, false);
            }
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
            return new VoiceHostResponse(false, Error: "VOICE_HOST_NOT_INITIALIZED");
        switch (normalizedCommand)
        {
            case "STATUS": return new VoiceHostResponse(true, Snapshot);
            case "DOCTOR": return new VoiceHostResponse(true, await RunDoctorAsync(cancellationToken, stopAfter: false));
            case "CONFIGURE": return await ConfigureAsync(payload, cancellationToken).ConfigureAwait(false);
            case "ENABLE": SetEnabled(ReadBool(payload, "enabled", true)); return new VoiceHostResponse(true, Snapshot);
            case "PUSH_TO_TALK_BEGIN": return new VoiceHostResponse(BeginPushToTalk(), Snapshot);
            case "PUSH_TO_TALK_END": return new VoiceHostResponse(true, await EndPushToTalkAsync(cancellationToken));
            case "PUSH_TO_TALK": return new VoiceHostResponse(true, await SubmitTextAsync(ReadString(payload, "text") ?? "", true, cancellationToken));
            case "TEXT": return new VoiceHostResponse(true, await SubmitTextAsync(ReadString(payload, "text") ?? "", false, cancellationToken));
            case "SET_SENSITIVITY": SetSensitivity(ReadString(payload, "sensitivity")); return new VoiceHostResponse(true, Snapshot);
            case "QUIET_MODE": QuietMode = ReadBool(payload, "enabled", false); return new VoiceHostResponse(true, Snapshot);
            case "TEST_TTS":
                if (_speech.QuietMode) return new VoiceHostResponse(false, Error: "VOICE_QUIET_MODE");
                return _speech.Test()
                    ? new VoiceHostResponse(true, new { ok = true })
                    : new VoiceHostResponse(false, Error: _speech.IsRussianVoiceAvailable ? "VOICE_HOST_BUSY" : "VOICE_RUSSIAN_VOICE_UNAVAILABLE");
            case "LIST_RUSSIAN_VOICES": return new VoiceHostResponse(true, new { voices = _speech.GetRussianVoiceNames(), selectedVoice = _speech.VoiceName });
            case "SPEAK_ASSISTANT_RESULT":
            {
                var answer = ReadString(payload, "voiceAnswer") ?? string.Empty;
                var status = ReadString(payload, "status");
                var queryId = ReadString(payload, "queryId");
                var commandId = ReadString(payload, "commandId");
                var traceId = ReadString(payload, "traceId");
                if (string.IsNullOrWhiteSpace(answer) || status is not ("READY" or "ANSWERED" or "ANSWERED_WITH_WARNING" or "NEEDS_REVIEW" or "NO_EVIDENCE" or "LOW_TRANSCRIPT_QUALITY" or "GROUNDING_REJECTED" or "FAILED" or "LLM_UNAVAILABLE"))
                    return new VoiceHostResponse(false, Error: "VOICE_COMMAND_REJECTED");
                if (!string.IsNullOrWhiteSpace(queryId) && TryGetAssistantTombstone(queryId, out var previous))
                    return new VoiceHostResponse(true, new VoiceResponse(answer, false, previous.AnswerStatus is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING", CommandId: commandId, TraceId: traceId, QueryId: queryId, ResponseId: previous.ResponseId, PlaybackState: previous.State, AcceptedForPlayback: previous.State == "ACCEPTED", AnswerStatus: previous.AnswerStatus));
                if (_speech.QuietMode)
                {
                    var quietResponseId = Guid.NewGuid().ToString("N");
                    if (!string.IsNullOrWhiteSpace(queryId)
                        && !SaveAssistantTombstone(queryId, quietResponseId, "CANCELLED", status))
                        return new VoiceHostResponse(false, Error: "VOICE_PLAYBACK_LEDGER_UNAVAILABLE");
                    return new VoiceHostResponse(true, new VoiceResponse(
                        answer,
                        false,
                        status is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING",
                        CommandId: commandId,
                        TraceId: traceId,
                        QueryId: queryId,
                        ResponseId: quietResponseId,
                        PlaybackState: "CANCELLED",
                        AcceptedForPlayback: false,
                        AnswerStatus: status));
                }
                var reservedResponseId = Guid.NewGuid().ToString("N");
                if (!string.IsNullOrWhiteSpace(queryId)
                    && !SaveAssistantTombstone(queryId, reservedResponseId, "RESERVED", status))
                    return new VoiceHostResponse(false, Error: "VOICE_PLAYBACK_LEDGER_UNAVAILABLE");
                var spoken = await RespondAsync(
                    answer,
                    cancellationToken,
                    status is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING",
                    commandId: commandId,
                    traceId: traceId,
                    queryId: queryId,
                    answerStatus: status,
                    reservedResponseId: reservedResponseId).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(queryId))
                    UpdateAssistantTombstone(queryId, spoken.AcceptedForPlayback ? "ACCEPTED" : "FAILED");
                return spoken.AcceptedForPlayback
                    ? new VoiceHostResponse(true, spoken)
                    : new VoiceHostResponse(false, Error: _lastErrorCode ?? "VOICE_HOST_BUSY");
            }
            case "STOP_SPEAKING":
                await TryRecordVoiceEventAsync(
                    "VOICE_COMMAND",
                    new { eventId = Guid.NewGuid().ToString("N"), intent = VoiceIntent.StopSpeaking.ToString(), commandId = Guid.NewGuid().ToString("N"), traceId = Guid.NewGuid().ToString("N"), capturedAtUtc = DateTimeOffset.UtcNow },
                    CancellationToken.None);
                _speech.CancelAll();
                return new VoiceHostResponse(true, new VoiceResponse("Ответ остановлен", false, true));
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
        _microphoneErrorDetail = _audio.LastErrorDetail ?? exception.GetBaseException().Message;
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
                    _microphoneErrorDetail = null;
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
                    // During TTS the cancel recognizer is the only consumer;
                    // response audio must never enter the normal pre-roll.
                    if (!_speech.IsBusy) _preRoll.Append(pcm);

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
        if (!_modelReady || !snapshot.Enabled) return;
        if (_speech.IsBusy)
        {
            await ProcessCancelPcmAsync(pcm, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (snapshot.State == VoiceHostState.Cooldown) return;

        // Provisional meeting ASR is isolated from the command recognizer.
        // It is advisory only: failures and drops never affect Recorder or
        // the canonical V1/V2 pipeline.
        if (Volatile.Read(ref _liveRecordingActive) == 1 && Volatile.Read(ref _liveRecordingPaused) == 0)
            await ProcessLiveAsrAsync(pcm, cancellationToken).ConfigureAwait(false);

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
            lock (_recognitionGate) result = _utteranceRecognizer!.Accept(pcm);
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

            var silence = _speechSeen && now - _lastSpeechAt >= TimeSpan.FromMilliseconds(700);
            var timeout = now - _commandStartedAt >= TimeSpan.FromSeconds(20);
            if (silence || timeout)
                await FinishCommandSessionAsync(cancellationToken);
            return;
        }

        VoiceRecognitionResult wakeResult;
        lock (_recognitionGate)
        {
            // This recognizer never leaves the process and is reset at each
            // utterance boundary.  It supplies the full one-shot phrase
            // while the constrained recognizer decides whether a wake word
            // was actually present.
            _utteranceRecognizer!.Accept(pcm);
            wakeResult = _wakeRecognizer!.Accept(pcm);
        }
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
            var wakeText = wakeResult.Text!;
            VoiceRecognitionResult utterance;
            lock (_recognitionGate)
            {
                utterance = _utteranceRecognizer!.FinalizeSessionResult();
                // The unrestricted recognizer already saw the complete audio
                // stream. Replay the in-memory pre-roll only when it failed to
                // retain a wake word or returned no usable text; otherwise a
                // two-second replay would truncate long one-shot questions.
                if (string.IsNullOrWhiteSpace(utterance.Text) || !_parser.HasWakeWord(utterance.Text))
                {
                    var preRoll = _preRoll.Snapshot();
                    _utteranceRecognizer.ResetSession();
                    if (preRoll.Length > 0) ReplayPreRoll(preRoll);
                    utterance = _utteranceRecognizer.FinalizeSessionResult();
                }
                _wakeRecognizer.ResetSession();
            }
            var text = _parser.HasWakeWord(utterance.Text ?? string.Empty)
                ? utterance.Text!
                : string.IsNullOrWhiteSpace(utterance.Text) ? wakeText : $"{wakeText} {utterance.Text}";
            if (!_parser.HasWakeWord(text) || text.Contains("[unk]", StringComparison.OrdinalIgnoreCase))
            {
                ResetRecognitionSessions();
                _state.ReturnToListening("wake-unknown");
                return;
            }
            var confidence = utterance.Confidence > 0 ? utterance.Confidence : wakeResult.Confidence;
            var command = _parser.Parse(text, confidence);
            if (confidence < MinimumConfidence())
            {
                _lastErrorCode = "VOICE_CONFIDENCE_TOO_LOW";
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

    private async Task ProcessCancelPcmAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        VoiceRecognitionResult result;
        lock (_recognitionGate) result = _cancelRecognizer?.Accept(pcm) ?? new VoiceRecognitionResult(null, null, false, 0);
        if (!result.IsEndpoint || string.IsNullOrWhiteSpace(result.Text)) return;
        var command = _parser.Parse(result.Text, result.Confidence);
        lock (_recognitionGate) _cancelRecognizer?.ResetSession();
        if (command.Intent != VoiceIntent.StopSpeaking || result.Confidence < 0.70) return;
        var traceId = Guid.NewGuid().ToString("N");
        var commandId = Guid.NewGuid().ToString("N");
        _lastTraceId = traceId;
        _lastCommandId = commandId;
        _lastIntent = VoiceIntent.StopSpeaking.ToString();
        await TryRecordVoiceEventAsync("VOICE_COMMAND", new
        {
            eventId = Guid.NewGuid().ToString("N"), intent = command.Intent.ToString(), commandId, traceId,
            confidence = result.Confidence, capturedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
        _speech.CancelAll();
        _lastErrorCode = null;
    }

    private async Task ProcessLiveAsrAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        if (_commandSession || _state.Snapshot.State is VoiceHostState.WakeDetected or VoiceHostState.Capturing or VoiceHostState.Recognizing)
            return;
        VoiceRecognitionResult result;
        lock (_recognitionGate)
            result = _liveRecognizer?.Accept(pcm) ?? new VoiceRecognitionResult(null, null, false, 0);
        if (!result.IsEndpoint || string.IsNullOrWhiteSpace(result.Text)) return;

        var text = result.Text.Trim();
        lock (_recognitionGate) _liveRecognizer?.ResetSession();
        if (text.Length < 2 || text.Contains("[unk]", StringComparison.OrdinalIgnoreCase)) return;

        // Wake/command utterances are handled by the deterministic command
        // path and must not become meeting evidence.
        var normalized = text.ToLowerInvariant();
        if (normalized.Contains("мифодий", StringComparison.Ordinal)
            || normalized.Contains("мефодий", StringComparison.Ordinal)
            || normalized.Contains("атом", StringComparison.Ordinal)) return;

        var startedAt = _liveRecordingStartedAt == default ? DateTimeOffset.UtcNow : _liveRecordingStartedAt;
        var endMs = Math.Max(100, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        var startMs = Math.Max(0, endMs - 900);
        var segment = new VoiceLiveAsrSegment(
            Guid.NewGuid(),
            startMs,
            endMs,
            text,
            result.Confidence > 0 ? Math.Clamp(result.Confidence, 0, 1) : null);
        _ = PublishLiveAsrSegmentAsync(segment, cancellationToken);
    }

    private async Task PublishLiveAsrSegmentAsync(VoiceLiveAsrSegment segment, CancellationToken cancellationToken)
    {
        if (_desktopBroker is null || Volatile.Read(ref _liveRecordingActive) == 0) return;
        if (!await _livePublishGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            await _desktopBroker.PublishLiveAsrSegmentsAsync(_liveRecordingSessionId, [segment], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Live provisional ASR publish failed");
        }
        finally { _livePublishGate.Release(); }
    }

    private void BeginCommandSession()
    {
        lock (_recognitionGate) _utteranceRecognizer!.ResetSession();
        // A wake-only command starts a new clean recognition session. The
        // pre-roll is only a fallback for the current wake utterance and must
        // not leak into the follow-up command.
        _preRoll.Clear();
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
        lock (_recognitionGate) tail = _utteranceRecognizer!.FinalizeSessionResult();
        var text = AppendText(_pendingRecognizedText, tail.Text);
        if (!string.IsNullOrWhiteSpace(tail.Text) && tail.Confidence > 0) { _pendingConfidenceSum += tail.Confidence; _pendingConfidenceSegments++; }
        var confidence = _pendingConfidenceSegments == 0 ? 0 : _pendingConfidenceSum / _pendingConfidenceSegments;
        _commandSession = false;
        _pendingRecognizedText = null;
        _intentLatencyMs = _speechSeen ? Math.Max(0, (DateTimeOffset.UtcNow - _lastSpeechAt).TotalMilliseconds) : null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000)
        {
            ResetRecognitionSessions();
            _state.ReturnToListening("command-empty");
            _lastErrorCode = "VOICE_ASSISTANT_EMPTY_ANSWER";
            await RespondAsync(VoiceErrorText(_lastErrorCode), cancellationToken, false);
            return;
        }
        var normalized = _parser.HasWakeWord(text) ? text : "Мифодий " + text;
        var command = _parser.Parse(normalized, confidence);
        if (confidence < MinimumConfidence() || command.Intent == VoiceIntent.Unknown)
        {
            ResetRecognitionSessions();
            _state.ReturnToListening("command-unknown");
            _lastErrorCode = confidence < MinimumConfidence() ? "VOICE_CONFIDENCE_TOO_LOW" : "VOICE_COMMAND_REJECTED";
            await RespondAsync(VoiceErrorText(_lastErrorCode), cancellationToken, false);
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
        lock (_recognitionGate) recognition = _utteranceRecognizer?.RecognizeBatchResult(audio) ?? new VoiceRecognitionResult(null, null, true, 0);
        var text = recognition.Text;
        if (string.IsNullOrWhiteSpace(text) || recognition.Confidence < MinimumConfidence())
        {
            _lastErrorCode = string.IsNullOrWhiteSpace(text) ? "VOICE_ASSISTANT_EMPTY_ANSWER" : "VOICE_CONFIDENCE_TOO_LOW";
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
            var commandId = command.CommandId ?? Guid.NewGuid().ToString("N");
            _lastTraceId = traceId;
            _lastCommandId = commandId;
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
            commandId = command.CommandId ?? commandId;
            command = command with { CommandId = commandId };
            var fingerprint = $"{command.Intent}:{NormalizeCommandText(command.Text)}";
            if (command.Intent is not (VoiceIntent.Confirm or VoiceIntent.Cancel)
                && string.Equals(_lastCommandFingerprint, fingerprint, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow - _lastCommandAtUtc < TimeSpan.FromSeconds(2))
            {
                return await RespondAsync("Команда уже выполняется", cancellationToken, false, commandId: commandId, traceId: traceId);
            }
            if (command.Intent == VoiceIntent.StopSpeaking)
            {
                _lastCommandFingerprint = fingerprint;
                _lastCommandAtUtc = DateTimeOffset.UtcNow;
                await TryRecordVoiceEventAsync(
                    "VOICE_COMMAND",
                    new { eventId = Guid.NewGuid().ToString("N"), intent = command.Intent.ToString(), traceId, commandId, capturedAtUtc = DateTimeOffset.UtcNow },
                    CancellationToken.None);
                _speech.CancelAll();
                return new VoiceResponse("Ответ остановлен", false, true, CommandId: commandId, TraceId: traceId);
            }
            _lastCommandFingerprint = fingerprint;
            _lastCommandAtUtc = DateTimeOffset.UtcNow;
            if (!_state.TryExecute(command)) return await RespondAsync("Команда недоступна в текущем состоянии", cancellationToken, false);

            // Persist the command before touching Recorder so the command and
            // any system response have a deterministic timeline order. A START
            // has no session yet; the same stable event id is replayed after
            // Recorder returns its localSessionId.
            var commandEventPayload = new
            {
                eventId = Guid.NewGuid().ToString("N"),
                intent = command.Intent.ToString(),
                parameter = command.Parameter,
                traceId,
                commandId,
                capturedAtUtc = DateTimeOffset.UtcNow
            };
            var commandEventSaved = await TryRecordVoiceEventAsync(
                "VOICE_COMMAND",
                commandEventPayload);
            if (!commandEventSaved)
                _logger?.LogWarning("VOICE_COMMAND event could not be persisted. TraceId={TraceId}", traceId);
            var response = command.Intent switch
            {
                VoiceIntent.StartRecording => await SendRecorderAsync("START", new { }, cancellationToken, traceId, commandId, command.Confidence),
                VoiceIntent.PauseRecording => await SendRecorderAsync("PAUSE", new { }, cancellationToken, traceId, commandId, command.Confidence),
                VoiceIntent.ResumeRecording => await SendRecorderAsync("RESUME", new { }, cancellationToken, traceId, commandId, command.Confidence),
                VoiceIntent.StopRecording => await SendRecorderAsync("STOP", new { }, cancellationToken, traceId, commandId, command.Confidence),
                VoiceIntent.AddMarker => await SendRecorderAsync("MARKER", new { label = command.Parameter }, cancellationToken, traceId, commandId, command.Confidence),
                VoiceIntent.MarkDecision => await SendRecorderAsync("DECISION", new { label = command.Parameter }, cancellationToken, traceId, commandId, command.Confidence),
                VoiceIntent.MarkActionItem => await SendRecorderAsync("ACTION_ITEM", new { label = command.Parameter }, cancellationToken, traceId, commandId, command.Confidence),
                VoiceIntent.GetStatus => await SendRecorderAsync("STATUS", new { }, cancellationToken, traceId, commandId, command.Confidence),
                VoiceIntent.HistoryQuestion when _desktopBroker is null => new VoiceResponse("Вопросы доступны только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId),
                VoiceIntent.HistoryQuestion => await AskAssistantAsync(command.Parameter ?? command.Text, commandId, traceId, cancellationToken),
                _ => new VoiceResponse("Команда не распознана", true, false)
            };
            if (command.Intent == VoiceIntent.StartRecording)
            {
                if (response.Success && !string.IsNullOrWhiteSpace(response.LocalSessionId))
                {
                    // This is idempotent: if the pre-START request reached the
                    // spool it promotes the pending row; if it did not, this
                    // call creates the event directly in the new session.
                    var persisted = false;
                    for (var attempt = 0; attempt < 3 && !persisted; attempt++)
                    {
                        persisted = await TryRecordVoiceEventAsync("VOICE_COMMAND", commandEventPayload, CancellationToken.None, response.LocalSessionId);
                        if (!persisted && attempt < 2)
                            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), _shutdown.Token).ConfigureAwait(false);
                    }
                    if (!persisted)
                        _logger?.LogWarning("VOICE_COMMAND could not be attached to START session. Session={SessionId}, TraceId={TraceId}", response.LocalSessionId, traceId);
                }
                else if (!response.Success)
                {
                    // Do not attach a failed START to a future meeting. If the
                    // broker was available, this removes its pending row; a
                    // later TTL prune covers a broker outage.
                    await TryRecordVoiceEventAsync(
                        "VOICE_COMMAND",
                        new { eventId = commandEventPayload.eventId, discardPending = true },
                        CancellationToken.None);
                }
            }
            _lastCommandLatencyMs = _commandStartedAt == default ? null : (DateTimeOffset.UtcNow - _commandStartedAt).TotalMilliseconds;
            return await RespondAsync(response.Text, cancellationToken, response.Success, localSessionId: response.LocalSessionId, commandId: commandId, traceId: traceId);
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

    private async Task<VoiceResponse> SendRecorderAsync(string command, object payload, CancellationToken cancellationToken, string? traceId = null, string? commandId = null, double confidence = 1.0)
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
            var broker = await _desktopBroker.ExecuteAsync(intent.ToString(), _pendingRecognizedText ?? command, Math.Clamp(confidence, 0d, 1d), false, cancellationToken, traceId, commandId);
            _lastTraceId = broker.TraceId ?? traceId ?? _lastTraceId;
            _recorderAckLatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!broker.Ok)
            {
                _lastErrorCode = broker.ErrorCode ?? "VOICE_COMMAND_REJECTED";
                return new VoiceResponse(VoiceErrorText(_lastErrorCode), true, false, CommandId: commandId, TraceId: traceId);
            }
            _recorderPipeReady = true;
            _recorderPipeError = null;
            UpdateLiveRecordingState(command, broker.LocalSessionId);
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
            }, true, true, broker.LocalSessionId, commandId, traceId);
        }
        var result = await _recorder.SendAsync(command, payload, cancellationToken);
        _recorderAckLatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _recorderPipeReady = true;
        _recorderPipeError = null;
        if (result.ProtocolVersion != AgentIpcProtocol.Version)
        {
            _recorderPipeReady = false;
            _recorderPipeError = "RECORDER_PROTOCOL_MISMATCH";
            return new VoiceResponse("Версия Recorder Service несовместима с приложением", true, false, CommandId: commandId, TraceId: traceId);
        }
        if (!result.Ok) return new VoiceResponse(VoiceErrorText(result.Error ?? "VOICE_RECORDER_UNAVAILABLE"), true, false, CommandId: commandId, TraceId: traceId);
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
        UpdateLiveRecordingState(command, result.SessionId);
        return new VoiceResponse(text, true, true, result.SessionId, commandId, traceId);
    }

    private void UpdateLiveRecordingState(string command, string? sessionId)
    {
        switch (command)
        {
            case "START":
                Interlocked.Exchange(ref _liveRecordingActive, 1);
                Interlocked.Exchange(ref _liveRecordingPaused, 0);
                _liveRecordingSessionId = Guid.TryParse(sessionId, out var parsed) ? parsed : null;
                _liveRecordingStartedAt = DateTimeOffset.UtcNow;
                lock (_recognitionGate) _liveRecognizer?.ResetSession();
                break;
            case "PAUSE":
                Interlocked.Exchange(ref _liveRecordingPaused, 1);
                break;
            case "RESUME":
                Interlocked.Exchange(ref _liveRecordingPaused, 0);
                break;
            case "STOP":
                Interlocked.Exchange(ref _liveRecordingActive, 0);
                Interlocked.Exchange(ref _liveRecordingPaused, 0);
                _liveRecordingSessionId = null;
                _liveRecordingStartedAt = default;
                lock (_recognitionGate) _liveRecognizer?.ResetSession();
                break;
        }
    }

    private static string VoiceErrorText(string code) => code switch
    {
        "VOICE_DESKTOP_BROKER_UNAVAILABLE" => "Приложение Desktop не отвечает.",
        "VOICE_RECORDER_UNAVAILABLE" => "Recorder недоступен.",
        "VOICE_MODEL_MISSING" => "Не найдена модель распознавания голоса.",
        "VOICE_MODEL_INTEGRITY_FAILED" => "Модель распознавания повреждена.",
        "VOICE_NATIVE_RUNTIME_UNAVAILABLE" => "Не доступен native runtime Vosk.",
        "VOICE_RUSSIAN_VOICE_UNAVAILABLE" => "Не найден установленный русский голос Windows (например, Microsoft Irina).",
        "VOICE_MICROPHONE_UNAVAILABLE" => "Микрофон недоступен или запрещён Windows.",
        "VOICE_HOST_OWNER_MISMATCH" => "Voice Host запущен от другого пользователя Windows.",
        "VOICE_HOST_PROCESS_UNINSPECTABLE" => "Не удалось проверить владельца или путь Voice Host.",
        "VOICE_HOST_RESTART_LIMIT" => "Voice Host часто завершается; автоматические перезапуски временно остановлены.",
        "VOICE_HOST_SHUTDOWN_TIMEOUT" => "Voice Host не завершился штатно.",
        "VOICE_COMMAND_REJECTED" => "Команда отклонена текущим состоянием записи.",
        "RECORDER_HOST_NOT_INITIALIZED" => "Recorder ещё запускается, повторите команду через несколько секунд.",
        "VOICE_HOST_NOT_INITIALIZED" => "Мифодий ещё запускается, повторите команду через несколько секунд.",
        "VOICE_ASSISTANT_DESKTOP_REQUIRED" => "Откройте Desktop, чтобы задавать вопросы по совещаниям.",
        "ASSISTANT_RECORDING_ACTIVE" => "Свежий контекст совещания ещё не готов.",
        "LIVE_MEETING_REQUIRED" => "Откройте текущее совещание, чтобы задать вопрос во время записи.",
        "LIVE_MEETING_NOT_READY" => "Пока нет свежего фрагмента совещания для ответа.",
        "VOICE_ASSISTANT_UNAVAILABLE" => "Помощник временно недоступен.",
        "ASSISTANT_MEETING_REQUIRED" => "Откройте совещание, по которому нужен ответ.",
        "ASSISTANT_HISTORY_FORBIDDEN" => "История совещаний недоступна в текущем контексте.",
        "ASSISTANT_CONTEXT_REQUIRED" => "Уточните, отвечать по текущему совещанию или в общем чате.",
        "NO_EVIDENCE" => "В стенограмме не найден подтверждённый ответ.",
        "LOW_TRANSCRIPT_QUALITY" => "Стенограмма требует проверки качества перед ответом.",
        "GROUNDING_REJECTED" => "Не удалось подтвердить ответ по стенограмме.",
        "ASSISTANT_NO_GROUNDED_ANSWER" => "В стенограмме не найден подтверждённый ответ.",
        "VOICE_CONFIDENCE_TOO_LOW" => "Не уверен, что правильно вас расслышал. Повторите команду.",
        "VOICE_ASSISTANT_EMPTY_ANSWER" => "Помощник не получил содержательного ответа.",
        "VOICE_HOST_BUSY" => "Мифодий занят предыдущим ответом. Скажите «Мифодий, остановись» или повторите позже.",
        "VOICE_ASSISTANT_QUEUE_FULL" => "Слишком много вопросов ожидает ответа. Повторите позже.",
        "VOICE_PLAYBACK_LEDGER_UNAVAILABLE" => "Не удалось безопасно подготовить голосовой ответ. Повторю попытку позже.",
        "NO_AUDIO_CAPTURED" => "Аудио не было захвачено, запись не сохранена.",
        _ => "Не удалось выполнить голосовую команду."
    };

    private async Task<VoiceResponse> AskAssistantAsync(string question, string? commandId, string? traceId, CancellationToken cancellationToken)
    {
        if (_desktopBroker is null)
            return new VoiceResponse("Вопросы доступны только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId);
        var (requestedMode, normalizedQuestion) = ResolveAssistantQuestion(question);
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
            return new VoiceResponse("Сформулируйте вопрос после кодовой фразы «Мифодий».", true, false, CommandId: commandId, TraceId: traceId);
        var result = await _desktopBroker.AskAssistantAsync(normalizedQuestion, requestedMode, false, cancellationToken, traceId, commandId);
        if (result.Ok && !string.IsNullOrWhiteSpace(result.QueryId))
        {
            // Keep the server query identity on the immediate acceptance ACK.
            // Desktop uses the same queryId for the later grounded result and
            // the Voice Host tombstone, which lets the production gate verify
            // the real Voice Host -> Desktop Broker path without relying on
            // timing or parsing the spoken acknowledgement.
            var acceptance = await RespondAsync(
                result.SpokenText ?? "Вопрос принят, отвечу после обработки.",
                cancellationToken,
                true,
                commandId: commandId,
                traceId: traceId,
                answerStatus: result.AssistantStatus);
            // The acceptance acknowledgement is a local prompt, not the
            // grounded answer. Keep the queryId in the IPC response so a
            // production gate can correlate it, but do not put it into the
            // playback tombstone: the later SPEAK_ASSISTANT_RESULT for the
            // same query must still be allowed to play exactly once.
            return acceptance with { QueryId = result.QueryId };
        }
        return new VoiceResponse(VoiceErrorText(result.ErrorCode ?? "VOICE_ASSISTANT_UNAVAILABLE"), true, false, CommandId: commandId, TraceId: traceId);
    }

    private static (string RequestedMode, string Question) ResolveAssistantQuestion(string question)
    {
        var normalized = (question ?? string.Empty).Trim();
        if (normalized.StartsWith("в общем чате ", StringComparison.OrdinalIgnoreCase))
            return ("GENERAL_CHAT", normalized["в общем чате ".Length..].Trim());
        if (normalized.StartsWith("общий вопрос ", StringComparison.OrdinalIgnoreCase))
            return ("GENERAL_CHAT", normalized["общий вопрос ".Length..].Trim());
        if (normalized.StartsWith("по истории совещаний ", StringComparison.OrdinalIgnoreCase))
            return ("MEETING_HISTORY", normalized["по истории совещаний ".Length..].Trim());
        if (normalized.StartsWith("по текущему совещанию ", StringComparison.OrdinalIgnoreCase))
            return ("CURRENT_MEETING", normalized["по текущему совещанию ".Length..].Trim());
        return ("AUTO", normalized);
    }

    private async Task<VoiceResponse> RespondAsync(
        string text,
        CancellationToken cancellationToken,
        bool success = true,
        VoiceHostState? returnState = null,
        string? localSessionId = null,
        string? commandId = null,
        string? traceId = null,
        string? queryId = null,
        string? answerStatus = null,
        string? reservedResponseId = null)
    {
        var target = returnState ?? (_state.Snapshot.State == VoiceHostState.Confirming ? VoiceHostState.Confirming : VoiceHostState.Listening);
        _state.TryRespond(text, target);
        // Never let pre-wake audio from the response itself become the next
        // command's context. The ring remains in-memory only and is rebuilt
        // from microphone frames after playback/cooldown.
        _preRoll.Clear();
        if (_speech.QuietMode)
        {
            // Quiet mode is an intentional no-playback result, not a busy or
            // failed TTS engine. Do not create a synthetic technical interval
            // when no system audio was emitted.
            _state.FinishResponse();
            if (_state.Snapshot.State == VoiceHostState.Cooldown) _ = CompleteCooldownAsync();
            return new VoiceResponse(text, false, success, localSessionId, commandId, traceId, queryId, PlaybackState: "CANCELLED", AcceptedForPlayback: false, AnswerStatus: answerStatus);
        }
        var responseId = reservedResponseId ?? Guid.NewGuid().ToString("N");
        var responseEventId = Guid.NewGuid().ToString("N");
        commandId ??= _lastCommandId;
        traceId ??= _lastTraceId;
        // The technical interval is opened by the speech worker immediately
        // before SpeakAsync, not here.  A queued response must not hide the
        // user's speech while it is waiting behind another response.
        var enqueued = _speech.TryEnqueueDetailed(
            text,
            async () =>
            {
                // The queue acceptance ACK is deliberately kept separate from
                // the moment audio starts.  Persist the intermediate state so
                // a duplicate query received while this item is speaking is
                // suppressed without being mistaken for a new playback.
                UpdateAssistantTombstone(queryId, "PLAYBACK_STARTED");
                var responseEventSaved = await TryRecordVoiceEventAsync(
                    "SYSTEM_RESPONSE_STARTED",
                    new { eventId = responseEventId, responseId, queryId, commandId, traceId, localSessionId },
                    CancellationToken.None,
                    localSessionId);
                if (!responseEventSaved)
                    _logger?.LogWarning("SYSTEM_RESPONSE_STARTED event could not be persisted. ResponseId={ResponseId}", responseId);
            },
            out var playbackCompleted);
        if (enqueued)
        {
            _ = CompleteResponseAfterPlaybackAsync(responseId, playbackCompleted, localSessionId, commandId, traceId, queryId);
        }
        else
        {
            _lastErrorCode = _speech.IsRussianVoiceAvailable
                ? (_speech.QueueDepth >= 8 ? "VOICE_ASSISTANT_QUEUE_FULL" : "VOICE_HOST_BUSY")
                : "VOICE_RUSSIAN_VOICE_UNAVAILABLE";
            await TryRecordVoiceEventAsync("SYSTEM_RESPONSE_FINISHED", new { eventId = Guid.NewGuid().ToString("N"), responseId, queryId, commandId, traceId, localSessionId, playbackStarted = false, cancelled = false }, CancellationToken.None, localSessionId);
            _state.FinishResponse();
            if (_state.Snapshot.State == VoiceHostState.Cooldown) _ = CompleteCooldownAsync();
        }
        return new VoiceResponse(text, enqueued, success, localSessionId, commandId, traceId, queryId, enqueued ? responseId : null, enqueued ? "ACCEPTED" : "FAILED", enqueued, answerStatus);
    }

    private async Task CompleteResponseAfterPlaybackAsync(string responseId, Task<SpeechPlaybackResult> playbackCompleted, string? localSessionId, string? commandId, string? traceId, string? queryId)
    {
        var playback = new SpeechPlaybackResult(SpeechPlaybackState.Failed, false, "VOICE_TTS_FAILED");
        try
        {
            playback = await playbackCompleted.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            playback = new SpeechPlaybackResult(SpeechPlaybackState.Cancelled, true);
        }
        finally
        {
            var cancelled = playback.State == SpeechPlaybackState.Cancelled || _shutdown.IsCancellationRequested;
            var finishedSaved = await TryRecordVoiceEventAsync("SYSTEM_RESPONSE_FINISHED", new { eventId = Guid.NewGuid().ToString("N"), responseId, queryId, commandId, traceId, localSessionId, playbackStarted = playback.Started, playbackState = playback.State.ToString().ToUpperInvariant(), cancelled }, CancellationToken.None, localSessionId);
            if (!finishedSaved)
                _logger?.LogWarning("SYSTEM_RESPONSE_FINISHED event could not be persisted. ResponseId={ResponseId}", responseId);
            // A completion can race the responder's queue bookkeeping. Wait
            // for the queue to become genuinely idle so a following response
            // keeps RESPONDING instead of briefly exposing LISTENING.
            await DrainAfterSpeechAsync().ConfigureAwait(false);
            _state.FinishResponse();
            _preRoll.Clear();
            if (!cancelled && _state.Snapshot.State == VoiceHostState.Cooldown) await CompleteCooldownAsync();
            if (!string.IsNullOrWhiteSpace(queryId))
            {
                var state = playback.State switch
                {
                    SpeechPlaybackState.Played => "PLAYED",
                    SpeechPlaybackState.Cancelled => "CANCELLED",
                    _ => "FAILED"
                };
                UpdateAssistantTombstone(queryId, state);
                if (_desktopBroker is not null)
                    _ = _desktopBroker.PublishAssistantPlaybackFinishedAsync(queryId, responseId, state, cancelled, commandId, traceId, localSessionId, CancellationToken.None);
            }
        }
    }
    private async Task CompleteCooldownAsync()
    {
        try
        {
            _preRoll.Clear();
            await Task.Delay(TimeSpan.FromMilliseconds(300), _shutdown.Token);
            _preRoll.Clear();
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
            _utteranceRecognizer?.ResetSession();
            _cancelRecognizer?.ResetSession();
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
        _preRoll.Clear();
    }

    private void ReplayPreRoll(byte[] pcm)
    {
        const int frameBytes = 640;
        for (var offset = 0; offset < pcm.Length; offset += frameBytes)
        {
            var length = Math.Min(frameBytes, pcm.Length - offset);
            _utteranceRecognizer!.Accept(pcm.AsSpan(offset, length).ToArray());
        }
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

    private static string NormalizeCommandText(string? text) =>
        string.Join(' ', (text ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private async Task<bool> TryRecordVoiceEventAsync(string eventType, object payload, CancellationToken? cancellationToken = null, string? localSessionId = null)
    {
        try
        {
            bool ok;
            var token = cancellationToken ?? CancellationToken.None;
            if (_desktopBroker is not null)
                ok = (await _desktopBroker.RecordEventAsync(eventType, payload, token, localSessionId)).Ok;
            else
                ok = (await _recorder.SendAsync("VOICE_EVENT", new { eventType, payload, localSessionId }, token)).Ok;
            if (!ok) _lastErrorCode = "VOICE_EVENT_PERSIST_FAILED";
            return ok;
        }
        catch (Exception ex)
        {
            _lastErrorCode = "VOICE_EVENT_PERSIST_FAILED";
            _logger?.LogDebug(ex, "Recorder event could not be persisted: {EventType}", eventType);
            return false;
        }
    }

    private static string? ReadString(JsonElement payload, string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? ReadNullableInt(JsonElement payload, string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
    private static bool ReadBool(JsonElement payload, string name, bool fallback) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private void LoadAssistantTombstones()
    {
        lock (_assistantTombstoneGate)
        {
            try
            {
                if (File.Exists(_assistantTombstonePath))
                    _assistantTombstones = JsonSerializer.Deserialize<Dictionary<string, AssistantPlaybackTombstone>>(File.ReadAllText(_assistantTombstonePath))
                        ?? new(StringComparer.Ordinal);
                foreach (var queryId in _assistantTombstones.Where(item => item.Value.State == "RESERVED").Select(item => item.Key).ToArray())
                {
                    var interrupted = _assistantTombstones[queryId];
                    _assistantTombstones[queryId] = interrupted with { State = "AMBIGUOUS", UpdatedAt = DateTimeOffset.UtcNow };
                }
            }
            catch { _assistantTombstones = new(StringComparer.Ordinal); }
            PruneAssistantTombstonesLocked(DateTimeOffset.UtcNow);
            PersistAssistantTombstonesLocked();
        }
    }

    private bool TryGetAssistantTombstone(string queryId, out AssistantPlaybackTombstone tombstone)
    {
        lock (_assistantTombstoneGate)
        {
            PruneAssistantTombstonesLocked(DateTimeOffset.UtcNow);
            return _assistantTombstones.TryGetValue(queryId, out tombstone!);
        }
    }

    private bool SaveAssistantTombstone(string queryId, string responseId, string state, string? answerStatus)
    {
        if (string.IsNullOrWhiteSpace(queryId)) return false;
        lock (_assistantTombstoneGate)
        {
            PruneAssistantTombstonesLocked(DateTimeOffset.UtcNow);
            var previous = _assistantTombstones.TryGetValue(queryId, out var existing) ? existing : null;
            _assistantTombstones[queryId] = new(responseId, state, answerStatus, DateTimeOffset.UtcNow);
            while (_assistantTombstones.Count > 256)
            {
                var oldest = _assistantTombstones.OrderBy(item => item.Value.UpdatedAt).First().Key;
                _assistantTombstones.Remove(oldest);
            }
            if (PersistAssistantTombstonesLocked()) return true;
            if (previous is null) _assistantTombstones.Remove(queryId);
            else _assistantTombstones[queryId] = previous;
            return false;
        }
    }

    private void UpdateAssistantTombstone(string? queryId, string state)
    {
        if (string.IsNullOrWhiteSpace(queryId)) return;
        lock (_assistantTombstoneGate)
        {
            if (_assistantTombstones.TryGetValue(queryId, out var current))
            {
                _assistantTombstones[queryId] = current with { State = state, UpdatedAt = DateTimeOffset.UtcNow };
                PersistAssistantTombstonesLocked();
            }
        }
    }

    private void PruneAssistantTombstonesLocked(DateTimeOffset now)
    {
        var changed = false;
        foreach (var key in _assistantTombstones.Where(item => now - item.Value.UpdatedAt > TimeSpan.FromDays(7)).Select(item => item.Key).ToArray())
        {
            _assistantTombstones.Remove(key);
            changed = true;
        }
        if (changed) PersistAssistantTombstonesLocked();
    }

    private bool PersistAssistantTombstonesLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_assistantTombstonePath)!;
            Directory.CreateDirectory(directory);
            var temporary = _assistantTombstonePath + ".part";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_assistantTombstones));
            File.Move(temporary, _assistantTombstonePath, true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not persist assistant playback tombstones.");
            return false;
        }
    }

    private sealed record AssistantPlaybackTombstone(string ResponseId, string State, string? AnswerStatus, DateTimeOffset UpdatedAt);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _audio.Stop();
        _audioQueue.Writer.TryComplete();
        _shutdown.Cancel();
        try { if (_audioWorker is not null) await _audioWorker.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { if (_liveStatusPollTask is not null) await _liveStatusPollTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _utteranceRecognizer?.Dispose();
        _wakeRecognizer?.Dispose();
        _cancelRecognizer?.Dispose();
        _liveRecognizer?.Dispose();
        _speech.Dispose();
        _executionGate.Dispose();
        _audioOperationGate.Dispose();
        _livePublishGate.Dispose();
        _shutdown.Dispose();
        _pttBuffer.Dispose();
    }
}

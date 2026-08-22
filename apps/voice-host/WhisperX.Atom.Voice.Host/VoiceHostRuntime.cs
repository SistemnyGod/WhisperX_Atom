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
    // "Атом" was a temporary migration alias. It is deliberately disabled
    // by default in production because it occurs frequently in meeting speech
    // and causes false activations. Set WHISPERX_WAKE_COMPAT_ATOM=true only
    // for an explicit legacy/development rollout.
    internal static readonly bool LegacyAtomWakeEnabled = IsTruthy(
        Environment.GetEnvironmentVariable("WHISPERX_WAKE_COMPAT_ATOM"));
    private static readonly bool WakeBargeInEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("VOICE_BARGE_IN_MODE"), "OFF", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] WakeGrammar = CreateWakeGrammar();
    private static readonly string[] CancelGrammar = CreateCancelGrammar();
    private static readonly string[] BargeGrammar = CreateBargeGrammar();

    private static string[] CreateBargeGrammar() => LegacyAtomWakeEnabled
        ? ["мефодий", "мифодий", "атом", "atom", "[unk]"]
        : ["мефодий", "мифодий", "[unk]"];

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static string[] CreateWakeGrammar()
    {
        var phrases = new List<string>
        {
            // The bundled small RU model has “мефодий” in its vocabulary and
            // commonly decodes the spoken “мифодий” into that phonetic variant.
            // Keep the canonical spelling in the parser while using the model
            // vocabulary here to avoid Vosk silently dropping the primary token.
            "мефодий", "мефодий начни запись", "мефодий запусти запись", "мефодий продолжи запись",
            "мефодий поставь на паузу", "мефодий приостанови запись", "мефодий статус", "мефодий заверши запись", "мефодий останови запись", "мефодий подтверждаю", "мефодий отмена",
            "мефодий останови запись", "мефодий пока", "мефодий ну пока", "мефодий ладно пока",
            "мефодий до свидания", "мефодий до встречи", "мефодий всего доброго", "мефодий хорошего дня",
            "мефодий спокойной ночи", "мефодий увидимся", "мефодий спасибо пока", "мефодий спасибо до свидания",
            "мефодий состояние сервера", "мефодий сервер доступен", "мефодий сервер работает", "мефодий состояние обработки", "мефодий статус обработки",
            "мефодий как идёт обработка", "мефодий как идет обработка", "мефодий какая стадия обработки", "мефодий стенограмма готова", "мефодий состояние диска", "мефодий свободное место", "мефодий сколько места", "мефодий сколько осталось места",
            "мифодий начни запись", "мифодий запусти запись", "мифодий останови запись", "мифодий подтверждаю",
            "мифодий состояние сервера", "мифодий сервер доступен", "мифодий сервер работает", "мифодий состояние обработки", "мифодий статус обработки",
            "мифодий как идёт обработка", "мифодий как идет обработка", "мифодий какая стадия обработки", "мифодий стенограмма готова", "мифодий состояние диска", "мифодий свободное место", "мифодий сколько места", "мифодий сколько осталось места",
            "мифодий пока", "мифодий до свидания", "мифодий до встречи", "мифодий хорошего дня", "[unk]"
        };
        if (LegacyAtomWakeEnabled)
        {
            phrases.AddRange([
                "атом начни запись", "атом запусти запись", "атом останови запись", "атом подтверждаю",
                "атом пока", "атом до свидания", "атом до встречи", "атом хорошего дня"
            ]);
        }
        if (ExactWakeWordRequested)
        {
            // A custom/larger model can opt into the canonical spelling. The
            // phonetic fallback remains in the grammar for compatibility.
            phrases.AddRange([
                "мифодий", "мифодий начни запись", "мифодий запусти запись", "мифодий останови запись", "мифодий подтверждаю",
                "мифодий пока", "мифодий ну пока", "мифодий ладно пока", "мифодий до свидания", "мифодий до встречи",
                "мифодий всего доброго", "мифодий хорошего дня", "мифодий спокойной ночи", "мифодий увидимся",
                "мифодий спасибо пока", "мифодий спасибо до свидания", "мифодий состояние сервера", "мифодий сервер доступен", "мифодий сервер работает",
                "мифодий состояние обработки", "мифодий статус обработки", "мифодий как идёт обработка", "мифодий как идет обработка",
                "мифодий какая стадия обработки", "мифодий стенограмма готова", "мифодий состояние диска", "мифодий свободное место",
                "мифодий сколько места", "мифодий сколько осталось места"
            ]);
        }
        return phrases.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] CreateCancelGrammar()
    {
        var phrases = new List<string>
        {
            "мефодий остановись", "мефодий замолчи", "мефодий прекрати говорить", "мефодий останови ответ",
            "мифодий остановись", "мифодий замолчи", "мифодий прекрати говорить", "мифодий останови ответ", "[unk]"
        };
        if (LegacyAtomWakeEnabled)
            phrases.AddRange(["атом остановись", "атом замолчи", "атом прекрати говорить", "атом останови ответ"]);
        return phrases.Distinct(StringComparer.Ordinal).ToArray();
    }

    // Recorder actions stay deterministic in VoiceIntentParser.  The audio
    // recognizer used after a wake word intentionally has no grammar: a
    // meeting question cannot be represented by the short command grammar.
    internal static IReadOnlyList<string> WakePhrases => WakeGrammar;
    private readonly VoiceStateMachine _state = new();
    private readonly VoiceIntentParser _parser = new(LegacyAtomWakeEnabled);
    private readonly VoiceAudioCapture _audio;
    private readonly RecorderPipeClient _recorder = new();
    private readonly SpeechResponder _speech = new(BuildIdentity);
    private readonly VoiceLedgerStore _voiceLedger = new();
    private readonly VoskRecognizer? _wakeRecognizer;
    private readonly VoskRecognizer? _utteranceRecognizer;
    private readonly VoskRecognizer? _cancelRecognizer;
    private readonly VoskRecognizer? _bargeRecognizer;
    private readonly VoskRecognizer? _liveRecognizer;
    private readonly VoskRecognizer? _liveLocalRecognizer;
    private readonly VoskRecognizer? _liveRemoteRecognizer;
    private readonly LiveAudioClient _liveAudio;
    private readonly Channel<LiveAudioFrameDto> _liveFrameQueue = Channel.CreateBounded<LiveAudioFrameDto>(new BoundedChannelOptions(128)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
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
    private readonly VoiceAudioFrontEnd _voiceFrontEnd = new();
    private readonly object _calibrationGate = new();
    private readonly PcmFrameAssembler _frameAssembler = new();
    // Two seconds of 16 kHz mono PCM16 (64,000 bytes) are retained only in
    // memory so the first words after the wake word are available to the
    // unrestricted recognizer. The buffer is never persisted or transmitted.
    private readonly VoiceRingBuffer _preRoll = new(64_000);
    private AudioPcmConverter? _converter;
    private Task? _audioWorker;
    private Task? _liveStatusPollTask;
    private Task? _liveAudioWorker;
    private DateTimeOffset _wakeStartedAt;
    private DateTimeOffset _commandStartedAt;
    private DateTimeOffset _lastSpeechAt;
    private DateTimeOffset _liveRecordingStartedAt;
    private string? _pendingRecognizedText;
    private Guid? _liveRecordingSessionId;
    private int _liveAudioConnected;
    private int _liveSystemAudioEnabled;
    private long _liveAudioDrops;
    private long _liveSegmentsPublished;
    private long _liveSegmentsSuppressed;
    // TTS can arrive on both the room and loopback tracks.  The responder's
    // busy flag covers the actual queue/playback; this monotonic deadline also
    // protects the short audio tail after playback finishes.
    private long _liveTtsSuppressionUntilTicks;
    private static readonly long LiveTtsTailTicks = Math.Max(1,
        Stopwatch.Frequency * 400 / 1000);
    private string _liveRoomTrackState = "WAITING";
    private string _liveSystemTrackState = "WAITING";

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
    private DateTimeOffset? _lastWakeAtUtc;
    private DateTimeOffset? _lastUtteranceAtUtc;
    private DateTimeOffset? _lastAssistantAcceptedAtUtc;
    private DateTimeOffset? _lastTtsStartedAtUtc;
    private DateTimeOffset? _lastTtsFinishedAtUtc;
    private double? _lastTtsQueueWaitMs;
    private double? _lastTtsSynthesisMs;
    private double? _lastTtsPlaybackMs;
    private string? _lastAssistantQueryId;
    private DateTimeOffset _wakeSpeechStartedAt;
    private long _audioQueueDrops;
    private int _audioQueueDepth;
    private int _wakePartialHits;
    private int _bargeHits;
    private DateTimeOffset _lastBargeHitAtUtc;
    private bool _bargeDucked;
    private int _audioQueueOverflow;
    private VoiceCalibrationAccumulator? _calibration;
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
            "Voice response engine constructed. RequestedEngine={Engine}, Voice={Voice}, Culture={Culture}",
            _speech.TtsEngine,
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
                _bargeRecognizer = new VoskRecognizer(modelPath, grammar: BargeGrammar);
                _liveRecognizer = _wakeRecognizer.CreateUnrestrictedSession();
                _liveLocalRecognizer = _wakeRecognizer.CreateUnrestrictedSession();
                _liveRemoteRecognizer = _wakeRecognizer.CreateUnrestrictedSession();
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

        _liveAudio = new LiveAudioClient();
        _liveAudio.FrameReceived += OnLiveAudioFrame;
        _liveAudio.SessionChanged += OnLiveAudioSessionChanged;
        _liveAudio.ConnectionChanged += OnLiveAudioConnectionChanged;
        _liveAudio.Start();
        _liveAudioWorker = Task.Run(ProcessLiveAudioFramesAsync);
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
                LiveAudioMode = Volatile.Read(ref _liveAudioConnected) == 1
                    ? (Volatile.Read(ref _liveSystemAudioEnabled) == 1 ? "DUAL_TRACK" : "MIC_ONLY")
                    : "MIC_FALLBACK",
                LiveRoomTrackState = _liveRoomTrackState,
                LiveSystemTrackState = _liveSystemTrackState,
                LiveAudioDrops = Interlocked.Read(ref _liveAudioDrops) + _liveAudio.Drops,
                LiveSegmentsPublished = Interlocked.Read(ref _liveSegmentsPublished),
                LiveSegmentsSuppressed = Interlocked.Read(ref _liveSegmentsSuppressed),
                TtsEngine = _speech.TtsEngine,
                TtsModel = _speech.TtsModel,
                TtsReady = _speech.TtsReady,
                TtsVoice = _speech.VoiceName,
                TtsCulture = _speech.VoiceCulture,
                TtsSampleRate = _speech.TtsEngine == "SILERO" ? _speech.TtsSampleRate : null,
                TtsCpuThreads = _speech.TtsEngine == "SILERO" ? _speech.TtsCpuThreads : null,
                TtsHostProcessId = _speech.TtsHostProcessId,
                TtsModelLoadMs = _speech.TtsModelLoadMs,
                TtsLastSynthesisMs = _speech.TtsLastSynthesisMs,
                TtsFallbackUsed = _speech.VoiceFallbackUsed,
                TtsFallbackReason = _speech.TtsFallbackReason,
                TtsRestartCount = _speech.TtsRestartCount,
                VoiceNoiseFloorDb = _vad.NoiseFloorDb,
                VoiceVadThresholdDb = _vad.ThresholdDb,
                LastWakeAtUtc = _lastWakeAtUtc,
                LastUtteranceAtUtc = _lastUtteranceAtUtc,
                LastAssistantAcceptedAtUtc = _lastAssistantAcceptedAtUtc,
                LastTtsStartedAtUtc = _lastTtsStartedAtUtc,
                LastTtsFinishedAtUtc = _lastTtsFinishedAtUtc,
                LastTtsQueueWaitMs = _lastTtsQueueWaitMs,
                LastTtsSynthesisMs = _lastTtsSynthesisMs,
                LastTtsPlaybackMs = _lastTtsPlaybackMs,
                LastAssistantQueryId = _lastAssistantQueryId,
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
            var windowsFallbackVoice = ReadString(payload, "windowsFallbackVoice") ?? voiceName;
            var voiceRate = ReadNullableInt(payload, "voiceRate");
            var voiceVolume = ReadNullableInt(payload, "voiceVolume");
            var ttsVoice = ReadString(payload, "ttsVoice");
            var ttsEngine = ReadString(payload, "ttsEngine");
            var ttsSampleRate = ReadNullableInt(payload, "ttsSampleRate");
            var ttsCpuThreads = ReadNullableInt(payload, "ttsCpuThreads");
            var ttsFallbackEnabled = ReadBool(payload, "ttsFallbackEnabled", true);
            var ttsReady = await _speech.ConfigureAsync(windowsFallbackVoice, voiceRate, voiceVolume, ttsVoice, ttsSampleRate, ttsCpuThreads, ttsFallbackEnabled, cancellationToken, ttsEngine).ConfigureAwait(false);
            _logger?.LogInformation(
                "Voice response engine configured. Engine={Engine}, Model={Model}, Voice={Voice}, Ready={Ready}, Fallback={Fallback}, FallbackReason={FallbackReason}, TtsHostPid={TtsHostPid}",
                _speech.TtsEngine,
                _speech.TtsModel,
                _speech.VoiceName,
                ttsReady,
                _speech.VoiceFallbackUsed,
                _speech.TtsFallbackReason ?? "none",
                _speech.TtsHostProcessId?.ToString() ?? "none");
            if (!ttsReady) _lastErrorCode = "VOICE_TTS_UNAVAILABLE";

            var normalized = string.IsNullOrWhiteSpace(requestedDevice) ? _microphoneDeviceId : requestedDevice.Trim();
            if (!string.Equals(normalized, _microphoneDeviceId, StringComparison.OrdinalIgnoreCase) || !_audio.IsRunning)
            {
                _microphoneDeviceId = normalized;
                if (_audio.IsRunning)
                {
                    _audio.Stop();
                    _voiceFrontEnd.Reset();
                    _vad.Reset();
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
            _voiceFrontEnd.Reset();
            _vad.Reset();
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
        var command = _parser.Parse(normalized, confidence, MinimumConfidence());
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
        if (!IsConfidenceSufficient(command))
        {
            _lastErrorCode = "VOICE_CONFIDENCE_TOO_LOW";
            return await RespondAsync(VoiceErrorText(_lastErrorCode), cancellationToken, false);
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
                var ttsTest = await _speech.TestAsync(cancellationToken).ConfigureAwait(false);
                return new VoiceHostResponse(ttsTest.State is SpeechPlaybackState.Played or SpeechPlaybackState.Accepted,
                    new { ok = ttsTest.State is SpeechPlaybackState.Played or SpeechPlaybackState.Accepted, playbackState = ttsTest.State.ToString().ToUpperInvariant(), errorCode = ttsTest.ErrorCode, engine = ttsTest.Engine, model = ttsTest.Model, voice = ttsTest.Voice, fallbackUsed = ttsTest.FallbackUsed });
            case "LIST_RUSSIAN_VOICES": return new VoiceHostResponse(true, new { voices = _speech.GetRussianVoiceNames(), selectedVoice = _speech.VoiceName });
            case "LIST_TTS_VOICES": return new VoiceHostResponse(true, new { voices = _speech.GetTtsVoices(), selectedVoice = _speech.RequestedVoiceName, engine = _speech.TtsEngine });
            case "SPEAK_ASSISTANT_RESULT":
            {
                var answer = ReadString(payload, "voiceAnswer") ?? string.Empty;
                var status = ReadString(payload, "status");
                var queryId = ReadString(payload, "queryId");
                var commandId = ReadString(payload, "commandId");
                var traceId = ReadString(payload, "traceId");
                _lastAssistantQueryId = queryId;
                if (string.IsNullOrWhiteSpace(answer) || status is not ("READY" or "ANSWERED" or "ANSWERED_WITH_WARNING" or "NEEDS_REVIEW" or "NO_EVIDENCE" or "LOW_TRANSCRIPT_QUALITY" or "GROUNDING_REJECTED" or "FAILED" or "LLM_UNAVAILABLE"))
                    return new VoiceHostResponse(false, Error: "VOICE_COMMAND_REJECTED");
                if (!string.IsNullOrWhiteSpace(queryId) && TryGetAssistantTombstone(queryId, out var previous))
                    return new VoiceHostResponse(true, new VoiceResponse(answer, false, previous.AnswerStatus is "READY" or "ANSWERED" or "ANSWERED_WITH_WARNING", CommandId: commandId, TraceId: traceId, QueryId: queryId, ResponseId: previous.ResponseId, PlaybackState: previous.State, AcceptedForPlayback: previous.State == "ACCEPTED", AnswerStatus: previous.AnswerStatus));
                QueueVoiceLedgerEvent("ASSISTANT_RESULT_READY", new
                {
                    eventId = Guid.NewGuid().ToString("N"),
                    queryId,
                    status,
                    commandId,
                    traceId,
                    capturedAtUtc = DateTimeOffset.UtcNow
                });
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
            case "ASSISTANT_PLAYBACK_STATUS":
            {
                var queryId = ReadString(payload, "queryId");
                if (string.IsNullOrWhiteSpace(queryId))
                    return new VoiceHostResponse(false, Error: "VOICE_COMMAND_REJECTED");
                if (!TryGetAssistantTombstone(queryId, out var tombstone))
                    return new VoiceHostResponse(true, new
                    {
                        queryId,
                        playbackState = "NOT_FOUND"
                    });
                return new VoiceHostResponse(true, new
                {
                    queryId,
                    responseId = tombstone.ResponseId,
                    playbackState = tombstone.State,
                    answerStatus = tombstone.AnswerStatus
                });
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
                var parsedCommand = _parser.Parse(text, confidence, MinimumConfidence());
                return new VoiceHostResponse(true, new
                {
                    recognizedText = text,
                    intent = parsedCommand.Intent.ToString(),
                    confidence = parsedCommand.Confidence,
                    wakeWord = _parser.HasWakeWord(text),
                    testMode = true
                });
            }
            case "CALIBRATION_START":
            {
                var phase = ReadString(payload, "phase") ?? "NOISE";
                lock (_calibrationGate)
                {
                    if (_calibration is not null) return new VoiceHostResponse(false, Error: "VOICE_CALIBRATION_BUSY");
                    _calibration = new VoiceCalibrationAccumulator(phase);
                }
                return new VoiceHostResponse(true, new { phase, state = "RUNNING", storesAudio = false });
            }
            case "CALIBRATION_STOP":
            {
                VoiceCalibrationAccumulator? calibration;
                lock (_calibrationGate)
                {
                    calibration = _calibration;
                    _calibration = null;
                }
                if (calibration is null) return new VoiceHostResponse(false, Error: "VOICE_CALIBRATION_NOT_RUNNING");
                var result = calibration.Complete();
                _vad.ApplyNoiseFloor(result.AverageRms, _sensitivity);
                return new VoiceHostResponse(true, result);
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
                    _voiceFrontEnd.Reset();
                    _vad.Reset();
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
                    lock (_calibrationGate) _calibration?.Add(pcm);
                    // Enhance only the derived Voice/Vosk path. Recorder
                    // durable PCM and playable/archive files remain original.
                    _voiceFrontEnd.Process(pcm);
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
        if (Volatile.Read(ref _liveRecordingActive) == 1 && Volatile.Read(ref _liveRecordingPaused) == 0
            && Volatile.Read(ref _liveAudioConnected) == 0)
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
                _lastWakeAtUtc = now;
                _wakeLatencyMs = _wakeSpeechStartedAt == default ? null : (now - _wakeSpeechStartedAt).TotalMilliseconds;
                QueueVoiceLedgerEvent("VOICE_WAKE_DETECTED", new
                {
                    eventId = Guid.NewGuid().ToString("N"),
                    capturedAtUtc = now,
                    wakeLatencyMs = _wakeLatencyMs,
                    wakeWordMode = WakeWordMode
                });
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
            var command = _parser.Parse(text, confidence, MinimumConfidence());
            _lastUtteranceAtUtc = DateTimeOffset.UtcNow;
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
            else if (command.Intent != VoiceIntent.Unknown && IsConfidenceSufficient(command))
            {
                EnsureRecognitionState(text);
                _intentLatencyMs = Math.Max(0, (DateTimeOffset.UtcNow - now).TotalMilliseconds);
                await ExecuteAsync(command, cancellationToken);
            }
            else
            {
                ResetRecognitionSessions();
                _lastErrorCode = command.Intent == VoiceIntent.Unknown ? "VOICE_COMMAND_REJECTED" : "VOICE_CONFIDENCE_TOO_LOW";
                await RespondAsync(VoiceErrorText(_lastErrorCode), cancellationToken, false);
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
        if (!WakeBargeInEnabled)
        {
            VoiceRecognitionResult disabledResult;
            lock (_recognitionGate) disabledResult = _cancelRecognizer?.Accept(pcm) ?? new VoiceRecognitionResult(null, null, false, 0);
            if (disabledResult.IsEndpoint) lock (_recognitionGate) _cancelRecognizer?.ResetSession();
            if (!disabledResult.IsEndpoint || string.IsNullOrWhiteSpace(disabledResult.Text)) return;
            var disabledCommand = _parser.Parse(disabledResult.Text, disabledResult.Confidence, 0.70);
            if (disabledCommand.Intent == VoiceIntent.StopSpeaking && disabledResult.Confidence >= 0.70)
                await CancelSpeechAsync("StopSpeaking", cancellationToken).ConfigureAwait(false);
            return;
        }
        VoiceRecognitionResult result;
        VoiceRecognitionResult barge;
        if (_bargeHits == 1 && _lastBargeHitAtUtc != default
            && DateTimeOffset.UtcNow - _lastBargeHitAtUtc > TimeSpan.FromSeconds(1.5))
        {
            _bargeHits = 0;
            _lastBargeHitAtUtc = default;
            if (_bargeDucked)
            {
                _speech.RestorePlaybackVolume();
                _bargeDucked = false;
            }
        }
        lock (_recognitionGate)
        {
            result = _cancelRecognizer?.Accept(pcm) ?? new VoiceRecognitionResult(null, null, false, 0);
            barge = _bargeRecognizer?.Accept(pcm) ?? new VoiceRecognitionResult(null, null, false, 0);
        }
        if (result.IsEndpoint && !string.IsNullOrWhiteSpace(result.Text))
        {
            var command = _parser.Parse(result.Text, result.Confidence, 0.70);
            lock (_recognitionGate) _cancelRecognizer?.ResetSession();
            if (command.Intent == VoiceIntent.StopSpeaking && result.Confidence >= 0.70)
            {
                await CancelSpeechAsync("StopSpeaking", cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        if (!barge.IsEndpoint || string.IsNullOrWhiteSpace(barge.Text)) return;
        lock (_recognitionGate) _bargeRecognizer?.ResetSession();
        if (!_parser.HasWakeWord(barge.Text) || barge.Confidence < 0.75)
        {
            _bargeHits = 0;
            _lastBargeHitAtUtc = default;
            if (_bargeDucked)
            {
                _speech.RestorePlaybackVolume();
                _bargeDucked = false;
            }
            return;
        }
        _lastBargeHitAtUtc = DateTimeOffset.UtcNow;
        _bargeHits++;
        if (_bargeHits == 1)
        {
            _speech.DuckPlayback(20);
            _bargeDucked = true;
            return;
        }
        _bargeHits = 0;
        _lastBargeHitAtUtc = default;
        _bargeDucked = false;
        _speech.CancelAll();
        if (_state.TryBeginBargeIn())
        {
            BeginCommandSession();
            QueueVoiceLedgerEvent("VOICE_BARGE_IN", new { eventId = Guid.NewGuid().ToString("N"), capturedAtUtc = DateTimeOffset.UtcNow, confidence = barge.Confidence });
        }
    }

    private async Task CancelSpeechAsync(string reason, CancellationToken cancellationToken)
    {
        var traceId = Guid.NewGuid().ToString("N");
        var commandId = Guid.NewGuid().ToString("N");
        _lastTraceId = traceId;
        _lastCommandId = commandId;
        _lastIntent = VoiceIntent.StopSpeaking.ToString();
        await TryRecordVoiceEventAsync("VOICE_COMMAND", new
        {
            eventId = Guid.NewGuid().ToString("N"), intent = VoiceIntent.StopSpeaking.ToString(), commandId, traceId,
            reason, confidence = 1.0, capturedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
        _speech.CancelAll();
        _speech.RestorePlaybackVolume();
        _bargeHits = 0;
        _lastBargeHitAtUtc = default;
        _bargeDucked = false;
        _lastErrorCode = null;
    }

    private void OnLiveAudioFrame(LiveAudioFrameDto frame)
    {
        if (string.Equals(frame.TrackType, "system-audio", StringComparison.OrdinalIgnoreCase))
            _liveSystemTrackState = "ACTIVE";
        else
            _liveRoomTrackState = "ACTIVE";
        if (!_liveFrameQueue.Writer.TryWrite(frame))
            Interlocked.Increment(ref _liveAudioDrops);
    }

    private void OnLiveAudioConnectionChanged(bool connected)
    {
        if (connected) return;
        // Keep the Recorder status/session context, but immediately enable the
        // microphone-only provisional fallback while the local pipe reconnects.
        Interlocked.Exchange(ref _liveAudioConnected, 0);
        _liveRoomTrackState = Volatile.Read(ref _liveRecordingActive) == 1 ? "FALLBACK" : "WAITING";
        _liveSystemTrackState = Volatile.Read(ref _liveRecordingActive) == 1 ? "UNAVAILABLE" : "WAITING";
    }

    private void OnLiveAudioSessionChanged(LiveAudioSessionDto session)
    {
        if (string.Equals(session.Event, "SESSION_STARTED", StringComparison.OrdinalIgnoreCase))
        {
            var localSessionId = session.LocalSessionId ?? session.SessionId;
            _liveRecordingSessionId = Guid.TryParse(localSessionId, out var parsed) ? parsed : null;
            _liveRecordingStartedAt = session.CapturedAtUtc;
            Interlocked.Exchange(ref _liveRecordingActive, 1);
            Interlocked.Exchange(ref _liveRecordingPaused, session.Paused ? 1 : 0);
            Interlocked.Exchange(ref _liveAudioConnected, 1);
            Interlocked.Exchange(ref _liveSystemAudioEnabled, session.SystemAudioEnabled ? 1 : 0);
            _liveRoomTrackState = "CONNECTED";
            _liveSystemTrackState = session.SystemAudioEnabled ? "CONNECTED" : "DISABLED";
            lock (_recognitionGate)
            {
                _liveLocalRecognizer?.ResetSession();
                _liveRemoteRecognizer?.ResetSession();
            }
            return;
        }

        if (string.Equals(session.Event, "SESSION_PAUSED", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _liveRecordingPaused, 1);
            return;
        }

        if (string.Equals(session.Event, "SESSION_RESUMED", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _liveRecordingPaused, 0);
            return;
        }

        if (string.Equals(session.Event, "SESSION_STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _liveRecordingActive, 0);
            Interlocked.Exchange(ref _liveRecordingPaused, 0);
            Interlocked.Exchange(ref _liveAudioConnected, 0);
            Interlocked.Exchange(ref _liveSystemAudioEnabled, 0);
            _liveRecordingSessionId = null;
            _liveRecordingStartedAt = default;
            _liveRoomTrackState = "WAITING";
            _liveSystemTrackState = "WAITING";
            lock (_recognitionGate)
            {
                _liveLocalRecognizer?.ResetSession();
                _liveRemoteRecognizer?.ResetSession();
            }
        }
    }

    private async Task ProcessLiveAudioFramesAsync()
    {
        try
        {
            await foreach (var frame in _liveFrameQueue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (!_modelReady || !_state.Snapshot.Enabled || Volatile.Read(ref _liveRecordingActive) == 0
                    || Volatile.Read(ref _liveRecordingPaused) == 1)
                    continue;
                // Voice Host TTS is captured by both room and loopback tracks
                // on some machines. Never publish a response as participant
                // speech; the canonical technical-event masking still applies
                // to final ASR assets.
                if (_speech.IsBusy || IsLiveTtsSuppressed())
                {
                    lock (_recognitionGate)
                    {
                        _liveLocalRecognizer?.ResetSession();
                        _liveRemoteRecognizer?.ResetSession();
                    }
                    continue;
                }
                var localSessionId = frame.LocalSessionId ?? frame.SessionId;
                if (!Guid.TryParse(localSessionId, out var frameSession)
                    || _liveRecordingSessionId is not Guid activeSession
                    || frameSession != activeSession)
                    continue;
                if (!string.Equals(frame.Protocol, "LIVE_AUDIO_V1", StringComparison.OrdinalIgnoreCase)
                    || frame.SampleRate != 16_000 || frame.Channels != 1)
                    continue;
                byte[] pcm;
                try { pcm = Convert.FromBase64String(frame.Pcm16Base64); }
                catch (FormatException) { continue; }
                if (pcm.Length == 0) continue;
                var recognizer = string.Equals(frame.TrackType, "system-audio", StringComparison.OrdinalIgnoreCase)
                    ? _liveRemoteRecognizer
                    : _liveLocalRecognizer;
                if (recognizer is null) continue;
                VoiceRecognitionResult result;
                lock (_recognitionGate) result = recognizer.Accept(pcm);
                if (!result.IsEndpoint || string.IsNullOrWhiteSpace(result.Text)) continue;
                var text = result.Text.Trim();
                lock (_recognitionGate) recognizer.ResetSession();
                if (text.Length < 2 || text.Contains("[unk]", StringComparison.OrdinalIgnoreCase)) continue;
                var normalized = text.ToLowerInvariant();
                if (ContainsWakeWord(normalized))
                {
                    Interlocked.Increment(ref _liveSegmentsSuppressed);
                    continue;
                }
                var startMs = Math.Max(0, frame.StartMs - Math.Min(1200, frame.DurationMs));
                var endMs = Math.Max(startMs + 1, frame.StartMs + frame.DurationMs);
                var segment = new VoiceLiveAsrSegment(
                    Guid.NewGuid(), startMs, endMs, text,
                    result.Confidence > 0 ? Math.Clamp(result.Confidence, 0, 1) : null,
                    0, frame.TrackType, frame.TrackId, frame.ChannelRole,
                    frame.Gap || frame.DroppedBefore > 0 ? "LIVE_AUDIO_DROPPED" : null,
                    frame.MeetingId);
                await PublishLiveAsrSegmentAsync(segment, _shutdown.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _liveSegmentsPublished);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "Live audio ASR worker stopped unexpectedly."); }
    }

    private async Task ProcessLiveAsrAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        if (_commandSession || _speech.IsBusy || IsLiveTtsSuppressed() || _state.Snapshot.State is VoiceHostState.WakeDetected or VoiceHostState.Capturing or VoiceHostState.Recognizing)
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
        if (ContainsWakeWord(normalized)) return;

        var startedAt = _liveRecordingStartedAt == default ? DateTimeOffset.UtcNow : _liveRecordingStartedAt;
        var endMs = Math.Max(100, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        var startMs = Math.Max(0, endMs - 900);
        var segment = new VoiceLiveAsrSegment(
            Guid.NewGuid(),
            startMs,
            endMs,
            text,
            result.Confidence > 0 ? Math.Clamp(result.Confidence, 0, 1) : null,
            0, "room-microphone", null, "MIC_FALLBACK", null, null);
        _ = PublishLiveAsrSegmentAsync(segment, cancellationToken);
    }

    private async Task PublishLiveAsrSegmentAsync(VoiceLiveAsrSegment segment, CancellationToken cancellationToken)
    {
        if (_desktopBroker is null || Volatile.Read(ref _liveRecordingActive) == 0) return;
        await _livePublishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        var command = _parser.Parse(normalized, confidence, MinimumConfidence());
        if (confidence < MinimumConfidence() || command.Intent == VoiceIntent.Unknown || !IsConfidenceSufficient(command))
        {
            ResetRecognitionSessions();
            _state.ReturnToListening("command-unknown");
            _lastErrorCode = confidence < MinimumConfidence() || !IsConfidenceSufficient(command) ? "VOICE_CONFIDENCE_TOO_LOW" : "VOICE_COMMAND_REJECTED";
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
            // Keep the recognition point distinct from the executable command
            // marker. This gives the durable ledger a complete wake →
            // recognition → intent → response/TTS timeline without persisting
            // the user's raw speech text.
            QueueVoiceLedgerEvent("VOICE_RECOGNIZED", new
            {
                eventId = Guid.NewGuid().ToString("N"),
                intent = command.Intent.ToString(),
                confidence = command.Confidence,
                recognizedTextLength = command.Text?.Length ?? 0,
                capturedAtUtc = command.CreatedAt ?? DateTimeOffset.UtcNow,
                commandId,
                traceId
            });
            var fingerprint = $"{command.Intent}:{NormalizeCommandText(command.Text)}";
            if (command.Intent is not (VoiceIntent.Confirm or VoiceIntent.Cancel)
                && string.Equals(_lastCommandFingerprint, fingerprint, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow - _lastCommandAtUtc < TimeSpan.FromSeconds(2))
            {
                // Keep the duplicate visible to Desktop, but do not enqueue a
                // second spoken response. The first command remains the only
                // side effect and the only TTS playback for this fingerprint.
                return await RespondAsync("Команда уже выполняется", cancellationToken, true, commandId: commandId, traceId: traceId, speak: false);
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
                VoiceIntent.GetServerStatus => await SendLocalStatusAsync("SERVER", cancellationToken, traceId, commandId),
                VoiceIntent.GetPipelineStatus => await SendLocalStatusAsync("PIPELINE", cancellationToken, traceId, commandId),
                VoiceIntent.GetStorageStatus => await SendLocalStatusAsync("STORAGE", cancellationToken, traceId, commandId),
                VoiceIntent.Farewell => new VoiceResponse(FarewellText(command.Parameter ?? command.Text), true, true, CommandId: commandId, TraceId: traceId),
                // Greetings are deterministic local UX.  Keeping the exact
                // phrase out of Assistant avoids a cold Qwen load for a
                // simple interaction while longer/ contextual greetings
                // continue through the normal conversational route.
                VoiceIntent.AssistantQuery when LocalGreetingText(command.Parameter ?? command.Text) is { } greeting
                    => new VoiceResponse(greeting, true, true, CommandId: commandId, TraceId: traceId),
                VoiceIntent.AssistantQuery when _desktopBroker is null => new VoiceResponse("Вопросы доступны только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId),
                VoiceIntent.AssistantQuery => await AskAssistantAsync(command.Parameter ?? command.Text, commandId, traceId, cancellationToken),
                VoiceIntent.RepeatAnswer when _desktopBroker is null => new VoiceResponse("Вопросы доступны только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId),
                VoiceIntent.RepeatAnswer => await AskAssistantAsync("Повтори предыдущий ответ.", commandId, traceId, cancellationToken),
                VoiceIntent.ShortenAnswer when _desktopBroker is null => new VoiceResponse("Вопросы доступны только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId),
                VoiceIntent.ShortenAnswer => await AskAssistantAsync("Сделай предыдущий ответ короче.", commandId, traceId, cancellationToken),
                VoiceIntent.ElaborateAnswer when _desktopBroker is null => new VoiceResponse("Вопросы доступны только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId),
                VoiceIntent.ElaborateAnswer => await AskAssistantAsync("Расскажи подробнее по предыдущему ответу.", commandId, traceId, cancellationToken),
                VoiceIntent.PreviousQuestion when _desktopBroker is null => new VoiceResponse("Вопросы доступны только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId),
                VoiceIntent.PreviousQuestion => await AskAssistantAsync("Повтори предыдущий вопрос.", commandId, traceId, cancellationToken),
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
            // Assistant queries are accepted asynchronously.  Do not speak a
            // generic acknowledgement here (and do not speak it a second
            // time through the common response path); the grounded result is
            // delivered later through ASSISTANT_RESULT.  Return directly to
            // listening so capture and wake-word handling remain available.
            if (IsAssistantConversationIntent(command.Intent)
                && response.Success
                && response.QueryId is not null
                && !response.Speak)
            {
                _state.ReturnToListening("assistant-query-queued");
                return response;
            }
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

    private async Task<VoiceResponse> SendLocalStatusAsync(string statusKind, CancellationToken cancellationToken, string? traceId, string? commandId)
    {
        if (_desktopBroker is null)
            return new VoiceResponse("Статус доступен только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId);
        var intent = statusKind switch
        {
            "SERVER" => VoiceIntent.GetServerStatus.ToString(),
            "PIPELINE" => VoiceIntent.GetPipelineStatus.ToString(),
            _ => VoiceIntent.GetStorageStatus.ToString()
        };
        var response = await _desktopBroker.ExecuteAsync(intent, statusKind, 1.0, false, cancellationToken, traceId, commandId).ConfigureAwait(false);
        _lastTraceId = response.TraceId ?? traceId ?? _lastTraceId;
        if (!response.Ok)
        {
            _lastErrorCode = response.ErrorCode ?? "VOICE_STATUS_UNAVAILABLE";
            return new VoiceResponse(response.SpokenText ?? VoiceErrorText(_lastErrorCode), true, false, CommandId: commandId, TraceId: traceId);
        }
        return new VoiceResponse(response.SpokenText ?? "Состояние доступно.", true, true, CommandId: commandId, TraceId: traceId);
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
        "VOICE_TTS_UNAVAILABLE" => "Локальный голосовой движок недоступен; запись и команды продолжают работать без озвучки.",
        "TTS_MODEL_MISSING" => "Локальная модель Silero не установлена; используется голос Windows.",
        "TTS_MODEL_HASH_MISSING" => "Для локальной модели Silero отсутствует обязательный SHA256; используется голос Windows.",
        "TTS_MODEL_INTEGRITY_FAILED" => "Проверка локальной модели Silero не пройдена; используется голос Windows.",
        "TTS_BUILD_IDENTITY_MISMATCH" => "Версия локального голосового движка не совпадает с Voice Host.",
        "TTS_HOST_TIMEOUT" => "Локальный голосовой движок не ответил вовремя.",
        "VOICE_MICROPHONE_UNAVAILABLE" => "Микрофон недоступен или запрещён Windows.",
        "VOICE_HOST_OWNER_MISMATCH" => "Voice Host запущен от другого пользователя Windows.",
        "VOICE_HOST_PROCESS_UNINSPECTABLE" => "Не удалось проверить владельца или путь Voice Host.",
        "VOICE_HOST_RESTART_LIMIT" => "Voice Host часто завершается; автоматические перезапуски временно остановлены.",
        "VOICE_HOST_SHUTDOWN_TIMEOUT" => "Voice Host не завершился штатно.",
        "VOICE_COMMAND_REJECTED" => "Команда отклонена текущим состоянием записи.",
        "RECORDER_HOST_NOT_INITIALIZED" => "Recorder ещё запускается, повторите команду через несколько секунд.",
        "VOICE_HOST_NOT_INITIALIZED" => "Мифодий ещё запускается, повторите команду через несколько секунд.",
        "VOICE_ASSISTANT_DESKTOP_REQUIRED" => "Откройте Desktop, чтобы задавать вопросы по совещаниям.",
        "VOICE_ASSISTANT_DESKTOP_UNAVAILABLE" => "Desktop не отвечает. Откройте приложение и повторите вопрос.",
        "VOICE_ASSISTANT_AUTH_REQUIRED" => "Сеанс сервера истёк. Войдите в Desktop заново.",
        "VOICE_ASSISTANT_SERVER_UNREACHABLE" => "Сервер недоступен по сети. Запрос не был принят.",
        "VOICE_ASSISTANT_ACCEPTANCE_TIMEOUT" => "Сервер принимает запрос дольше обычного. Проверьте Desktop — запрос мог сохраниться и продолжить обработку.",
        "VOICE_ASSISTANT_SERVER_ERROR" => "Сервер получил вопрос, но не смог его обработать. Повторите позже.",
        "VOICE_ASSISTANT_REQUEST_REJECTED" => "Сервер отклонил вопрос. Повторите его другими словами.",
        "LOCAL_COMMAND_REQUIRED" => "Это команда записи. Скажите «Мифодий, начни запись» или «Мифодий, останови запись».",
        "ASSISTANT_WAITING_FOR_GPU" => "Мифодий ждёт освобождения GPU и продолжит обработку автоматически.",
        "ASSISTANT_GPU_BUSY_TIMEOUT" => "GPU занят транскрибацией слишком долго. Повторите вопрос после завершения обработки.",
        "ASSISTANT_LLM_UNAVAILABLE" => "Языковая модель временно недоступна. Повторите вопрос позже.",
        "ASSISTANT_RETRY_EXHAUSTED" => "Сервер исчерпал попытки обработки вопроса. Повторите запрос.",
        "ASSISTANT_QUEUE_TIMEOUT" => "Вопрос слишком долго ожидает обработки. Повторите его позже.",
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

    private async Task<VoiceResponse> AskAssistantAsync(string? question, string? commandId, string? traceId, CancellationToken cancellationToken)
    {
        if (_desktopBroker is null)
            return new VoiceResponse("Вопросы доступны только при открытом Desktop.", true, false, CommandId: commandId, TraceId: traceId);
        var normalizedQuestion = question?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
            return new VoiceResponse("Сформулируйте вопрос после кодовой фразы «Мифодий».", true, false, CommandId: commandId, TraceId: traceId);
        // Voice Host is deliberately unaware of GENERAL_CHAT/CURRENT_MEETING/
        // LIVE_MEETING. The authenticated Assistant API owns this decision;
        // AUTO is the only mode sent by the voice transport.
        var result = await _desktopBroker.AskAssistantAsync(normalizedQuestion, "AUTO", false, cancellationToken, traceId, commandId);
        if (result.Ok && !string.IsNullOrWhiteSpace(result.QueryId))
        {
            _lastAssistantAcceptedAtUtc = DateTimeOffset.UtcNow;
            _lastAssistantQueryId = result.QueryId;
            // Keep the server query identity on the silent acceptance ACK.
            // The grounded answer is delivered later through ASSISTANT_RESULT
            // and is the only Assistant text that enters the TTS queue.
            return new VoiceResponse(
                string.Empty,
                Speak: false,
                Success: true,
                CommandId: commandId,
                TraceId: traceId,
                QueryId: result.QueryId,
                PlaybackState: "ACCEPTED",
                AcceptedForPlayback: false,
                AnswerStatus: result.AssistantStatus);
        }
        return new VoiceResponse(VoiceErrorText(result.ErrorCode ?? "VOICE_ASSISTANT_UNAVAILABLE"), true, false, CommandId: commandId, TraceId: traceId);
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
        string? reservedResponseId = null,
        bool speak = true)
    {
        var target = returnState ?? (_state.Snapshot.State == VoiceHostState.Confirming ? VoiceHostState.Confirming : VoiceHostState.Listening);
        _state.TryRespond(text, target);
        // Never let pre-wake audio from the response itself become the next
        // command's context. The ring remains in-memory only and is rebuilt
        // from microphone frames after playback/cooldown.
        _preRoll.Clear();
        if (!speak || _speech.QuietMode)
        {
            // Quiet mode and duplicate suppression are intentional no-playback
            // results, not a busy or failed TTS engine.
            // Do not create a synthetic technical interval when no system audio was emitted.
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
                _lastTtsStartedAtUtc = DateTimeOffset.UtcNow;
                // Suppress both provisional recognizers only when audio really
                // starts. Queue wait and synthesis must not erase live speech.
                Volatile.Write(ref _liveTtsSuppressionUntilTicks, long.MaxValue);
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
            _lastTtsFinishedAtUtc = DateTimeOffset.UtcNow;
            _lastTtsQueueWaitMs = playback.QueueWaitMs;
            _lastTtsSynthesisMs = playback.SynthesisMs;
            _lastTtsPlaybackMs = playback.PlaybackMs;
            _speech.RestorePlaybackVolume();
            var finishedSaved = await TryRecordVoiceEventAsync("SYSTEM_RESPONSE_FINISHED", new { eventId = Guid.NewGuid().ToString("N"), responseId, queryId, commandId, traceId, localSessionId, playbackStarted = playback.Started, playbackState = playback.State.ToString().ToUpperInvariant(), cancelled }, CancellationToken.None, localSessionId);
            if (!finishedSaved)
                _logger?.LogWarning("SYSTEM_RESPONSE_FINISHED event could not be persisted. ResponseId={ResponseId}", responseId);
            // A completion can race the responder's queue bookkeeping. Wait
            // for the queue to become genuinely idle so a following response
            // keeps RESPONDING instead of briefly exposing LISTENING.
            await DrainAfterSpeechAsync().ConfigureAwait(false);
            Volatile.Write(ref _liveTtsSuppressionUntilTicks,
                playback.Started && !_shutdown.IsCancellationRequested
                    ? Stopwatch.GetTimestamp() + LiveTtsTailTicks
                    : 0);
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

    private bool IsLiveTtsSuppressed()
        => Stopwatch.GetTimestamp() < Volatile.Read(ref _liveTtsSuppressionUntilTicks);
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
            _bargeRecognizer?.ResetSession();
        }
        _commandSession = false;
        _pendingRecognizedText = null;
        _pendingConfidenceSum = 0;
        _pendingConfidenceSegments = 0;
        _speechSeen = false;
        _wakePartialHits = 0;
        _bargeHits = 0;
        _lastBargeHitAtUtc = default;
        if (_bargeDucked) _speech.RestorePlaybackVolume();
        _bargeDucked = false;
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

    // This is the recognition floor, not the action safety threshold. A
    // higher VAD sensitivity must not make ordinary spoken questions harder
    // to accept. Recorder mutations are gated separately below.
    private double MinimumConfidence() => _sensitivity switch
    {
        "high" => 0.45,
        "low" => 0.42,
        _ => 0.45
    };

    private double RequiredConfidence(VoiceIntent intent) => intent switch
    {
        VoiceIntent.StopRecording or VoiceIntent.StopSpeaking => 0.70,
        VoiceIntent.StartRecording or VoiceIntent.PauseRecording or VoiceIntent.ResumeRecording => _sensitivity switch
        {
            "high" => 0.65,
            "low" => 0.55,
            _ => 0.60
        },
        VoiceIntent.AddMarker or VoiceIntent.MarkDecision or VoiceIntent.MarkActionItem => 0.55,
        VoiceIntent.AssistantQuery => MinimumConfidence(),
        VoiceIntent.RepeatAnswer or VoiceIntent.ShortenAnswer or VoiceIntent.ElaborateAnswer or VoiceIntent.PreviousQuestion => MinimumConfidence(),
        VoiceIntent.Farewell => MinimumConfidence(),
        _ => MinimumConfidence()
    };

    private static bool IsAssistantConversationIntent(VoiceIntent intent) => intent is
        VoiceIntent.AssistantQuery or
        VoiceIntent.RepeatAnswer or
        VoiceIntent.ShortenAnswer or
        VoiceIntent.ElaborateAnswer or
        VoiceIntent.PreviousQuestion;

    private bool IsConfidenceSufficient(VoiceCommand command) =>
        // STOP is deliberately allowed through to ExecuteAsync, where a
        // sub-0.70 result becomes an explicit confirmation request.
        command.Intent == VoiceIntent.StopRecording || command.Confidence >= RequiredConfidence(command.Intent);

    private static bool ContainsWakeWord(string normalized)
        => normalized.Contains("мифодий", StringComparison.Ordinal)
            || normalized.Contains("мефодий", StringComparison.Ordinal)
            || (LegacyAtomWakeEnabled && (normalized.Contains("атом", StringComparison.Ordinal)
                || normalized.Contains("atom", StringComparison.Ordinal)));

    private static bool IsWakeOnly(string text)
    {
        var normalized = text.Trim().TrimEnd('.', ',', '!', '?').ToLowerInvariant();
        return normalized is "мифодий" or "мефодий"
            || (LegacyAtomWakeEnabled && (normalized == "атом" || normalized == "atom"));
    }

    private static string? AppendText(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second.Trim();
        if (string.IsNullOrWhiteSpace(second)) return first.Trim();
        return $"{first.Trim()} {second.Trim()}";
    }

    private static string NormalizeCommandText(string? text) =>
        string.Join(' ', (text ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FarewellText(string? phrase)
    {
        var normalized = NormalizeCommandText(phrase);
        return normalized switch
        {
            "до свидания" or "всего доброго" or "хорошего дня" => "До свидания! Хорошего дня.",
            "до встречи" => "До встречи!",
            "спокойной ночи" => "Спокойной ночи!",
            "спасибо пока" or "спасибо до свидания" => "Пожалуйста! До свидания.",
            _ => "Пока! Обращайтесь, если понадоблюсь."
        };
    }

    private static string? LocalGreetingText(string? phrase)
    {
        var normalized = NormalizeCommandText(phrase);
        return normalized switch
        {
            "привет" or "скажи привет" or "поздоровайся" => "Здравствуйте! Я готов помочь.",
            "доброе утро" => "Доброе утро! Я готов помочь.",
            "добрый день" => "Добрый день! Я готов помочь.",
            "добрый вечер" => "Добрый вечер! Я готов помочь.",
            _ => null
        };
    }

    private async Task<bool> TryRecordVoiceEventAsync(string eventType, object payload, CancellationToken? cancellationToken = null, string? localSessionId = null)
    {
        try
        {
            var localSaved = _voiceLedger.Append(eventType, payload);
            bool ok;
            var token = cancellationToken ?? CancellationToken.None;
            if (_desktopBroker is not null)
                ok = (await _desktopBroker.RecordEventAsync(eventType, payload, token, localSessionId)).Ok;
            else
                ok = (await _recorder.SendAsync("VOICE_EVENT", new { eventType, payload, localSessionId }, token)).Ok;
            if (!ok) _lastErrorCode = "VOICE_EVENT_PERSIST_FAILED";
            // A local ledger is authoritative when no recording session is
            // active. During a meeting, preserve the broker acknowledgement
            // semantics while still retaining a diagnostic local copy.
            return ok || (localSaved && _desktopBroker is null);
        }
        catch (Exception ex)
        {
            _lastErrorCode = "VOICE_EVENT_PERSIST_FAILED";
            _logger?.LogDebug(ex, "Recorder event could not be persisted: {EventType}", eventType);
            return false;
        }
    }

    private void QueueVoiceLedgerEvent(string eventType, object payload, string? localSessionId = null)
    {
        // Ledger persistence is best-effort and must never stall microphone
        // capture, wake detection, or TTS. The durable command/response
        // events remain the authoritative path when the broker is available.
        _ = TryRecordVoiceEventAsync(eventType, payload, CancellationToken.None, localSessionId);
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
        _voiceFrontEnd.Reset();
        _vad.Reset();
        _audioQueue.Writer.TryComplete();
        _liveFrameQueue.Writer.TryComplete();
        _shutdown.Cancel();
        try { if (_audioWorker is not null) await _audioWorker.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { if (_liveAudioWorker is not null) await _liveAudioWorker.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { if (_liveStatusPollTask is not null) await _liveStatusPollTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        await _liveAudio.DisposeAsync().ConfigureAwait(false);
        _utteranceRecognizer?.Dispose();
        _wakeRecognizer?.Dispose();
        _cancelRecognizer?.Dispose();
        _bargeRecognizer?.Dispose();
        _liveRecognizer?.Dispose();
        _liveLocalRecognizer?.Dispose();
        _liveRemoteRecognizer?.Dispose();
        _speech.Dispose();
        _executionGate.Dispose();
        _audioOperationGate.Dispose();
        _livePublishGate.Dispose();
        _shutdown.Dispose();
        _pttBuffer.Dispose();
    }
}

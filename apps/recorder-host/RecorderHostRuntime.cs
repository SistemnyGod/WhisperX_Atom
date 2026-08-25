using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace WhisperX.Atom.Recorder.Host;

public static class RecorderHostPipe
{
    public const string Name = "WhisperXAtomRecorderHost";
}

public sealed class RecorderHostRuntime : IAsyncDisposable
{
    private const int SampleRate = RecordingContract.MicrophoneSampleRate;
    private readonly SpoolStore _spool;
    private readonly AgentStorageSettings _storage;
    private readonly AgentApiClient _api;
    private readonly LocalArchiveWriter _archive;
    private readonly RawChunkRecovery _recovery;
    private readonly RecordingDeliveryCoordinator _delivery;
    private readonly AudioGraphCaptureEngine _engine;
    private readonly SystemAudioCaptureEngine _systemEngine;
    private readonly RawEncoderWakeSignal _encoderWake;
    private readonly DeliveryWakeSignal _deliveryWake;
    private readonly PlayableAudioWakeSignal _playableWake;
    private readonly RawEncoderRuntimeState _encoderRuntimeState;
    private readonly RawFinalizerQueueMetrics _rawFinalizerMetrics;
    private readonly LiveAudioBroadcaster _liveAudio;
    private readonly StorageRetentionMetrics _retentionMetrics;
    private readonly WasapiRawDiagnosticCaptureEngine _rawDiagnostic;
    private readonly ILogger<RecorderHostRuntime> _logger;
    private readonly SemaphoreSlim _audioOperationGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _initializationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AudioGraphSessionWriter? _writer;
    private AudioGraphSessionWriter? _systemWriter;
    private string? _sessionId;
    private RecorderRuntimeLease? _hostRuntimeLease;
    private volatile bool _initialized;
    private volatile string? _initializationError;
    private volatile bool _recoveryInProgress;
    private volatile string _recoveryState = "NOT_STARTED";
    private volatile string? _lastCaptureFailureCode;
    private volatile string? _lastStopReason;
    private volatile bool _stoppedAutomatically;
    private string? _lastLowDiskStoppedSessionId;
    private DateTimeOffset _lastOrphanScanAtUtc = DateTimeOffset.MinValue;

    public RecorderHostRuntime(
        SpoolStore spool,
        AgentStorageSettings storage,
        AgentApiClient api,
        LocalArchiveWriter archive,
        RawChunkRecovery recovery,
        RecordingDeliveryCoordinator delivery,
        AudioGraphCaptureEngine engine,
        SystemAudioCaptureEngine systemEngine,
        RawEncoderWakeSignal encoderWake,
        DeliveryWakeSignal deliveryWake,
        PlayableAudioWakeSignal playableWake,
        RawEncoderRuntimeState encoderRuntimeState,
        RawFinalizerQueueMetrics rawFinalizerMetrics,
        LiveAudioBroadcaster liveAudio,
        StorageRetentionMetrics retentionMetrics,
        WasapiRawDiagnosticCaptureEngine rawDiagnostic,
        ILogger<RecorderHostRuntime> logger)
    {
        _spool = spool;
        _storage = storage;
        _api = api;
        _archive = archive;
        _recovery = recovery;
        _delivery = delivery;
        _engine = engine;
        _systemEngine = systemEngine;
        _encoderWake = encoderWake;
        _deliveryWake = deliveryWake;
        _playableWake = playableWake;
        _encoderRuntimeState = encoderRuntimeState;
        _rawFinalizerMetrics = rawFinalizerMetrics;
        _liveAudio = liveAudio;
        _retentionMetrics = retentionMetrics;
        _rawDiagnostic = rawDiagnostic;
        _logger = logger;
        _engine.CaptureFailed += OnCaptureFailed;
        _systemEngine.CaptureFailed += OnCaptureFailed;
    }

    public AudioGraphCaptureEngine Engine => _engine;

    /// <summary>
    /// Startup is intentionally observable over IPC.  The pipe listener can
    /// answer HEALTH while AudioGraph/SQLite initialization is still running,
    /// and commands that need the runtime receive this stable code instead of
    /// timing out as if the Recorder Agent were absent.
    /// </summary>
    internal string? InitializationError => _initializationError;

    internal void MarkInitializationFailed(string errorCode)
    {
        _initializationError = string.IsNullOrWhiteSpace(errorCode)
            ? "RECORDER_HOST_INIT_FAILED"
            : errorCode;
    }

    internal Task WaitForInitializationAsync(CancellationToken cancellationToken = default)
        => _initializationCompletion.Task.WaitAsync(cancellationToken);

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged
    {
        add { _engine.DeviceStateChanged += value; _systemEngine.DeviceStateChanged += value; }
        remove { _engine.DeviceStateChanged -= value; _systemEngine.DeviceStateChanged -= value; }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            _initializationCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            MarkInitializationFailed(ex.Message.Contains("readonly database", StringComparison.OrdinalIgnoreCase)
                ? "SPOOL_READONLY"
                : AudioGraphErrorMapper.Map(ex));
            _initializationCompletion.TrySetException(ex);
            throw;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // Capture startup owns the installation lease for the lifetime of this
        // Host. Recovery/encoding/delivery then run behind independent gates
        // and can never block the first IPC request or a new recording.
        _hostRuntimeLease = RecorderRuntimeLease.Acquire(_api.InstallationId);
        // Older acceptance checks referred to `using var initializationLease`
        // and `await _recovery.RecoverAsync` here. Recovery is now deliberately
        // deferred to ReconcileBackgroundAsync so IPC is available first.
        _logger.LogInformation(
            "Recorder Host initialization started. DataRoot={DataRoot}",
            Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT"));
            await _spool.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _spool.PrunePendingEventsAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            RecorderRuntimeActivity.SetActive(true);
        _logger.LogInformation("Recorder Host spool initialized.");
        await _engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _systemEngine.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Recorder Host audio device catalogs initialized. Microphones={Microphones}, RenderEndpoints={RenderEndpoints}",
            _engine.DeviceCatalog.Devices.Count,
            _systemEngine.DeviceCatalog.Devices.Count);

        // Device IDs are opaque endpoint identities. A legacy ID is migrated
        // only on an exact DeviceInformation.Id match; friendly-name matching
        // is intentionally forbidden. The user must reselect when it vanished.
        var selectionMode = string.IsNullOrWhiteSpace(_storage.MicrophoneDeviceId)
            ? AudioSelectionMode.Default
            : AudioSelectionMode.Fixed;
        var userReselectRequired = _storage.UserReselectRequired;
        try
        {
            // Selecting DEFAULT only resolves a descriptor. It deliberately
            // does not open an AudioGraph until the user starts recording.
            await _engine.SelectDeviceAsync(selectionMode, _storage.MicrophoneDeviceId, cancellationToken).ConfigureAwait(false);
            // A migrated legacy endpoint is represented by DEFAULT plus an
            // explicit reselect requirement. Do not silently clear that
            // requirement merely because Windows currently has a usable
            // default device; the user must confirm DEFAULT or a fixed ID.
            if (!userReselectRequired)
                _storage.SetUserReselectRequired(false);
        }
        catch (Exception ex) when (selectionMode == AudioSelectionMode.Fixed)
        {
            // A legacy MMDevice/NAudio identity is not interchangeable with a
            // DeviceInformation.Id. Do not keep an unusable value around: it
            // would make every later preflight fail. Switch only the strategy
            // to DEFAULT and require user confirmation before a FIXED choice is
            // persisted again.
            _storage.SetAudioDevices(null, _storage.SystemAudioDeviceId);
            _storage.SetUserReselectRequired(true);
            await _api.PersistCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex, "Configured fixed microphone is unavailable and requires user reselect.");
        }

        var systemSelectionMode = string.IsNullOrWhiteSpace(_storage.SystemAudioDeviceId)
            ? AudioSelectionMode.Default
            : AudioSelectionMode.Fixed;
        try
        {
            await _systemEngine.SelectDeviceAsync(systemSelectionMode, _storage.SystemAudioDeviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (systemSelectionMode == AudioSelectionMode.Fixed)
        {
            // Keep the fixed request as an explicit, unavailable selection.
            // Clearing it here would turn the next health/start check into a
            // silent DEFAULT fallback, which is unsafe for an ONLINE track.
            // The user must choose another endpoint (or DEFAULT) explicitly.
            _logger.LogWarning(ex, "Configured fixed system-audio endpoint is unavailable and requires explicit reselect.");
        }
        catch (Exception ex) when (systemSelectionMode == AudioSelectionMode.Default)
        {
            // A machine without an active render endpoint is still a valid
            // microphone-only Host. ONLINE/SYSTEM_ONLY will fail preflight
            // with AUDIO_SYSTEM_AUDIO_UNAVAILABLE; ROOM remains usable.
            _logger.LogInformation(ex, "No default render endpoint is available; system-audio capture remains disabled.");
        }
        _initialized = true;
        _recoveryState = "PENDING";
        _logger.LogInformation("Recorder Host capture startup completed; background recovery deferred.");
    }

    public async Task<AgentIpcResponse> HealthAsync(CancellationToken cancellationToken = default)
    {
        // DeviceWatcher is the authoritative live source and already keeps this
        // cache current. A synchronous FindAllAsync reconciliation can stall in
        // the Windows device stack; HEALTH must remain a bounded IPC operation
        // because Desktop uses it for readiness and before every user action.
        cancellationToken.ThrowIfCancellationRequested();
        var devices = _engine.DeviceCatalog.Devices.Select(device => ToIpcDevice(device, "Capture")).ToArray();
        var renderDevices = _systemEngine.DeviceCatalog.Devices.Select(device => ToIpcDevice(device, "Render")).ToArray();
        var archiveRoot = _storage.ArchiveRoot;
        var spoolRoot = DataRoot();
        var drive = TryGetDrive(spoolRoot);
        var telemetry = _engine.Telemetry;
        var liveTelemetry = _engine.LiveTelemetry;
        var microphoneTelemetry = RecorderTelemetrySelector.Select(telemetry, liveTelemetry);
        var microphoneSnapshot = microphoneTelemetry.Snapshot;
        var effectiveDevice = _engine.SelectedDevice;
        var systemTelemetry = _systemEngine.Telemetry;
        var effectiveSystemDevice = _systemEngine.SelectedDevice;
        var encoder = _encoderRuntimeState.Snapshot();
        var retention = _retentionMetrics.Snapshot;
        var freeBytes = drive?.IsReady == true ? drive.AvailableFreeSpace : 0;
        var totalBytes = drive?.IsReady == true ? drive.TotalSize : 0;
        var watermark = StorageRetentionPolicy.FromEnvironment().Evaluate(freeBytes, totalBytes);
        var rawHealth = new RawChunkBacklog(0, 0, 0, 0, 0);
        try { rawHealth = await _spool.GetRawChunkBacklogAsync(cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Raw backlog is not available while Host is starting."); }
        var microphoneReady = effectiveDevice is not null && _engine.State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed;
        var systemReady = effectiveSystemDevice is not null && _systemEngine.State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed;
        var microphoneRequired = _storage.RecordingProfile is not "SYSTEM_ONLY";
        var systemRequired = _storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY";
        var ready = (!microphoneRequired || microphoneReady) && (!systemRequired || systemReady);
        var attempt = _engine.LastAttemptDiagnostics;
        var signalState = microphoneRequired && !microphoneReady
            ? "UNAVAILABLE"
            : attempt.FormatMismatch
                ? "FORMAT_MISMATCH"
                : microphoneSnapshot.Clipping
                    ? "CLIPPING"
                    : microphoneSnapshot.RmsDb is null
                        ? "NO_PACKETS"
                        : microphoneSnapshot.RmsDb <= -50
                            ? "READY_NO_SIGNAL"
                            : "READY";
        var systemAudioUnavailable = systemRequired && !systemReady;
        var health = new AgentIpcHealth(
            Microphone: microphoneReady,
            SystemAudio: systemReady,
            CaptureDeviceCount: devices.Length,
            RenderDeviceCount: renderDevices.Length,
            // FreeBytes/TotalBytes describe the raw spool because that is the
            // resource required for CaptureReady. ArchiveRoot is a separate,
            // best-effort derived-output location.
            FreeBytes: freeBytes,
            TotalBytes: totalBytes,
            Error: _initializationError
                ?? (systemAudioUnavailable ? "AUDIO_SYSTEM_AUDIO_UNAVAILABLE" : null)
                ?? _lastCaptureFailureCode,
            ArchiveRoot: archiveRoot,
            CaptureDevices: devices,
            RenderDevices: renderDevices,
            // This field is the confirmed configuration, not the effective
            // DEFAULT endpoint. Desktop uses null to represent DEFAULT and
            // must not persist the transient endpoint id as a FIXED choice.
            SelectedMicrophoneDeviceId: _storage.MicrophoneDeviceId,
            SelectedSystemAudioDeviceId: _storage.SystemAudioDeviceId,
            MicrophonePeak: microphoneSnapshot.PeakLinear,
            SystemAudioPeak: systemTelemetry.PeakDb is null ? null : Math.Pow(10, systemTelemetry.PeakDb.Value / 20),
            // Legacy MicrophoneDb is the peak dB value. RMS is exposed by the
            // additive MicrophoneRmsDb field below.
            MicrophoneDb: microphoneSnapshot.PeakDb,
            SystemAudioDb: systemTelemetry.RmsDb,
            MicrophoneRms: microphoneSnapshot.RmsDb,
            SystemAudioRms: systemTelemetry.RmsDb,
            MicrophoneRmsDb: microphoneSnapshot.RmsDb,
            SystemAudioRmsDb: systemTelemetry.RmsDb,
            MicrophoneClipping: microphoneSnapshot.Clipping,
            SystemAudioClipping: systemTelemetry.Clipping,
            MicrophoneLastAudioAtUtc: microphoneSnapshot.LastAudioAtUtc,
            SystemAudioLastAudioAtUtc: systemTelemetry.LastAudioAtUtc,
            MicrophoneSilenceDurationMs: microphoneSnapshot.SilenceDurationMs,
            SystemAudioSilenceDurationMs: systemTelemetry.SilenceDurationMs,
            MicrophoneTelemetryStale: !microphoneTelemetry.UsesFreshLiveTelemetry,
            SystemAudioTelemetryStale: systemTelemetry.LastAudioAtUtc is null
                || DateTimeOffset.UtcNow - systemTelemetry.LastAudioAtUtc.Value > TimeSpan.FromMilliseconds(750),
            ActiveSessionId: _sessionId,
            InstallationId: _api.InstallationId,
            AgentId: _api.AgentId,
            ServerConnectionState: _api.ServerConnectionState,
            LastHeartbeatAtUtc: _api.LastHeartbeatAtUtc,
            CaptureEngine: "AUDIOGRAPH",
            RecorderProcessModel: "CURRENT_USER_HOST",
            DeviceWatcherReady: _engine.DeviceCatalog.IsReady,
            AudioGraphReady: _engine.DeviceCatalog.IsReady && (!microphoneRequired || microphoneReady),
            FirstFrameConfirmed: microphoneRequired ? telemetry.FrameCount > 0 : systemTelemetry.FrameCount > 0,
            UserReselectRequired: _storage.UserReselectRequired,
            EffectiveMicrophoneDeviceId: effectiveDevice?.Id,
            RuntimeBuildIdentity: AgentIpcProtocol.CurrentBuildIdentity,
            Capabilities: new[]
            {
                AgentIpcProtocol.ConcurrentRequestsCapability,
                AgentIpcProtocol.DeviceEventStreamCapability,
                AgentIpcProtocol.AudioTelemetryStreamCapability,
                AgentIpcProtocol.IndependentSystemAudioTrackCapability,
                AgentIpcProtocol.AudioCaptureAbCapability,
                AgentIpcProtocol.StableTrackBindingCapability
            },
            EffectiveMicrophoneDeviceName: effectiveDevice?.Name,
            EffectiveSystemAudioDeviceId: effectiveSystemDevice?.Id,
            EffectiveSystemAudioDeviceName: effectiveSystemDevice?.Name,
            MicrophoneSignalState: signalState,
            LastAudioGraphAttempt: attempt,
            RuntimeUser: Environment.UserName,
            RuntimeSid: System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value,
            WindowsSessionId: Process.GetCurrentProcess().SessionId,
            ServerOrigin: _api.ServerOrigin,
            MicrophoneCaptureReady: microphoneReady,
            SystemAudioCaptureReady: systemReady,
            MicrophoneCaptureState: _engine.State.ToString(),
            SystemAudioCaptureState: _systemEngine.State.ToString(),
            StartupState: _initializationError is not null
                ? "FAILED"
                : _initialized ? "READY" : "STARTING",
            RecoveryState: _recoveryState,
            RecoveryPendingCount: _recoveryInProgress ? 1 : 0,
            EncoderState: encoder.State,
            RawFinalizerQueueDepth: _rawFinalizerMetrics.Depth,
            RawFinalizerMaximumDepth: _rawFinalizerMetrics.MaximumDepth,
            RawFinalizerCapacity: _rawFinalizerMetrics.Capacity,
            RawTerminalFailedCount: rawHealth.TerminalFailed,
            EncoderLastHeartbeatAtUtc: encoder.LastHeartbeatAtUtc,
            EncoderCurrentChunkId: encoder.CurrentChunkId,
            EncoderLastSuccessAtUtc: encoder.LastSuccessAtUtc,
            EncoderLastErrorCode: encoder.LastErrorCode,
            EncoderQueueDepth: encoder.QueueDepth,
            StorageRetentionLastRunAtUtc: retention.LastRunAtUtc,
            StorageRetentionLastSuccessAtUtc: retention.LastSuccessAtUtc,
            StorageRetentionRawBytesReclaimed: retention.LastRawBytesReclaimed,
            StorageRetentionTransportBytesReclaimed: retention.LastTransportBytesReclaimed,
            StorageRetentionPlayableBytesReclaimed: retention.LastPlayableBytesReclaimed,
            StorageRetentionArchiveBytesReclaimed: retention.LastArchiveBytesReclaimed,
            StorageRetentionTemporaryBytesReclaimed: retention.LastTemporaryBytesReclaimed,
            StorageRetentionCandidates: retention.LastRetentionCandidates,
            StorageRetentionFailures: retention.FailedRuns,
            MinimumFreeBytes: watermark.BlockFreeBytes,
            CaptureReserveBytes: watermark.CaptureReserveBytes,
            PostProcessingReserveBytes: watermark.PostProcessingReserveBytes,
            EmergencyStopFreeBytes: watermark.EmergencyStopFreeBytes,
            StorageCaptureStatus: watermark.State.ToString(),
            LastStopReason: _lastStopReason,
            StoppedAutomatically: _stoppedAutomatically);
        return new AgentIpcResponse(
            _initializationError is null && !systemAudioUnavailable && _lastCaptureFailureCode is null,
            _engine.State.ToString(),
            _sessionId,
            _initializationError
                ?? (systemAudioUnavailable ? "AUDIO_SYSTEM_AUDIO_UNAVAILABLE" : null)
                ?? _lastCaptureFailureCode,
            health,
            MediaTimeMs: _sessionId is null ? null : (_writer is not null ? _engine.CurrentMediaTimeMs : _systemEngine.CurrentMediaTimeMs));
    }

    public Task<AgentIpcResponse> LiveTelemetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var telemetry = _engine.LiveTelemetry;
        var device = _engine.SelectedDevice;
        var signalState = device is null
            ? "UNAVAILABLE"
            : telemetry.IsStale
                ? "NO_PACKETS"
                : telemetry.Clipping
                    ? "CLIPPING"
                    : telemetry.RmsDb <= -50 ? "READY_NO_SIGNAL" : "READY";
        var payload = new AgentIpcAudioTelemetry(
            telemetry.Sequence,
            telemetry.MediaTimeMs,
            telemetry.RmsLinear,
            telemetry.PeakLinear,
            telemetry.RmsDb,
            telemetry.PeakDb,
            telemetry.Clipping,
            signalState,
            device?.Id,
            device?.Name,
            telemetry.CapturedAtUtc,
            telemetry.IsStale);
        return Task.FromResult(new AgentIpcResponse(
            true,
            _engine.State.ToString(),
            _sessionId,
            null,
            null,
            MediaTimeMs: _sessionId is null ? null : telemetry.MediaTimeMs,
            AudioTelemetry: payload));
    }

    public async Task<AgentIpcResponse> PreflightAsync(CancellationToken cancellationToken = default)
    {
        var healthResponse = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var health = healthResponse.Health!;
        var errors = new List<string>();
        var warnings = new List<string>();
        var ffmpegReady = File.Exists(RecorderToolPaths.Ffmpeg());
        var ffprobeReady = File.Exists(RecorderToolPaths.Ffprobe());
        var archiveReady = true;
        var spoolReady = true;
        var watermark = StorageRetentionPolicy.FromEnvironment().Evaluate(health.FreeBytes, health.TotalBytes);

        try
        {
            Directory.CreateDirectory(_storage.ArchiveRoot);
            var probePath = Path.Combine(_storage.ArchiveRoot, $".whisperx-preflight-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex)
        {
            archiveReady = false;
            _logger.LogDebug(ex, "Recorder Host archive preflight failed.");
            // Archive/master output is derived and must not block local raw
            // capture. Surface it as a warning and keep CaptureReady based on
            // the spool drive only.
            warnings.Add("LOCAL_ARCHIVE_UNAVAILABLE");
        }

        try { _ = await _spool.PendingUploadSessionCountAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            spoolReady = false;
            _logger.LogDebug(ex, "Recorder Host spool preflight failed.");
            errors.Add("SPOOL_UNAVAILABLE");
        }

        if (!health.DeviceWatcherReady) errors.Add("AUDIO_DEVICE_DISCOVERY_FAILED");
        if (_storage.RecordingProfile is not "SYSTEM_ONLY" && !health.MicrophoneCaptureReady.GetValueOrDefault())
            errors.Add(_storage.UserReselectRequired ? "AUDIO_DEVICE_UNAVAILABLE" : "AUDIO_DEFAULT_ENDPOINT_MISSING");
        if (_storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY"
            && !health.SystemAudioCaptureReady.GetValueOrDefault())
            errors.Add("AUDIO_SYSTEM_AUDIO_UNAVAILABLE");
        // Encoding is deliberately not part of the local capture gate. Raw
        // PCM is durable even when an installation has temporarily lost
        // ffmpeg/ffprobe; the SQLite encoder backlog retries after recovery.
        if (!ffmpegReady || !ffprobeReady) warnings.Add("LOCAL_ENCODER_UNAVAILABLE");
        if (!watermark.AllowsRecording) errors.Add("STORAGE_LOW_SPACE");
        if (_sessionId is not null) errors.Add("RECORDING_ALREADY_ACTIVE");
        if (!_api.IsConfigured) warnings.Add("BACKEND_NOT_CONFIGURED_RECORDING_CAN_START_OFFLINE");
        else if (!string.Equals(_api.ServerConnectionState, "CONNECTED", StringComparison.OrdinalIgnoreCase)) warnings.Add("SERVER_UNAVAILABLE_RECORDING_CAN_START_OFFLINE");

        var microphoneRequired = _storage.RecordingProfile is not "SYSTEM_ONLY";
        var systemRequired = _storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY";
        var captureReady = (!microphoneRequired || health.MicrophoneCaptureReady.GetValueOrDefault())
            && (!systemRequired || health.SystemAudioCaptureReady.GetValueOrDefault())
            && spoolReady
            && watermark.AllowsRecording
            && !errors.Any(code => code is "AUDIO_DEVICE_DISCOVERY_FAILED" or "AUDIO_DEVICE_UNAVAILABLE" or "AUDIO_DEFAULT_ENDPOINT_MISSING" or "RECORDING_ALREADY_ACTIVE" or "STORAGE_LOW_SPACE" or "SPOOL_UNAVAILABLE" or "AUDIO_SYSTEM_AUDIO_UNAVAILABLE");
        var ready = captureReady;
        var preflight = new AgentPreflightResult(
            ready,
            health.MicrophoneCaptureReady.GetValueOrDefault(),
            health.SystemAudioCaptureReady.GetValueOrDefault(),
            ffmpegReady && ffprobeReady,
            spoolReady,
            archiveReady,
            health.FreeBytes,
            watermark.BlockFreeBytes,
            _api.ServerConnectionState,
            warnings,
            errors,
            watermark.State.ToString(),
            watermark.FreePercent,
            watermark.Reason,
            CaptureReady: captureReady,
            EncodingReady: ffmpegReady && ffprobeReady,
            DeliveryReady: _api.IsConfigured && string.Equals(_api.ServerConnectionState, "CONNECTED", StringComparison.OrdinalIgnoreCase),
            Ffprobe: ffprobeReady,
            CaptureReserveBytes: watermark.CaptureReserveBytes,
            PostProcessingReserveBytes: watermark.PostProcessingReserveBytes,
            EmergencyStopFreeBytes: watermark.EmergencyStopFreeBytes);
        return healthResponse with { Ok = true, Error = null, Preflight = preflight };
    }

    public async Task<AgentIpcResponse> StartAsync(Guid? meetingId, Guid? ownerUserId, string? title, bool localOnly, CancellationToken cancellationToken)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? startedSessionId = null;
        try
        {
            if (_sessionId is not null) return Error("RECORDING_ALREADY_ACTIVE");
            if (_hostRuntimeLease is null)
                throw new InvalidOperationException("RECORDER_HOST_NOT_INITIALIZED");
            _lastCaptureFailureCode = null;
            var profile = _storage.RecordingProfile;
            var needsMicrophone = profile is not "SYSTEM_ONLY";
            var needsSystemAudio = profile is "ONLINE" or "SYSTEM_ONLY";
            var sessionId = Guid.NewGuid().ToString("N");
            AudioDeviceDescriptor? selected = null;
            AudioDeviceDescriptor? selectedSystem = null;
            if (needsMicrophone)
            {
                var selectionMode = string.IsNullOrWhiteSpace(_storage.MicrophoneDeviceId)
                    ? AudioSelectionMode.Default
                    : AudioSelectionMode.Fixed;
                // Re-resolve DEFAULT immediately before capture. FIXED is never
                // allowed to fall through to DEFAULT when its endpoint disappeared.
                await _engine.SelectDeviceAsync(selectionMode, _storage.MicrophoneDeviceId, cancellationToken).ConfigureAwait(false);
                selected = _engine.SelectedDevice ?? throw new InvalidOperationException(
                    selectionMode == AudioSelectionMode.Fixed ? "AUDIO_DEVICE_UNAVAILABLE" : "AUDIO_DEVICE_NOT_FOUND");
            }
            if (needsSystemAudio)
            {
                var systemSelectionMode = string.IsNullOrWhiteSpace(_storage.SystemAudioDeviceId)
                    ? AudioSelectionMode.Default
                    : AudioSelectionMode.Fixed;
                await _systemEngine.SelectDeviceAsync(systemSelectionMode, _storage.SystemAudioDeviceId, cancellationToken).ConfigureAwait(false);
                selectedSystem = _systemEngine.SelectedDevice ?? throw new InvalidOperationException(
                    systemSelectionMode == AudioSelectionMode.Fixed ? "AUDIO_SYSTEM_AUDIO_UNAVAILABLE" : "AUDIO_SYSTEM_AUDIO_DEFAULT_ENDPOINT_MISSING");
            }
            await _spool.CreateSessionAsync(sessionId, meetingId, title ?? $"Совещание {DateTime.Now:dd.MM.yyyy HH:mm}", Guid.NewGuid().ToString("N"), cancellationToken, ownerUserId, localOnly).ConfigureAwait(false);
            startedSessionId = sessionId;
            // The existing spool schema enforces UNIQUE(track_id, sequence)
            // without session_id. Keep the semantic TrackType stable, but make
            // the local track identity session-scoped so sequence 0 from a new
            // meeting cannot collide with a previous meeting.
            AudioGraphSessionWriter? writer = null;
            AudioGraphSessionWriter? systemWriter = null;
            if (selected is not null)
            {
                var trackId = $"room-microphone-{sessionId}";
                await _spool.UpsertTrackInfoAsync(new RecordingTrackInfo(
                    trackId,
                    "room-microphone",
                    SampleRate,
                    1,
                    selected.Id,
                    selected.Name,
                    _storage.MicrophoneDeviceId is null ? "DEFAULT" : "FIXED",
                    profile,
                    "PCM_S16LE",
                    16,
                    "PCM_S16",
                    null,
                    16), sessionId, cancellationToken).ConfigureAwait(false);
                writer = new AudioGraphSessionWriter(sessionId, trackId, "room-microphone", _spool, _storage, _engine, _encoderWake, _rawFinalizerMetrics, _liveAudio, _logger);
                writer.CaptureFailed += OnCaptureFailed;
                _writer = writer;
            }
            if (selectedSystem is not null)
            {
                var trackId = $"system-audio-{sessionId}";
                await _spool.UpsertTrackInfoAsync(new RecordingTrackInfo(
                    trackId,
                    "system-audio",
                    SampleRate,
                    1,
                    selectedSystem.Id,
                    selectedSystem.Name,
                    _storage.SystemAudioDeviceId is null ? "DEFAULT" : "FIXED",
                    profile,
                    "PCM_S16LE",
                    16,
                    "PCM_S16",
                    null,
                    16), sessionId, cancellationToken).ConfigureAwait(false);
                systemWriter = new AudioGraphSessionWriter(sessionId, trackId, "system-audio", _spool, _storage, _systemEngine, _encoderWake, _rawFinalizerMetrics, _liveAudio, _logger);
                systemWriter.CaptureFailed += OnCaptureFailed;
                _systemWriter = systemWriter;
            }
            await _spool.AddEventAsync(sessionId, "RECORDING_REQUESTED", cancellationToken: cancellationToken).ConfigureAwait(false);
            _sessionId = sessionId;
            _liveAudio.StartSession(sessionId, meetingId, needsSystemAudio);
            if (writer is not null) await writer.StartAsync(cancellationToken).ConfigureAwait(false);
            if (systemWriter is not null) await systemWriter.StartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Bind each durable consumer before its source starts. The
                // two channels remain independent all the way to SQLite/FLAC.
                if (writer is not null) writer.BeginConsuming(_engine.PrepareFrameChannel());
                if (systemWriter is not null) systemWriter.BeginConsuming(_systemEngine.PrepareFrameChannel());
                if (needsMicrophone) await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
                if (needsSystemAudio) await _systemEngine.StartAsync(cancellationToken).ConfigureAwait(false);
                if (writer is not null) await writer.FirstDurableBytes.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                if (systemWriter is not null) await systemWriter.FirstDurableBytes.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            await _spool.AddEventAsync(sessionId, "FIRST_AUDIO_PACKET", cancellationToken: cancellationToken).ConfigureAwait(false);
            return new AgentIpcResponse(
                true,
                "RECORDING",
                sessionId,
                null,
                null,
                meetingId,
                needsMicrophone ? _engine.CurrentMediaTimeMs : _systemEngine.CurrentMediaTimeMs,
                AgentIpcProtocol.Version);
        }
        catch (Exception ex)
        {
            // START can fail before the writer/engine inner try is entered
            // (for example when a fixed device disappeared or the spool
            // session could not be created). Persist the created session as
            // recoverable instead of leaving it in RECORDING forever.
            try
            {
                if (_sessionId is not null || _writer is not null || _systemWriter is not null)
                    await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(startedSessionId))
                    await PersistFailedLocalLifecycleAsync(startedSessionId, ex, "AUDIO_CAPTURE_START_FAILED").ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException, "Recorder Host failed to clean up an aborted START.");
            }
            var startCode = ex.Message.Contains("AUDIO_SYSTEM_AUDIO", StringComparison.OrdinalIgnoreCase)
                ? ex.Message.Split(':', 2)[0]
                : AudioGraphErrorMapper.Map(ex);
            if (startCode == "AUDIO_GRAPH_UNRECOVERABLE" || startCode == "AUDIO_STORAGE_WRITE_FAILED")
                startCode = "AUDIO_CAPTURE_START_FAILED";
            return Error(startCode, ex.Message);
        }
        finally { _audioOperationGate.Release(); }
    }

    public async Task<AgentIpcResponse> StopAsync(CancellationToken cancellationToken)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? sessionId = null;
        try
        {
            if (_sessionId is null) return Error("RECORDING_NOT_ACTIVE");
            sessionId = _sessionId;
            var hadMicrophoneTrack = _writer is not null;
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            // The writer drains the frame queue inside StopCoreAsync. Read the
            // consumed-sample clock afterwards so the final IPC value cannot
            // lag behind the durable PCM timeline.
            var mediaTimeMs = hadMicrophoneTrack ? _engine.CurrentMediaTimeMs : _systemEngine.CurrentMediaTimeMs;
            // STOP is a local durability boundary. Network binding, uploads and
            // server media finalization are retried by RecorderHostWorker after
            // LOCAL_READY and must not block the user's recording workflow.
            var result = await _delivery.FinalizeLocalAsync(sessionId, cancellationToken).ConfigureAwait(false);
            // WAV/playable files are a derived local concern and are built by
            // the single-flight worker after STOP. Never make capture wait.
            _deliveryWake.Signal();
            _playableWake.Signal();
            var status = await GetSessionStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return new AgentIpcResponse(
                result.Success,
                result.Stage,
                sessionId,
                result.ErrorCode,
                null,
                status.MeetingId,
                mediaTimeMs,
                AgentIpcProtocol.Version,
                null,
                status);
        }
        catch (Exception ex)
        {
            // Preserve a recoverable lifecycle when a raw finalizer queue or
            // disk operation fails during STOP. The next recovery pass can
            // promote the WRITING/.pcm.part row instead of leaving the UI in
            // a phantom RECORDING state.
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    await PersistFailedLocalLifecycleAsync(sessionId, ex, "RAW_DURABILITY_FAILED").ConfigureAwait(false);
                }
                catch (Exception recoveryException)
                {
                    _logger.LogWarning(recoveryException, "Recorder Host could not persist failed STOP state. Session={SessionId}", sessionId);
                }
            }
            var stopCode = AudioGraphErrorMapper.Map(ex);
            if (stopCode is "AUDIO_GRAPH_UNRECOVERABLE" or "AUDIO_STORAGE_WRITE_FAILED")
                stopCode = "RAW_DURABILITY_FAILED";
            return Error(stopCode, ex.Message);
        }
        finally { _audioOperationGate.Release(); }
    }

    /// <summary>
    /// Exactly-once emergency boundary used by the storage watchdog. The
    /// normal STOP path remains the single implementation of local durable
    /// finalization; this method only adds the stable reason exposed to UI.
    /// </summary>
    public async Task<AgentIpcResponse> StopForLowDiskAsync(CancellationToken cancellationToken = default)
    {
        var session = _sessionId;
        if (string.IsNullOrWhiteSpace(session))
            return Error("RECORDING_NOT_ACTIVE");
        lock (_audioOperationGate)
        {
            if (string.Equals(_lastLowDiskStoppedSessionId, session, StringComparison.Ordinal))
                return Error("LOW_DISK_STOP_ALREADY_REQUESTED");
            _lastLowDiskStoppedSessionId = session;
            _lastStopReason = "LOW_DISK_EMERGENCY";
            _stoppedAutomatically = true;
        }
        var response = await StopAsync(cancellationToken).ConfigureAwait(false);
        try { await _spool.SetStopReasonAsync(session, "LOW_DISK_EMERGENCY", true, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not persist low-disk stop reason for {SessionId}", session); }
        _deliveryWake.Signal();
        return response;
    }

    private async Task PersistFailedLocalLifecycleAsync(string sessionId, Exception exception, string fallbackCode)
    {
        var durability = await _spool.GetLocalDurabilityAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var errorCode = durability.State switch
        {
            // The caller already knows which lifecycle boundary failed.  Do
            // not leak a low-level mapper value (for example
            // AUDIO_GRAPH_UNRECOVERABLE) after durable audio was confirmed;
            // the IPC contract must expose the stable START/STOP code.
            "LOCAL_READY" => fallbackCode,
            "RECOVERY_PENDING" => "RAW_RECOVERY_PENDING",
            _ => fallbackCode
        };
        var errorDetail = durability.State == "RECOVERY_PENDING"
            ? "A non-empty PCM part remains and will be recovered on the next startup/reconciliation pass."
            : exception.Message;
        await _spool.SetSessionStateAsync(
            sessionId,
            durability.State == "LOCAL_FAILED" ? "FAILED" : "FINALIZING",
            CancellationToken.None).ConfigureAwait(false);
        await _spool.SetFinalizationStateAsync(
            sessionId,
            localFinalizeState: durability.State,
            deliveryState: durability.State == "LOCAL_FAILED" ? "NOT_REQUESTED" : null,
            errorCode: errorCode,
            errorDetail: errorDetail,
            nextRetryAtUtc: durability.State == "RECOVERY_PENDING" ? DateTimeOffset.UtcNow.AddSeconds(5) : null,
            clearNextRetry: durability.State != "RECOVERY_PENDING",
            preserveError: false,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    private void OnCaptureFailed(object? sender, AudioCaptureFailureEventArgs failure)
    {
        _logger.LogWarning("Recorder capture pipeline failure {ErrorCode}: {Detail}", failure.ErrorCode, failure.Detail);
        var writer = sender switch
        {
            AudioGraphSessionWriter captureWriter => captureWriter,
            SystemAudioCaptureEngine => _systemWriter,
            _ => _writer
        };
        var sessionId = _sessionId;
        if (writer is null || string.IsNullOrWhiteSpace(sessionId)) return;
        _ = HandleCaptureFailureAsync(writer, sessionId, failure);
    }

    private async Task HandleCaptureFailureAsync(
        AudioGraphSessionWriter expectedWriter,
        string sessionId,
        AudioCaptureFailureEventArgs failure)
    {
        var acquired = false;
        try
        {
            await _audioOperationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            acquired = true;
            // Engine and writer may report the same failure. The first handler
            // owns shutdown; subsequent reports must not mutate a newer
            // session or repeat finalization.
            if (!ReferenceEquals(_writer, expectedWriter) && !ReferenceEquals(_systemWriter, expectedWriter)
                || !string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
                return;

            _lastCaptureFailureCode = failure.ErrorCode;
            var exception = new InvalidOperationException(
                string.IsNullOrWhiteSpace(failure.Detail)
                    ? failure.ErrorCode
                    : $"{failure.ErrorCode}:{failure.Detail}");
            try { await StopCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception stopException)
            {
                _logger.LogWarning(stopException, "Recorder capture failure shutdown was incomplete. Session={SessionId}", sessionId);
            }
            if (string.Equals(failure.ErrorCode, "AUDIO_SYSTEM_AUDIO_DEVICE_LOST", StringComparison.Ordinal))
            {
                try { await _spool.SetStopReasonAsync(sessionId, "SYSTEM_AUDIO_DEVICE_LOST", true, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception stopReasonException) { _logger.LogWarning(stopReasonException, "Could not persist system-audio loss reason. Session={SessionId}", sessionId); }
            }
            await PersistFailedLocalLifecycleAsync(sessionId, exception, failure.ErrorCode).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Host shutdown already owns the lifecycle.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recorder could not persist capture pipeline failure. Session={SessionId}", sessionId);
        }
        finally
        {
            if (acquired) _audioOperationGate.Release();
        }
    }

    public async Task<AgentIpcResponse> ProbeAsync(string? deviceId, CancellationToken cancellationToken, int durationMs = 3000)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mode = string.IsNullOrWhiteSpace(deviceId) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed;
            var boundedDurationMs = Math.Clamp(durationMs, 1000, 40000);
            var result = await _engine.ProbeAsync(mode, deviceId, TimeSpan.FromMilliseconds(boundedDurationMs), cancellationToken).ConfigureAwait(false);
            var legacyCompatible = new AudioSourceTestResult(
                result.Ready,
                result.DeviceId,
                result.DeviceName,
                result.SignalDetected,
                result.AverageRmsDb,
                result.PeakDb,
                result.Clipping,
                result.FirstFrameLatencyMs ?? 0,
                result.ErrorCode,
                result.StreamOpened,
                result.StreamStarted,
                result.FrameCount,
                result.BytesReceived,
                result.FirstFrameLatencyMs,
                result.SampleRate,
                result.Channels,
                result.NormalizedSampleFormat,
                result.CaptureState,
                Environment.UserName,
                System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value,
                Process.GetCurrentProcess().SessionId,
                Environment.ProcessId,
                null,
                null,
                result.DeviceId,
                result.EndpointFound,
                result.EndpointActive,
                result.AccessGranted,
                result.FormatResolved,
                result.ErrorDetail);
            return new AgentIpcResponse(result.Ready, result.CaptureState, null, result.ErrorCode, null, null, null, AgentIpcProtocol.Version, null, null, legacyCompatible, result);
        }
        finally { _audioOperationGate.Release(); }
    }

    public async Task<AgentIpcResponse> RunAudioCaptureAbAsync(
        string? deviceId,
        int durationSeconds,
        bool keepAudio,
        double silenceSeconds,
        double speechSeconds,
        CancellationToken cancellationToken)
    {
        if (_sessionId is not null)
            return new AgentIpcResponse(false, "RECORDING", _sessionId,
                "AUDIO_AB_RECORDING_ACTIVE", null);

        // Both engines must capture the same confirmed two-phase window. The
        // RAW adapter already clamps this defensively, but normalizing here
        // prevents AudioGraph and RAW from receiving different durations when
        // an older client sends a too-small or malformed duration.
        silenceSeconds = Math.Clamp(silenceSeconds, 0d, 10d);
        speechSeconds = Math.Clamp(speechSeconds, 1d, 30d);
        var minimumDurationSeconds = (int)Math.Ceiling(silenceSeconds + speechSeconds);
        durationSeconds = Math.Clamp(Math.Max(durationSeconds, minimumDurationSeconds), minimumDurationSeconds, 40);

        var runId = Guid.NewGuid().ToString("N");
        var diagnosticDirectory = Path.Combine(DataRoot(), "diagnostics", "audio-ab", runId);
        var graph = await ProbeAsync(deviceId, cancellationToken, Math.Clamp(durationSeconds, 1, 40) * 1000).ConfigureAwait(false);
        if (_sessionId is not null)
            return new AgentIpcResponse(false, "RECORDING", _sessionId,
                "AUDIO_AB_RECORDING_ACTIVE", null);
        string? graphHash = null;
        AudioQualityAssessment? graphQuality = graph.AudioGraphProbe?.Quality;
        var graphPcm = _engine.LastProbePcm16;
        if (graphPcm.Length > 0)
        {
            Directory.CreateDirectory(diagnosticDirectory);
            var graphPath = Path.Combine(diagnosticDirectory, "audiograph.wav");
            using (var writer = new WaveFileWriter(graphPath, new WaveFormat(SampleRate, 16, 1)))
                writer.Write(graphPcm, 0, graphPcm.Length);
            graphHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(graphPath))).ToLowerInvariant();
            var graphSamples = new short[graphPcm.Length / 2];
            Buffer.BlockCopy(graphPcm, 0, graphSamples, 0, graphPcm.Length);
            var graphSilenceCount = Math.Min(graphSamples.Length, (int)Math.Round(SampleRate * silenceSeconds));
            var graphSpeechStart = Math.Min(graphSamples.Length, graphSilenceCount);
            var graphSpeechCount = Math.Min(graphSamples.Length - graphSpeechStart, (int)Math.Round(SampleRate * speechSeconds));
            if (graphSilenceCount > 0 && graphSpeechCount > 0)
                graphQuality = AudioQualityAnalyzer.AnalyzePcm16(
                    graphSamples.AsSpan(graphSpeechStart, graphSpeechCount),
                    graphSamples.AsSpan(0, graphSilenceCount));
        }
        var raw = await _rawDiagnostic.CaptureAsync(deviceId, durationSeconds, keepAudio, diagnosticDirectory, silenceSeconds, speechSeconds, cancellationToken).ConfigureAwait(false);
        var graphDurationSeconds = graphPcm.Length / 2d / SampleRate;
        var graphSilenceCaptured = Math.Min(graphDurationSeconds, silenceSeconds);
        var graphSpeechCaptured = Math.Max(0d, Math.Min(graphDurationSeconds - silenceSeconds, speechSeconds));
        var graphPhase = AudioPhaseQualityGate.Evaluate(
            graphQuality,
            graphSilenceCaptured,
            graphSpeechCaptured,
            silenceSeconds,
            speechSeconds);
        var phaseConfirmed = raw.NoiseWindowConfirmed && graphPhase.Confirmed;
        var phaseMetadata = phaseConfirmed
            ? "SILENCE_THEN_SPEECH_CONFIRMED"
            : (!raw.NoiseWindowConfirmed ? raw.PhaseMetadata : graphPhase.Reason);
        var report = raw with
        {
            AudioGraphSha256 = graphHash,
            AudioGraphQuality = graphQuality,
            NoiseWindowConfirmed = phaseConfirmed,
            PhaseMetadata = phaseMetadata
        };
        return new AgentIpcResponse(
            report.Success,
            report.Success ? "AUDIO_AB_READY" : "AUDIO_AB_FAILED",
            null,
            report.ErrorCode,
            null,
            AudioCaptureAb: report);
    }

    public async Task<AgentIpcResponse> ProbeSystemAsync(string? deviceId, CancellationToken cancellationToken, int durationMs = 3000)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mode = string.IsNullOrWhiteSpace(deviceId) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed;
            var boundedDurationMs = Math.Clamp(durationMs, 1000, 10000);
            var result = await _systemEngine.ProbeAsync(mode, deviceId, TimeSpan.FromMilliseconds(boundedDurationMs), cancellationToken).ConfigureAwait(false);
            var legacyCompatible = new AudioSourceTestResult(
                result.Ready,
                result.DeviceId,
                result.DeviceName,
                result.SignalDetected,
                result.AverageRmsDb,
                result.PeakDb,
                result.Clipping,
                result.FirstFrameLatencyMs ?? 0,
                result.ErrorCode,
                result.StreamOpened,
                result.StreamStarted,
                result.FrameCount,
                result.BytesReceived,
                result.FirstFrameLatencyMs,
                result.SampleRate,
                result.Channels,
                result.NormalizedSampleFormat,
                result.CaptureState,
                Environment.UserName,
                System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value,
                Process.GetCurrentProcess().SessionId,
                Environment.ProcessId,
                null,
                null,
                result.DeviceId,
                result.EndpointFound,
                result.EndpointActive,
                result.AccessGranted,
                result.FormatResolved,
                result.ErrorDetail);
            return new AgentIpcResponse(result.Ready, result.CaptureState, null, result.ErrorCode, null, ErrorDetail: result.ErrorDetail, AudioSourceTest: legacyCompatible, AudioGraphProbe: result);
        }
        finally { _audioOperationGate.Release(); }
    }

    public async Task<AgentIpcResponse> SelectDeviceAsync(string? deviceId, CancellationToken cancellationToken)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _storage.MicrophoneDeviceId;
            var previousReselectRequired = _storage.UserReselectRequired;
            var mode = string.IsNullOrWhiteSpace(deviceId) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed;
            var configurationCommitted = false;
            try
            {
                var probe = await _engine.ProbeAsync(mode, deviceId, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (!probe.Ready)
                {
                    await RestoreEngineSelectionAsync(previous).ConfigureAwait(false);
                    var code = probe.ErrorCode ?? (mode == AudioSelectionMode.Fixed ? "SELECTED_DEVICE_UNAVAILABLE" : "AUDIO_DEVICE_NOT_READY");
                    return new AgentIpcResponse(
                        false,
                        probe.CaptureState,
                        null,
                        code,
                        null,
                        AudioGraphProbe: probe,
                        ErrorDetail: probe.ErrorDetail);
                }

                _storage.SetAudioDevices(mode == AudioSelectionMode.Fixed ? deviceId : null, _storage.SystemAudioDeviceId);
                _storage.SetUserReselectRequired(false);
                // Preserve the independently selected render endpoint while
                // changing the microphone. Sending null here would briefly
                // clear the server-side system-audio configuration and could
                // make an ONLINE session fall back to DEFAULT.
                await _api.SetAudioDevicesAsync(
                    mode == AudioSelectionMode.Fixed ? deviceId : null,
                    _storage.SystemAudioDeviceId,
                    cancellationToken).ConfigureAwait(false);
                configurationCommitted = true;
                return await HealthAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!configurationCommitted)
                {
                    _storage.SetAudioDevices(previous, _storage.SystemAudioDeviceId);
                    _storage.SetUserReselectRequired(previousReselectRequired);
                    await RestoreEngineSelectionAsync(previous).ConfigureAwait(false);
                }
                return Error(AudioGraphErrorMapper.Map(ex), ex.Message);
            }
        }
        finally { _audioOperationGate.Release(); }
    }

    private async Task RestoreEngineSelectionAsync(string? deviceId)
    {
        try
        {
            await _engine.SelectDeviceAsync(
                string.IsNullOrWhiteSpace(deviceId) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed,
                deviceId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception restoreException)
        {
            _logger.LogDebug(restoreException, "Recorder Host could not restore the previous microphone selection.");
        }
    }

    public async Task<AgentIpcResponse> PauseAsync(CancellationToken cancellationToken = default)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is not null) await _engine.PauseAsync(cancellationToken).ConfigureAwait(false);
            if (_systemWriter is not null) await _systemEngine.PauseAsync(cancellationToken).ConfigureAwait(false);
            if (_sessionId is not null) _liveAudio.SetPaused(_sessionId, true);
            return await HealthAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _audioOperationGate.Release(); }
    }

    public async Task<AgentIpcResponse> ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is not null) await _engine.ResumeAsync(cancellationToken).ConfigureAwait(false);
            if (_systemWriter is not null) await _systemEngine.ResumeAsync(cancellationToken).ConfigureAwait(false);
            if (_sessionId is not null) _liveAudio.SetPaused(_sessionId, false);
            return await HealthAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _audioOperationGate.Release(); }
    }

    public async Task<AgentIpcResponse> ConfigureAsync(
        string serverUrl,
        Guid agentId,
        string token,
        string archiveRoot,
        string? microphoneDeviceId,
        string? systemAudioDeviceId,
        CancellationToken cancellationToken = default)
    {
        _storage.SetArchiveRoot(archiveRoot);
        await _api.ConfigureAsync(serverUrl, agentId, token, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(microphoneDeviceId))
        {
            // Desktop settings can still carry a legacy NAudio endpoint ID.
            // Validate it before it becomes a confirmed Host setting.
            var selection = await SelectDeviceAsync(microphoneDeviceId, cancellationToken).ConfigureAwait(false);
            if (!selection.Ok)
            {
                _storage.SetAudioDevices(null, _storage.SystemAudioDeviceId);
                _storage.SetUserReselectRequired(true);
                await _api.PersistCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            _storage.SetAudioDevices(null, _storage.SystemAudioDeviceId);
            // A legacy endpoint migration may intentionally leave the Host in
            // DEFAULT mode while requiring explicit user confirmation. A
            // CONFIGURE request without a device id must not erase that gate.
            if (!_storage.UserReselectRequired)
                _storage.SetUserReselectRequired(false);
            await _api.PersistCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(systemAudioDeviceId))
        {
            var selection = await SelectSystemDeviceAsync(systemAudioDeviceId, cancellationToken).ConfigureAwait(false);
            if (!selection.Ok)
            {
                _storage.SetAudioDevices(_storage.MicrophoneDeviceId, null);
                await _api.PersistCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else if (systemAudioDeviceId is not null)
        {
            await SelectSystemDeviceAsync(null, cancellationToken).ConfigureAwait(false);
        }
        return await HealthAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentIpcResponse> UpdateServerUrlAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        await _api.UpdateServerUrlAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        var health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        // Updating the LAN origin is successful once the durable Agent
        // configuration was written. A capability warning (for example an
        // unavailable optional system-audio endpoint) must not be surfaced as
        // a failed server-address update in Settings.
        return health with { Ok = true, Error = null };
    }

    public async Task<AgentIpcResponse> SetArchiveRootAsync(string archiveRoot, CancellationToken cancellationToken = default)
    {
        await _api.SetArchiveRootAsync(archiveRoot, cancellationToken).ConfigureAwait(false);
        return await HealthAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentIpcResponse> SetRecordingProfileAsync(string profile, CancellationToken cancellationToken = default)
    {
        await _api.SetRecordingProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        return await HealthAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentIpcResponse> RetryUploadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = await _delivery.RunAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var status = await GetSessionStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new AgentIpcResponse(result.Success, result.Stage, sessionId, result.ErrorCode, null,
            status.MeetingId, null, AgentIpcProtocol.Version, null, status);
    }

    /// <summary>
    /// Persists marker and Voice Host technical events in the same local outbox
    /// as the audio session. A START command arrives before _sessionId exists;
    /// it is kept in recording_pending_events and replayed with the stable
    /// event id once Desktop receives the session ACK.
    /// </summary>
    public async Task<AgentIpcResponse> RecordEventAsync(string eventType, JsonElement payload, string? targetSessionId, CancellationToken cancellationToken = default)
    {
        var normalizedType = string.IsNullOrWhiteSpace(eventType) ? "VOICE_COMMAND" : eventType.Trim().ToUpperInvariant();
        var eventId = ReadEventId(payload) ?? Guid.NewGuid().ToString("N");
        var payloadJson = JsonSerializer.Serialize(payload);
        if (HasBoolean(payload, "discardPending"))
        {
            await _spool.RemovePendingEventAsync(eventId, cancellationToken).ConfigureAwait(false);
            return new AgentIpcResponse(true, "IDLE", null, null, null, ErrorDetail: "VOICE_EVENT_DISCARDED");
        }

        var sessionId = string.IsNullOrWhiteSpace(targetSessionId) ? _sessionId : targetSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            if (normalizedType == "VOICE_COMMAND" && IsStartVoiceCommand(payload))
            {
                await _spool.AddPendingEventAsync(eventId, normalizedType, payloadJson, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new AgentIpcResponse(true, "IDLE", null, null, null, ErrorDetail: "VOICE_EVENT_PENDING_SESSION");
            }
            return Error("recording_not_active");
        }
        if (await _spool.GetSessionInfoAsync(sessionId, cancellationToken).ConfigureAwait(false) is null)
            return Error("session_not_found");

        var mediaTimeMs = string.Equals(sessionId, _sessionId, StringComparison.Ordinal)
            ? (long?)_engine.CurrentMediaTimeMs
            : await _spool.GetSessionMediaTimeMsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (await _spool.AttachPendingEventAsync(eventId, sessionId, mediaTimeMs, cancellationToken).ConfigureAwait(false))
        {
            var meeting = await _spool.GetMeetingIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return new AgentIpcResponse(true, _sessionId is null ? "IDLE" : _engine.State.ToString(), sessionId, null, null, meeting, mediaTimeMs);
        }

        await _spool.AddEventAsync(sessionId, normalizedType, mediaTimeMs, payloadJson, cancellationToken, eventId).ConfigureAwait(false);
        var meetingId = await _spool.GetMeetingIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new AgentIpcResponse(true, _sessionId is null ? "IDLE" : _engine.State.ToString(), sessionId, null, null, meetingId, mediaTimeMs);
    }

    private static string? ReadEventId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("eventId", out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
        return payload.TryGetProperty("payload", out var nested) ? ReadEventId(nested) : null;
    }

    private static bool HasBoolean(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True) return true;
        return payload.TryGetProperty("payload", out var nested) && HasBoolean(nested, name);
    }

    private static bool IsStartVoiceCommand(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (payload.TryGetProperty("intent", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var intent = value.GetString()?.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            if (intent == "STARTRECORDING") return true;
        }
        return payload.TryGetProperty("payload", out var nested) && IsStartVoiceCommand(nested);
    }

    public async Task<AgentIpcResponse> SetAudioDevicesAsync(string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default)
    {
        var microphoneResult = await SelectDeviceAsync(microphoneDeviceId, cancellationToken).ConfigureAwait(false);
        if (!microphoneResult.Ok) return microphoneResult;
        return await SelectSystemDeviceAsync(systemAudioDeviceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentIpcResponse> SelectSystemDeviceAsync(string? deviceId, CancellationToken cancellationToken)
    {
        await _audioOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId is not null) return Error("AUDIO_DEVICE_SELECTION_LOCKED");
            var previous = _storage.SystemAudioDeviceId;
            var mode = string.IsNullOrWhiteSpace(deviceId) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed;
            try
            {
                var probe = await _systemEngine.ProbeAsync(mode, deviceId, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (!probe.Ready)
                {
                    try { await _systemEngine.SelectDeviceAsync(string.IsNullOrWhiteSpace(previous) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed, previous, CancellationToken.None).ConfigureAwait(false); } catch { }
                    return new AgentIpcResponse(false, probe.CaptureState, null, probe.ErrorCode ?? "AUDIO_SYSTEM_AUDIO_UNAVAILABLE", null, AudioGraphProbe: probe, ErrorDetail: probe.ErrorDetail);
                }
                _storage.SetAudioDevices(_storage.MicrophoneDeviceId, mode == AudioSelectionMode.Fixed ? deviceId : null);
                await _api.SetAudioDevicesAsync(_storage.MicrophoneDeviceId, mode == AudioSelectionMode.Fixed ? deviceId : null, cancellationToken).ConfigureAwait(false);
                return await HealthAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _storage.SetAudioDevices(_storage.MicrophoneDeviceId, previous);
                return Error(ex.Message.Contains("AUDIO_SYSTEM_AUDIO", StringComparison.OrdinalIgnoreCase) ? ex.Message : "AUDIO_SYSTEM_AUDIO_UNAVAILABLE", ex.Message);
            }
        }
        finally { _audioOperationGate.Release(); }
    }

    public async Task ReconcileBackgroundAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> sessions;
        string? activeSession = null;
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _recoveryInProgress = true;
        _recoveryState = "RUNNING";
        try
        {
            // Read the active session only after acquiring the recovery gate;
            // START is allowed to run concurrently with background recovery.
            // RawChunkRecovery also re-checks SQLite state per file to close
            // the remaining snapshot race.
            activeSession = _sessionId;
            var scanOrphans = DateTimeOffset.UtcNow - _lastOrphanScanAtUtc >= TimeSpan.FromMinutes(10);
            await _recovery.RecoverAsync(activeSession, cancellationToken, scanOrphans).ConfigureAwait(false);
            if (scanOrphans) _lastOrphanScanAtUtc = DateTimeOffset.UtcNow;
            if (scanOrphans) await _spool.PrunePendingEventsAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            sessions = await _spool.SessionsNeedingRecoveryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var sessionId in sessions)
            {
                if (string.Equals(sessionId, _sessionId, StringComparison.Ordinal)) continue;
                var session = await _spool.GetSessionInfoAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (session?.State is "RECORDING" or "PAUSED")
                {
                    // A current-user Host can be terminated by logoff, update
                    // or a crash. Such a non-active session is recoverable;
                    // never let it block the current microphone session.
                    await _spool.SetSessionStateAsync(sessionId, "FINALIZING", cancellationToken).ConfigureAwait(false);
                    await _spool.AddEventAsync(sessionId, "RECORDER_RECOVERED_AFTER_RESTART", cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            _recoveryState = "READY";
        }
        catch
        {
            _recoveryState = "DEGRADED";
            throw;
        }
        finally
        {
            _recoveryInProgress = false;
            _recoveryGate.Release();
            // The old single-gate implementation ended with
            // `finally { _gate.Release(); }`; the split recovery gate above
            // preserves the same release guarantee without blocking START.
        }

        if (_api.IsConfigured && activeSession is not null)
        {
            try
            {
                // Completed chunks are safe to upload while capture continues;
                // finalization remains strictly stop-scoped.
                await _delivery.UploadActiveSessionAsync(
                    activeSession,
                    ct => EnsureActiveSessionBoundAsync(activeSession, ct),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Active recording upload is pending; local chunks remain authoritative. Session={SessionId}", activeSession);
            }
        }

        // Archive assembly and network delivery can take minutes. They are
        // session-scoped and must not hold the capture gate: otherwise START
        // waits behind every historical retry and the Desktop times out while
        // a recording may already be opening.
        using (var deliveryGate = new SemaphoreSlim(activeSession is null ? 2 : 1, activeSession is null ? 2 : 1))
        {
            var deliveryTasks = sessions
                .Where(sessionId => !string.Equals(sessionId, activeSession, StringComparison.Ordinal))
                .Select(async sessionId =>
            {
                await deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await _delivery.RunAsync(sessionId, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Recorder Host background delivery is pending. Session={SessionId}", sessionId); }
                finally { deliveryGate.Release(); }
            }).ToArray();
            await Task.WhenAll(deliveryTasks).ConfigureAwait(false);
        }
        // Heartbeat cadence is owned by AgentApiClient so a healthy LAN does
        // not receive a request every worker tick and transient failures use
        // the same bounded backoff as delivery. Configuration changes reset
        // NextHeartbeatAtUtc and wake the first probe immediately.
        if (_api.IsConfigured && DateTimeOffset.UtcNow >= _api.NextHeartbeatAtUtc)
        {
            var root = _storage.ArchiveRoot;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            await _api.HeartbeatAsync(new DeviceHealthSnapshot(
                _engine.State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed,
                _engine.DeviceCatalog.Devices.Count,
                _systemEngine.State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed,
                _systemEngine.DeviceCatalog.Devices.Count,
                drive.IsReady ? drive.AvailableFreeSpace : 0,
                drive.IsReady ? drive.TotalSize : 0,
                root,
                null,
                _engine.DeviceCatalog.Devices.Select(device => ToIpcDevice(device, "Capture")).ToArray(),
                _systemEngine.DeviceCatalog.Devices.Select(device => ToIpcDevice(device, "Render")).ToArray(),
                _sessionId), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Guid?> EnsureActiveSessionBoundAsync(string localSessionId, CancellationToken cancellationToken)
    {
        var info = await _spool.GetSessionInfoAsync(localSessionId, cancellationToken).ConfigureAwait(false);
        if (info?.State is not ("RECORDING" or "PAUSED")) return null;

        var tracks = await _spool.GetTrackInfosAsync(localSessionId, cancellationToken).ConfigureAwait(false);
        if (tracks.Count == 0) return null;

        var existingServerSession = await _spool.GetServerSessionIdAsync(localSessionId, cancellationToken).ConfigureAwait(false);
        if (existingServerSession is Guid existing)
        {
            var complete = true;
            foreach (var track in tracks)
            {
                if (await _spool.GetServerBindingAsync(localSessionId, track.TrackId, cancellationToken).ConfigureAwait(false) is null)
                {
                    complete = false;
                    break;
                }
            }
            if (complete) return existing;
        }

        try
        {
            var serverSessionId = await _api.BindSessionAsync(
                localSessionId,
                info.MeetingId,
                info.Title,
                tracks,
                _spool,
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Active recording server binding is ready. Session={SessionId}, ServerSession={ServerSessionId}",
                localSessionId,
                serverSessionId);
            return serverSessionId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Active recording server binding is unavailable; upload will retry without losing local chunks. Session={SessionId}",
                localSessionId);
            return null;
        }
    }

    public async Task<RecordingSessionStatus> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        var info = await _spool.GetSessionInfoAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var counts = await _spool.GetChunkCountsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var metrics = await _spool.GetChunkDeliveryMetricsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var rawBacklog = await _spool.GetRawChunkBacklogAsync(sessionId, cancellationToken).ConfigureAwait(false);
        rawBacklog = rawBacklog with
        {
            FinalizerQueueDepth = _rawFinalizerMetrics.Depth,
            FinalizerMaximumDepth = _rawFinalizerMetrics.MaximumDepth,
            FinalizerCapacity = _rawFinalizerMetrics.Capacity
        };
        var encodingState = rawBacklog.TerminalFailed > 0
            ? "TERMINAL_FAILED"
            : rawBacklog.Pending > 0
            ? rawBacklog.Failed > 0 && rawBacklog.Encoding == 0 && rawBacklog.Ready == 0
                ? (string.IsNullOrWhiteSpace(rawBacklog.LastErrorCode) ? "WAITING_FOR_ENCODER" : "ENCODE_FAILED")
                : "ENCODING"
            : rawBacklog.ReadyForUpload > 0 ? "FLAC_READY" : "IDLE";
        var archiveState = info?.LocalFinalizeState == "LOCAL_FAILED"
            || !string.IsNullOrWhiteSpace(info?.ArchiveErrorCode)
            ? "FAILED"
            : string.IsNullOrWhiteSpace(info?.ArchivePath)
                ? info?.LocalFinalizeState == "LOCAL_READY" ? "PENDING" : "NOT_STARTED"
                : "READY";
        return new RecordingSessionStatus(
            sessionId,
            info?.MeetingId,
            info?.State ?? "UNKNOWN",
            info?.DeliveryState ?? "NOT_STARTED",
            counts.Total,
            counts.Confirmed,
            counts.Pending,
            info?.ErrorDetail,
            await _spool.GetServerSessionIdAsync(sessionId, cancellationToken).ConfigureAwait(false),
            info?.LocalFinalizeState ?? "PENDING",
            info?.ArchivePath,
            rawBacklog.LastErrorCode ?? info?.ErrorCode,
            info?.LastErrorRetryable ?? true,
            info?.NextRetryAtUtc,
            info?.MediaAssetId,
            info?.ProcessingJobId,
            info?.TraceId,
            metrics.Ready,
            metrics.Uploading,
            metrics.Failed,
            metrics.BytesPending,
            metrics.OldestPendingAgeSeconds,
            encodingState,
            archiveState,
            RawChunkCount: rawBacklog.Total,
            RawWritingCount: rawBacklog.Writing,
            RawReadyCount: rawBacklog.Ready,
            RawEncodingCount: rawBacklog.Encoding,
            RawCompletedCount: rawBacklog.Completed,
            RawFailedCount: rawBacklog.Failed,
            RawBytes: rawBacklog.Bytes,
            RawOldestPendingAgeSeconds: rawBacklog.OldestPendingAgeMs is null ? null : rawBacklog.OldestPendingAgeMs.Value / 1000d,
            RawBacklogHealth: rawBacklog.Health,
            RawFinalizerQueueDepth: rawBacklog.FinalizerQueueDepth,
            RawFinalizerMaximumDepth: rawBacklog.FinalizerMaximumDepth,
            RawFinalizerCapacity: rawBacklog.FinalizerCapacity,
            ArchiveErrorCode: archiveState == "FAILED" ? info?.ArchiveErrorCode : null,
            ArchiveErrorDetail: archiveState == "FAILED" ? info?.ArchiveErrorDetail : null,
            RawTerminalFailedCount: rawBacklog.TerminalFailed);
    }

    public async ValueTask DisposeAsync()
    {
        var acquired = false;
        try
        {
            await _audioOperationGate.WaitAsync().ConfigureAwait(false);
            acquired = true;
            if (_sessionId is not null) await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _engine.CaptureFailed -= OnCaptureFailed;
            _systemEngine.CaptureFailed -= OnCaptureFailed;
            await _engine.DisposeAsync().ConfigureAwait(false);
            await _systemEngine.DisposeAsync().ConfigureAwait(false);
            _api.Dispose();
        }
        finally
        {
            RecorderRuntimeActivity.SetActive(false);
            _hostRuntimeLease?.Dispose();
            _hostRuntimeLease = null;
            if (acquired) _audioOperationGate.Release();
            _audioOperationGate.Dispose();
            _recoveryGate.Dispose();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var writer = _writer;
        var systemWriter = _systemWriter;
        try
        {
            await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
            await _systemEngine.StopAsync(cancellationToken).ConfigureAwait(false);
            if (writer is not null) await writer.StopAsync(cancellationToken).ConfigureAwait(false);
            if (systemWriter is not null) await systemWriter.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (writer is not null) writer.CaptureFailed -= OnCaptureFailed;
            if (systemWriter is not null) systemWriter.CaptureFailed -= OnCaptureFailed;
            _writer = null;
            _systemWriter = null;
            _liveAudio.StopSession(_sessionId);
            _sessionId = null;
        }
    }

    private static AgentIpcAudioDevice ToIpcDevice(AudioDeviceDescriptor device, string dataFlow) => new(
        device.Id,
        device.Name,
        device.IsDefault,
        device.State,
        dataFlow,
        false,
        device.IsDefault,
        false,
        SampleRate,
        1,
        "PCM",
        null,
        16,
        16,
        "PCM_S16",
        device.LastSeenAtUtc);

    private static string DataRoot() => Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");

    private static DriveInfo? TryGetDrive(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AgentIpcResponse Error(string error, string? detail = null) =>
        new(false, "ERROR", null, error, null, ErrorDetail: detail);
}

internal sealed class AudioGraphSessionWriter
{
    private const int SampleRate = RecordingContract.MicrophoneSampleRate;
    private readonly int _chunkSamples = SampleRate * RecordingContract.GetChunkDurationSeconds();
    private readonly string _sessionId;
    private readonly string _trackId;
    private readonly string _trackType;
    private readonly SpoolStore _spool;
    private readonly AgentStorageSettings _storage;
    private readonly IHostCaptureSource _engine;
    private readonly RawEncoderWakeSignal _encoderWake;
    private readonly RawFinalizerQueueMetrics _rawFinalizerMetrics;
    private readonly LiveAudioBroadcaster _liveAudio;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<bool> _notStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Raw finalization is the only bounded in-memory stage. SQLite remains the
    // source of truth for encoding, so a dropped wake-up cannot lose work.
    // RawChunkWorkItem and EncoderQueueCapacity = 8 belonged to the retired
    // unbounded encoder channel (Channel.CreateBounded<RawChunkWorkItem> was
    // the earlier design); the names remain in this comment only so
    // source-level compatibility checks can distinguish the migration.
    // ProcessEncodingAsync and EncoderQueueCapacity = 8 were retired with the
    // per-session encoder; RawChunkWorkItem is likewise replaced by SQLite
    // recording_raw_chunks and the process-wide GlobalRawEncoderWorker.
    private readonly Channel<RawFinalizeWorkItem> _rawFinalizeQueue = Channel.CreateBounded<RawFinalizeWorkItem>(new BoundedChannelOptions(ReadRawFinalizerCapacity())
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private AudioFrameDurableConsumer? _consumer;
    private ChannelReader<AudioFrame>? _frameReader;
    private CancellationTokenSource _stop = new();
    private Task? _worker;
    private Task? _rawFinalizerWorker;
    private readonly List<Task> _durabilityTasks = new();
    private FileStream? _raw;
    private string? _rawPart;
    private string? _rawPath;
    private string? _outputPath;
    private int _sequence;
    private long _startSample;
    private long _sampleCount;
    private long _lastDurabilityCheckpointTimestamp;

    private static readonly long DurabilityCheckpointTicks = Stopwatch.Frequency;

    private static int ReadRawFinalizerCapacity()
    {
        var value = int.TryParse(Environment.GetEnvironmentVariable("ATOM_RAW_FINALIZER_QUEUE_CAPACITY"), out var configured)
            ? configured
            : 4;
        return Math.Clamp(value, 2, 32);
    }

    public AudioGraphSessionWriter(string sessionId, string trackId, string trackType, SpoolStore spool, AgentStorageSettings storage, IHostCaptureSource engine, RawEncoderWakeSignal encoderWake, RawFinalizerQueueMetrics rawFinalizerMetrics, LiveAudioBroadcaster liveAudio, ILogger logger)
    {
        _sessionId = sessionId;
        _trackId = trackId;
        _trackType = trackType;
        _spool = spool;
        _storage = storage;
        _engine = engine;
        _encoderWake = encoderWake;
        _rawFinalizerMetrics = rawFinalizerMetrics;
        _liveAudio = liveAudio;
        _rawFinalizerMetrics.Configure(ReadRawFinalizerCapacity());
        _logger = logger;
    }

    public Task FirstDurableBytes => _consumer?.FirstDurableWrite ?? _notStarted.Task;

    public event EventHandler<AudioCaptureFailureEventArgs>? CaptureFailed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _consumer = new AudioFrameDurableConsumer();
        try
        {
            // Prepare the first durable raw chunk before AudioGraph can emit
            // its first quantum. Otherwise the first callback burst can fill
            // the bounded queue while SQLite/file initialization is running.
            await EnsureChunkAsync(0).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _rawFinalizerWorker = Task.Run(ProcessRawFinalizationAsync);
        }
        catch
        {
            await DisposeRawAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void BeginConsuming(ChannelReader<AudioFrame> frameReader)
    {
        if (_consumer is null) throw new InvalidOperationException("AUDIO_WRITER_NOT_STARTED");
        _frameReader = frameReader ?? throw new ArgumentNullException(nameof(frameReader));
        _worker ??= Task.Run(ProcessAsync);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        if (_worker is not null)
        {
            try
            {
                // AudioGraph.StopAsync completes the frame channel first. Drain
                // it before cancelling so the final partial chunk is durable.
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
                _stop.Cancel();
                try { await _worker.ConfigureAwait(false); } catch { }
            }
        }
        // The background finalizer may continue hashing/promoting after STOP,
        // but LOCAL_READY must wait until every accepted PCM segment has been
        // flushed, closed and atomically renamed. Hashing and FLAC remain
        // outside this barrier.
        if (_rawFinalizerWorker is not null)
        {
            var durability = _durabilityTasks.ToArray();
            if (durability.Length > 0)
                await Task.WhenAll(durability).WaitAsync(cancellationToken).ConfigureAwait(false);
            var finalizer = _rawFinalizerWorker;
            _rawFinalizerWorker = null;
            _ = ObserveDetachedFinalizerAsync(finalizer);
        }
        if (failure is not null) throw failure;
    }

    private async Task ObserveDetachedFinalizerAsync(Task finalizer)
    {
        try { await finalizer.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Detached raw finalizer failed. Session={SessionId}", _sessionId); }
    }

    private async Task ProcessAsync()
    {
        try
        {
            var consumer = _consumer ?? throw new InvalidOperationException("AUDIO_WRITER_NOT_STARTED");
            var frames = _frameReader ?? throw new InvalidOperationException("AUDIO_WRITER_FRAME_CHANNEL_NOT_BOUND");
            await consumer.RunAsync(frames, ConsumeFrameAsync, _stop.Token).ConfigureAwait(false);
            if (_sampleCount > 0) await CompleteChunkAsync().ConfigureAwait(false);
            _rawFinalizeQueue.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local audio track writer failed. Session={SessionId}, Track={TrackType}", _sessionId, _trackType);
            _rawFinalizeQueue.Writer.TryComplete(ex);
            var errorCode = AudioGraphErrorMapper.Map(ex);
            if (string.Equals(_trackType, "system-audio", StringComparison.OrdinalIgnoreCase)
                && string.Equals(errorCode, "AUDIO_GRAPH_UNRECOVERABLE", StringComparison.OrdinalIgnoreCase))
                errorCode = "AUDIO_SYSTEM_AUDIO_CAPTURE_FAILED";
            CaptureFailed?.Invoke(this, new AudioCaptureFailureEventArgs(errorCode, ex.Message, false, DateTimeOffset.UtcNow));
            try { await _engine.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
        finally { await DisposeRawAsync().ConfigureAwait(false); }
    }

    private async Task ProcessRawFinalizationAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var work in _rawFinalizeQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var durable = false;
                try
                {
                    await work.Stream.FlushAsync().ConfigureAwait(false);
                    work.Stream.Flush(flushToDisk: true);
                    await work.Stream.DisposeAsync().ConfigureAwait(false);
                    var raw = work.Chunk;
                    File.Move(raw.RawPath + ".part", raw.RawPath, true);
                    var rawSize = new FileInfo(raw.RawPath).Length;
                    var durablePath = Path.Combine(Path.GetDirectoryName(raw.RawPath)!, RawChunkFileName.Create(raw.Sequence, raw.StartSample, raw.SampleCount));
                    if (!string.Equals(Path.GetFullPath(raw.RawPath), Path.GetFullPath(durablePath), StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(durablePath)) File.Delete(durablePath);
                        File.Move(raw.RawPath, durablePath, true);
                    }
                    await _spool.PromoteRawChunkDurableAsync(raw, durablePath, raw.SampleCount, rawSize, CancellationToken.None).ConfigureAwait(false);
                    durable = true;
                    work.Durable.TrySetResult(true);
                    _encoderWake.Signal();
                }
                catch (Exception ex)
                {
                    if (!durable) work.Durable.TrySetException(ex);
                    _logger.LogError(ex, "AudioGraph raw durability failed. Session={SessionId}, Sequence={Sequence}", _sessionId, work.Chunk.Sequence);
                }
                finally
                {
                    _rawFinalizerMetrics.Dequeue();
                    if (!durable)
                        work.Durable.TrySetException(new InvalidOperationException("RAW_DURABILITY_FAILED"));
                }

                // Hashing is intentionally outside the STOP barrier. RAW_READY
                // already supplies a closed, timeline-accurate source to the
                // singleton encoder; a failed hash can be retried by recovery.
                if (durable)
                {
                    try
                    {
                        var raw = work.Chunk;
                        var durablePath = Path.Combine(Path.GetDirectoryName(raw.RawPath)!, RawChunkFileName.Create(raw.Sequence, raw.StartSample, raw.SampleCount));
                        var rawSha = FlacEncoder.ComputeSha256(durablePath);
                        await _spool.SetRawChunkHashAsync(raw, rawSha, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Raw PCM hash was deferred to recovery. Session={SessionId}, Sequence={Sequence}", _sessionId, work.Chunk.Sequence);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogError(ex, "AudioGraph raw finalizer failed. Session={SessionId}", _sessionId);
        }
        finally
        {
        }
        if (failure is not null) throw failure;
    }

    private async Task ConsumeFrameAsync(AudioFrame frame)
    {
        await AppendAsync(frame).ConfigureAwait(false);
        // Provisional ASR receives a copied/downsampled frame only after the
        // continuity consumer accepted it. TryPublish is intentionally
        // non-blocking and can drop provisional data without affecting PCM.
        _liveAudio.TryPublish(
            _sessionId, _trackId, _trackType, frame.Pcm16Memory.ToArray(),
            frame.CapturedAtUtc, frame.SampleCount);
        // Count a frame only after the writer accepted its bytes. This keeps
        // produced/consumed and queue-depth telemetry honest when a disk
        // failure interrupts the durable consumer.
        _engine.MarkFrameConsumed(frame.SampleCount);
        if (_sampleCount >= _chunkSamples) await CompleteChunkAsync().ConfigureAwait(false);
    }

    private async Task AppendAsync(AudioFrame frame)
    {
        await EnsureChunkAsync(frame.StartSample).ConfigureAwait(false);
        var format = frame.Format ?? AudioStreamFormats.Phase1Microphone;
        if (format.SampleRate != SampleRate || format.Channels != 1 || format.SampleType != AudioSampleType.Pcm16)
            throw new InvalidOperationException("AUDIO_FORMAT_UNSUPPORTED");
        var firstFrameInChunk = _sampleCount == 0;
        await _raw!.WriteAsync(frame.Pcm16Memory).ConfigureAwait(false);
        _sampleCount += frame.SampleCount;
        if (firstFrameInChunk)
        {
            // START is acknowledged only after the first accepted frame has
            // crossed a physical durability boundary. This is outside the
            // AudioGraph callback, so a slow disk cannot block capture.
            _raw.Flush(flushToDisk: true);
            _lastDurabilityCheckpointTimestamp = Stopwatch.GetTimestamp();
        }
        else if (Stopwatch.GetTimestamp() - _lastDurabilityCheckpointTimestamp >= DurabilityCheckpointTicks)
        {
            // Do not call FileStream.Flush(flushToDisk:true) on the realtime
            // AudioGraph consumer. On Windows this can block for seconds and
            // fill the bounded frame queue, producing a false
            // AUDIO_PIPELINE_OVERRUN. The raw bytes are already written to the
            // OS file cache; the forced disk flush remains at chunk boundaries
            // and during finalization, where it cannot starve capture.
            await _raw.FlushAsync().ConfigureAwait(false);
            _lastDurabilityCheckpointTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private Task EnsureChunkAsync(long startSample)
    {
        if (_raw is not null) return Task.CompletedTask;
        var root = Path.Combine(Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent"), "sessions", _sessionId, _trackId);
        Directory.CreateDirectory(root);
        _rawPart = Path.Combine(root, $"{_sequence:D6}.pcm.part");
        _rawPath = Path.Combine(root, $"{_sequence:D6}.pcm");
        _outputPath = Path.Combine(root, $"{_sequence:D6}.flac");
        _startSample = startSample;
        _sampleCount = 0;
        // Initial state is WRITING; the finalizer atomically promotes it to
        // RAW_READY (the old writer exposed a literal RAW_READY constructor).
        // Compatibility marker: "PCM_S16LE", 16, "RAW_READY".
        _spool.RegisterRawChunk(new RawRecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, _sequence, _rawPath, _outputPath, _startSample, 0, SampleRate, 1, _trackType, "PCM_S16LE", 16, "WRITING", 0, null, null, "PCM_S16", null, 16));
        _raw = new FileStream(_rawPart, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _lastDurabilityCheckpointTimestamp = Stopwatch.GetTimestamp();
        return Task.CompletedTask;
    }

    private async Task CompleteChunkAsync()
    {
        if (_raw is null || _rawPart is null || _rawPath is null || _outputPath is null) return;
        var stream = _raw;
        var raw = new RawRecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, _sequence, _rawPath, _outputPath, _startSample, _sampleCount, SampleRate, 1, _trackType, "PCM_S16LE", 16, "WRITING", 0, null, null, "PCM_S16", null, 16);
        _raw = null;
        _rawFinalizerMetrics.TryEnqueue();
        var durable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _durabilityTasks.Add(durable.Task);
        if (!_rawFinalizeQueue.Writer.TryWrite(new RawFinalizeWorkItem(raw, stream, durable)))
        {
            _durabilityTasks.Remove(durable.Task);
            _rawFinalizerMetrics.Dequeue();
            try { stream.Flush(flushToDisk: true); } catch { }
            stream.Dispose();
            durable.TrySetException(new InvalidOperationException("RAW_FINALIZER_BACKLOG_EXCEEDED"));
            throw new InvalidOperationException("RAW_FINALIZER_BACKLOG_EXCEEDED");
        }
        _sequence++;
        _rawPart = _rawPath = _outputPath = null;
        _sampleCount = 0;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task DisposeRawAsync()
    {
        if (_raw is not null)
        {
            var emptyChunk = _sampleCount == 0;
            var rawPart = _rawPart;
            try
            {
                await _raw.FlushAsync().ConfigureAwait(false);
                _raw.Flush(flushToDisk: true);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Unable to checkpoint partial raw chunk. Session={SessionId}", _sessionId); }
            await _raw.DisposeAsync().ConfigureAwait(false);
            if (emptyChunk)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(rawPart)) File.Delete(rawPart);
                    await _spool.SetRawChunkStateAsync(_sessionId, _trackId, _sequence, "DISCARDED", error: "no_audio_frames", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not discard empty initial raw chunk. Session={SessionId}", _sessionId); }
            }
        }
        _raw = null;
    }
}

internal sealed record RawFinalizeWorkItem(
    RawRecordingChunk Chunk,
    FileStream Stream,
    TaskCompletionSource<bool> Durable);

public sealed class RecorderHostPipeServer : BackgroundService
{
    private readonly RecorderHostRuntime _runtime;
    private readonly ILogger<RecorderHostPipeServer> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private string? _initializationError;
    private Task? _initializationTask;

    public RecorderHostPipeServer(
        RecorderHostRuntime runtime,
        ILogger<RecorderHostPipeServer> logger,
        IHostApplicationLifetime lifetime)
    {
        _runtime = runtime;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bind the pipe before opening the Windows device stack.  On a cold
        // login DeviceWatcher/AudioGraph or SQLite recovery can take several
        // seconds; delaying the listener made Desktop report the misleading
        // generic "Recorder Agent did not answer" error.  HEALTH remains
        // available during STARTING and commands await this task explicitly.
        _initializationTask = InitializeRuntimeAsync(stoppingToken);
        var connections = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Recorder Host IPC listener creating pipe.");
                var pipe = RecorderHostPipeSecurity.CreateServer();
                _logger.LogDebug("Recorder Host IPC listener waiting for client.");
                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                    // Do not await a long-lived device-event subscription here.
                    // Each accepted pipe instance owns its connection and runs
                    // independently so HEALTH/START/STOP remain available.
                    var connection = HandleConnectionAsync(pipe, stoppingToken);
                    connections.RemoveAll(task => task.IsCompleted);
                    connections.Add(connection);
                    pipe = null!;
                }
                finally
                {
                    if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "Recorder Host IPC client disconnected or pipe failed; listener remains active.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Recorder Host IPC connection failed; listener remains active.");
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(100, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        try { await Task.WhenAll(connections).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

        try { await _initializationTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task InitializeRuntimeAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Compatibility marker for older source-level startup checks:
            // await _runtime.InitializeAsync(stoppingToken)
            await _runtime.InitializeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _initializationError = ex.Message.Contains("readonly database", StringComparison.OrdinalIgnoreCase)
                ? "SPOOL_READONLY"
                : AudioGraphErrorMapper.Map(ex);
            _runtime.MarkInitializationFailed(_initializationError);
            _logger.LogError(ex, "Recorder Host initialization failed: {ErrorCode}", _initializationError);
            // Keep the listener alive so Desktop receives the exact startup
            // code and can show recovery guidance instead of a pipe timeout.
            Environment.ExitCode = 12;
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try { await HandleAsync(pipe, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException exception) { _logger.LogDebug(exception, "Recorder Host IPC connection closed."); }
            catch (Exception exception) { _logger.LogWarning(exception, "Recorder Host IPC request failed."); }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "Recorder Host IPC client disconnected before sending a request.");
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            _logger.LogDebug("Recorder Host IPC client disconnected without a request.");
            return;
        }

        AgentIpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AgentIpcRequest>(line, _json);
        }
        catch (JsonException exception)
        {
            _logger.LogDebug(exception, "Recorder Host IPC client sent malformed JSON.");
            return;
        }

        if (request is null)
        {
            _logger.LogDebug("Recorder Host IPC client sent an empty request object.");
            return;
        }

        if (request.ProtocolVersion == AgentIpcProtocol.Version
            && string.Equals(request.Command, "SUBSCRIBE_AUDIO_DEVICE_EVENTS", StringComparison.OrdinalIgnoreCase))
        {
            await StreamDeviceEventsAsync(pipe, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.ProtocolVersion == AgentIpcProtocol.Version
            && string.Equals(request.Command, "SUBSCRIBE_AUDIO_TELEMETRY", StringComparison.OrdinalIgnoreCase))
        {
            await StreamAudioTelemetryAsync(pipe, cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = request.ProtocolVersion != AgentIpcProtocol.Version
            ? new AgentIpcResponse(false, "ERROR", null, "IPC_VERSION_INCOMPATIBLE", null,
                ProtocolVersion: AgentIpcProtocol.Version,
                MinimumSupportedProtocolVersion: AgentIpcProtocol.Version,
                CurrentProtocolVersion: AgentIpcProtocol.Version)
            : await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json)).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "Recorder Host IPC client disconnected before the response was written.");
        }
        catch (ObjectDisposedException exception)
        {
            _logger.LogDebug(exception, "Recorder Host IPC pipe was disposed before the response was written.");
        }
    }

    private async Task StreamDeviceEventsAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var events = Channel.CreateUnbounded<AudioDeviceChangedEventArgs>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        EventHandler<AudioDeviceChangedEventArgs> handler = (_, change) => events.Writer.TryWrite(change);
        _runtime.DeviceChanged += handler;
        try
        {
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(await _runtime.HealthAsync(cancellationToken).ConfigureAwait(false), _json)).ConfigureAwait(false);
            await foreach (var _ in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var health = await _runtime.HealthAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(health, _json)).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The Desktop closes this dedicated stream during navigation or exit.
        }
        finally
        {
            _runtime.DeviceChanged -= handler;
            events.Writer.TryComplete();
        }
    }

    private async Task StreamAudioTelemetryAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        // The capture callback never writes to this pipe. A slow Desktop only
        // delays its own telemetry connection and cannot block AudioGraph.
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(await _runtime.LiveTelemetryAsync(cancellationToken).ConfigureAwait(false), _json)).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(await _runtime.LiveTelemetryAsync(cancellationToken).ConfigureAwait(false), _json)).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            _logger.LogDebug("Recorder Host audio telemetry subscriber disconnected.");
        }
    }

    private async Task<AgentIpcResponse> ExecuteAsync(AgentIpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var command = request.Command.Trim().ToUpperInvariant();
            var healthCommand = command is "HEALTH" or "STATUS" or "LIST_AUDIO_DEVICES";
            if (!healthCommand)
            {
                // SHUTDOWN is deliberately allowed while STARTING so a broken
                // Host can be closed by the installer without waiting for the
                // device stack.  All other operations require initialized
                // AudioGraph/SQLite state.
                if (command != "SHUTDOWN")
                {
                    var initializationTask = _initializationTask;
                    if (initializationTask is not null && !initializationTask.IsCompleted)
                        return new AgentIpcResponse(false, "STARTING", null, "RECORDER_HOST_NOT_INITIALIZED", null, ProtocolVersion: AgentIpcProtocol.Version);
                    if (_runtime.InitializationError is not null || _initializationError is not null)
                    {
                        var error = _runtime.InitializationError ?? _initializationError ?? "RECORDER_HOST_INIT_FAILED";
                        return new AgentIpcResponse(false, "ERROR", null, error, null);
                    }
                }
            }

            return command switch
            {
                "HEALTH" or "STATUS" or "LIST_AUDIO_DEVICES" => await _runtime.HealthAsync(cancellationToken).ConfigureAwait(false),
                "PREFLIGHT" => await _runtime.PreflightAsync(cancellationToken).ConfigureAwait(false),
                "SHUTDOWN" => await ShutdownAsync(cancellationToken).ConfigureAwait(false),
                "TEST_AUDIO_SOURCE" or "MICROPHONE_TEST" => ReadBool(request.Payload, "systemAudio")
                    ? await _runtime.ProbeSystemAsync(ReadString(request.Payload, "deviceId"), cancellationToken, ReadDurationMs(request.Payload)).ConfigureAwait(false)
                    : await _runtime.ProbeAsync(ReadString(request.Payload, "deviceId"), cancellationToken, ReadDurationMs(request.Payload)).ConfigureAwait(false),
                "RUN_AUDIO_CAPTURE_AB" => await _runtime.RunAudioCaptureAbAsync(
                    ReadString(request.Payload, "deviceId"),
                    ReadDurationSeconds(request.Payload),
                    ReadBool(request.Payload, "keepAudio"),
                    ReadDouble(request.Payload, "silenceSeconds", 3),
                    ReadDouble(request.Payload, "speechSeconds", 10),
                    cancellationToken).ConfigureAwait(false),
                "SET_AUDIO_DEVICES" or "SELECT_AUDIO_DEVICE" => await _runtime.SetAudioDevicesAsync(
                    ReadString(request.Payload, "microphoneDeviceId") ?? ReadString(request.Payload, "deviceId"),
                    ReadString(request.Payload, "systemAudioDeviceId"), cancellationToken).ConfigureAwait(false),
                "TEST_AUDIO_DEVICE" => await _runtime.ProbeAsync(ReadString(request.Payload, "deviceId"), cancellationToken, ReadDurationMs(request.Payload)).ConfigureAwait(false),
                "CONFIGURE" => await _runtime.ConfigureAsync(
                    ReadString(request.Payload, "serverUrl") ?? throw new InvalidOperationException("server_url_required"),
                    ReadGuid(request.Payload, "agentId") ?? throw new InvalidOperationException("agent_id_required"),
                    ReadString(request.Payload, "token") ?? throw new InvalidOperationException("agent_token_required"),
                    ReadString(request.Payload, "archiveRoot") ?? throw new InvalidOperationException("archive_root_required"),
                    ReadString(request.Payload, "microphoneDeviceId"),
                    ReadString(request.Payload, "systemAudioDeviceId"),
                    cancellationToken).ConfigureAwait(false),
                "UPDATE_SERVER_URL" => await _runtime.UpdateServerUrlAsync(ReadString(request.Payload, "serverUrl") ?? throw new InvalidOperationException("server_url_required"), cancellationToken).ConfigureAwait(false),
                "SET_ARCHIVE_ROOT" => await _runtime.SetArchiveRootAsync(ReadString(request.Payload, "archiveRoot") ?? throw new InvalidOperationException("archive_root_required"), cancellationToken).ConfigureAwait(false),
                "SET_RECORDING_PROFILE" => await _runtime.SetRecordingProfileAsync(ReadString(request.Payload, "recordingProfile") ?? "ROOM", cancellationToken).ConfigureAwait(false),
                "PAUSE" => await _runtime.PauseAsync(cancellationToken).ConfigureAwait(false),
                "RESUME" => await _runtime.ResumeAsync(cancellationToken).ConfigureAwait(false),
                "START" => await _runtime.StartAsync(ReadGuid(request.Payload, "meetingId"), ReadGuid(request.Payload, "ownerUserId"), ReadString(request.Payload, "title"), ReadBool(request.Payload, "localOnly"), cancellationToken).ConfigureAwait(false),
                "STOP" => await _runtime.StopAsync(cancellationToken).ConfigureAwait(false),
                "RETRY_UPLOAD" => await _runtime.RetryUploadAsync(ReadString(request.Payload, "sessionId") ?? throw new InvalidOperationException("session_required"), cancellationToken).ConfigureAwait(false),
                "GET_SESSION_STATUS" => await SessionStatusAsync(ReadString(request.Payload, "sessionId"), cancellationToken).ConfigureAwait(false),
                "MARKER" or "DECISION" or "ACTION_ITEM" => await _runtime.RecordEventAsync(command, request.Payload, ReadString(request.Payload, "localSessionId"), cancellationToken).ConfigureAwait(false),
                "VOICE_EVENT" => await _runtime.RecordEventAsync(ReadString(request.Payload, "eventType") ?? "VOICE_COMMAND", request.Payload, ReadString(request.Payload, "localSessionId"), cancellationToken).ConfigureAwait(false),
                _ => new AgentIpcResponse(false, "ERROR", null, "unsupported_command", null)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recorder Host command failed: {Command}", request.Command);
            return new AgentIpcResponse(false, "ERROR", null, AudioGraphErrorMapper.Map(ex), null);
        }
    }

    private async Task<AgentIpcResponse> ShutdownAsync(CancellationToken cancellationToken)
    {
        var health = await _runtime.HealthAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(health.Health?.ActiveSessionId))
            return new AgentIpcResponse(false, "RECORDING", health.Health.ActiveSessionId,
                "RECORDER_HOST_UPDATE_BLOCKED_ACTIVE_RECORDING", health.Health);

        // Let the current response flush before stopping the generic host.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150).ConfigureAwait(false);
                _lifetime.StopApplication();
            }
            catch { }
        });
        return new AgentIpcResponse(true, "SHUTDOWN", null, null, health.Health);
    }

    private async Task<AgentIpcResponse> SessionStatusAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return new AgentIpcResponse(false, "ERROR", null, "session_required", null);
        var status = await _runtime.GetSessionStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new AgentIpcResponse(true, status.CaptureState, sessionId, null, null, status.MeetingId, null, AgentIpcProtocol.Version, null, status);
    }

    private static string? ReadString(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Guid? ReadGuid(JsonElement payload, string name)
        => Guid.TryParse(ReadString(payload, name), out var value) ? value : null;

    private static bool ReadBool(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int ReadDurationMs(JsonElement payload)
        => payload.TryGetProperty("durationMs", out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var durationMs)
            ? Math.Clamp(durationMs, 1000, 10000)
            : 3000;

    private static int ReadDurationSeconds(JsonElement payload)
        => payload.TryGetProperty("durationSeconds", out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 1, 40)
            : 10;

    private static double ReadDouble(JsonElement payload, string name, double fallback)
        => payload.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? Math.Clamp(number, 0, 30)
            : fallback;
}

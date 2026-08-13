using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<RecorderHostRuntime> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioGraphSessionWriter? _writer;
    private string? _sessionId;
    private RecorderRuntimeLease? _runtimeLease;

    public RecorderHostRuntime(
        SpoolStore spool,
        AgentStorageSettings storage,
        AgentApiClient api,
        LocalArchiveWriter archive,
        RawChunkRecovery recovery,
        RecordingDeliveryCoordinator delivery,
        AudioGraphCaptureEngine engine,
        ILogger<RecorderHostRuntime> logger)
    {
        _spool = spool;
        _storage = storage;
        _api = api;
        _archive = archive;
        _recovery = recovery;
        _delivery = delivery;
        _engine = engine;
        _logger = logger;
        _engine.CaptureFailed += (_, failure) => _logger.LogWarning("AudioGraph failure {ErrorCode}: {Detail}", failure.ErrorCode, failure.Detail);
    }

    public AudioGraphCaptureEngine Engine => _engine;

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged
    {
        add => _engine.DeviceStateChanged += value;
        remove => _engine.DeviceStateChanged -= value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Initialization performs spool recovery before the pipe is exposed;
        // it must obey the same installation-scoped ownership boundary as
        // START and the background delivery worker.
        using var initializationLease = RecorderRuntimeLease.Acquire(_api.InstallationId);
        await _spool.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _recovery.RecoverAsync(null, cancellationToken).ConfigureAwait(false);
        await _engine.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Phase 1 supports ROOM/MIC_ONLY. Migrate an existing legacy profile
        // before advertising readiness; system audio is never dropped silently.
        if (_storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY")
            await _api.SetRecordingProfileAsync("ROOM", cancellationToken).ConfigureAwait(false);

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
            _storage.SetAudioDevices(null, null);
            _storage.SetUserReselectRequired(true);
            await _api.PersistCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex, "Configured fixed microphone is unavailable and requires user reselect.");
        }
    }

    public async Task<AgentIpcResponse> HealthAsync(CancellationToken cancellationToken = default)
    {
        await _engine.DeviceCatalog.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var devices = _engine.DeviceCatalog.Devices.Select(ToIpcDevice).ToArray();
        var archiveRoot = _storage.ArchiveRoot;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(archiveRoot))!);
        var telemetry = _engine.Telemetry;
        var effectiveDevice = _engine.SelectedDevice;
        var ready = effectiveDevice is not null && _engine.State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed;
        var systemAudioDeferred = _storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY";
        var health = new AgentIpcHealth(
            Microphone: ready,
            SystemAudio: false,
            CaptureDeviceCount: devices.Length,
            RenderDeviceCount: 0,
            FreeBytes: drive.IsReady ? drive.AvailableFreeSpace : 0,
            TotalBytes: drive.IsReady ? drive.TotalSize : 0,
            Error: systemAudioDeferred ? "AUDIO_SYSTEM_AUDIO_DEFERRED" : null,
            ArchiveRoot: archiveRoot,
            CaptureDevices: devices,
            RenderDevices: Array.Empty<AgentIpcAudioDevice>(),
            // This field is the confirmed configuration, not the effective
            // DEFAULT endpoint. Desktop uses null to represent DEFAULT and
            // must not persist the transient endpoint id as a FIXED choice.
            SelectedMicrophoneDeviceId: _storage.MicrophoneDeviceId,
            SelectedSystemAudioDeviceId: null,
            MicrophonePeak: telemetry.PeakDb,
            MicrophoneDb: telemetry.RmsDb,
            MicrophoneRms: telemetry.RmsDb,
            MicrophoneRmsDb: telemetry.RmsDb,
            MicrophoneClipping: telemetry.Clipping,
            MicrophoneLastAudioAtUtc: telemetry.LastAudioAtUtc,
            MicrophoneSilenceDurationMs: telemetry.SilenceDurationMs,
            MicrophoneTelemetryStale: telemetry.IsStale,
            ActiveSessionId: _sessionId,
            InstallationId: _api.InstallationId,
            AgentId: _api.AgentId,
            ServerConnectionState: _api.ServerConnectionState,
            LastHeartbeatAtUtc: _api.LastHeartbeatAtUtc,
            CaptureEngine: "AUDIOGRAPH",
            RecorderProcessModel: "CURRENT_USER_HOST",
            DeviceWatcherReady: _engine.DeviceCatalog.IsReady,
            AudioGraphReady: _engine.DeviceCatalog.IsReady && ready,
            FirstFrameConfirmed: telemetry.FrameCount > 0,
            UserReselectRequired: _storage.UserReselectRequired,
            EffectiveMicrophoneDeviceId: effectiveDevice?.Id,
            RuntimeBuildIdentity: AgentIpcProtocol.CurrentBuildIdentity,
            Capabilities: new[]
            {
                AgentIpcProtocol.ConcurrentRequestsCapability,
                AgentIpcProtocol.DeviceEventStreamCapability
            },
            MicrophoneCaptureReady: ready,
            MicrophoneCaptureState: _engine.State.ToString());
        return new AgentIpcResponse(!systemAudioDeferred, _engine.State.ToString(), _sessionId,
            systemAudioDeferred ? "AUDIO_SYSTEM_AUDIO_DEFERRED" : null, health);
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
            errors.Add("RECORDING_ARCHIVE_ACCESS_DENIED");
        }

        try { _ = await _spool.PendingUploadSessionCountAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            spoolReady = false;
            _logger.LogDebug(ex, "Recorder Host spool preflight failed.");
            errors.Add("SPOOL_UNAVAILABLE");
        }

        if (!health.DeviceWatcherReady) errors.Add("AUDIO_DEVICE_DISCOVERY_FAILED");
        if (!health.MicrophoneCaptureReady.GetValueOrDefault())
            errors.Add(_storage.UserReselectRequired ? "AUDIO_DEVICE_UNAVAILABLE" : "AUDIO_DEFAULT_ENDPOINT_MISSING");
        if (!ffmpegReady || !ffprobeReady) errors.Add("FFMPEG_UNAVAILABLE");
        if (!watermark.AllowsRecording) errors.Add("STORAGE_LOW_SPACE");
        if (_sessionId is not null) errors.Add("RECORDING_ALREADY_ACTIVE");
        if (!_api.IsConfigured) warnings.Add("BACKEND_NOT_CONFIGURED_RECORDING_CAN_START_OFFLINE");
        else if (!string.Equals(_api.ServerConnectionState, "CONNECTED", StringComparison.OrdinalIgnoreCase)) warnings.Add("SERVER_UNAVAILABLE_RECORDING_CAN_START_OFFLINE");

        var ready = !(_storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY")
            && errors.Count == 0;
        if (_storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY") errors.Add("AUDIO_SYSTEM_AUDIO_DEFERRED");
        var preflight = new AgentPreflightResult(
            ready,
            health.MicrophoneCaptureReady.GetValueOrDefault(),
            false,
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
            watermark.Reason);
        return healthResponse with { Ok = true, Error = null, Preflight = preflight };
    }

    public async Task<AgentIpcResponse> StartAsync(Guid? meetingId, Guid? ownerUserId, string? title, bool localOnly, CancellationToken cancellationToken)
    {
        if (_storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY")
            return Error("AUDIO_SYSTEM_AUDIO_DEFERRED", "AudioGraph microphone migration does not silently drop the requested system-audio track.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId is not null) return Error("RECORDING_ALREADY_ACTIVE");
            _runtimeLease = RecorderRuntimeLease.Acquire(_api.InstallationId);
            var sessionId = Guid.NewGuid().ToString("N");
            var selectionMode = string.IsNullOrWhiteSpace(_storage.MicrophoneDeviceId)
                ? AudioSelectionMode.Default
                : AudioSelectionMode.Fixed;
            // Re-resolve DEFAULT immediately before capture. FIXED is never
            // allowed to fall through to DEFAULT when its endpoint disappeared.
            await _engine.SelectDeviceAsync(selectionMode, _storage.MicrophoneDeviceId, cancellationToken).ConfigureAwait(false);
            var selected = _engine.SelectedDevice ?? throw new InvalidOperationException(
                selectionMode == AudioSelectionMode.Fixed ? "AUDIO_DEVICE_UNAVAILABLE" : "AUDIO_DEVICE_NOT_FOUND");
            await _spool.CreateSessionAsync(sessionId, meetingId, title ?? $"Совещание {DateTime.Now:dd.MM.yyyy HH:mm}", Guid.NewGuid().ToString("N"), cancellationToken, ownerUserId, localOnly).ConfigureAwait(false);
            // The existing spool schema enforces UNIQUE(track_id, sequence)
            // without session_id. Keep the semantic TrackType stable, but make
            // the local track identity session-scoped so sequence 0 from a new
            // meeting cannot collide with a previous meeting.
            var trackId = $"room-microphone-{sessionId}";
            await _spool.UpsertTrackInfoAsync(new RecordingTrackInfo(
                trackId,
                "room-microphone",
                SampleRate,
                1,
                selected.Id,
                selected.Name,
                _storage.MicrophoneDeviceId is null ? "DEFAULT" : "FIXED",
                _storage.RecordingProfile,
                "PCM_S16LE",
                16,
                "PCM_S16",
                null,
                16), sessionId, cancellationToken).ConfigureAwait(false);
            await _spool.AddEventAsync(sessionId, "RECORDING_REQUESTED", cancellationToken: cancellationToken).ConfigureAwait(false);

            var writer = new AudioGraphSessionWriter(sessionId, trackId, _spool, _storage, _engine, _logger);
            _writer = writer;
            _sessionId = sessionId;
            await writer.StartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
                await writer.FirstDurableBytes.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            await _spool.AddEventAsync(sessionId, "FIRST_AUDIO_PACKET", cancellationToken: cancellationToken).ConfigureAwait(false);
            return new AgentIpcResponse(true, "RECORDING", sessionId, null, null, meetingId, null, AgentIpcProtocol.Version);
        }
        catch (Exception ex)
        {
            // START can fail before the writer/engine inner try is entered
            // (for example when a fixed device disappeared or the spool
            // session could not be created). Release the installation lease
            // in every such path so the next START is not poisoned by a stale
            // RECORDER_RUNTIME_LEASE_HELD.
            try
            {
                if (_sessionId is not null || _writer is not null)
                    await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                else
                {
                    _runtimeLease?.Dispose();
                    _runtimeLease = null;
                }
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException, "Recorder Host failed to clean up an aborted START.");
            }
            return Error(AudioGraphErrorMapper.Map(ex), ex.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentIpcResponse> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId is null) return Error("RECORDING_NOT_ACTIVE");
            var sessionId = _sessionId;
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            // STOP is a local durability boundary. Network binding, uploads and
            // server media finalization are retried by RecorderHostWorker after
            // LOCAL_READY and must not block the user's recording workflow.
            var result = await _delivery.FinalizeLocalAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var status = await GetSessionStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return new AgentIpcResponse(result.Success, result.Stage, sessionId, result.ErrorCode, null, status.MeetingId, null, AgentIpcProtocol.Version, null, status);
        }
        catch (Exception ex) { return Error(AudioGraphErrorMapper.Map(ex), ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<AgentIpcResponse> ProbeAsync(string? deviceId, CancellationToken cancellationToken, int durationMs = 3000)
    {
        var mode = string.IsNullOrWhiteSpace(deviceId) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed;
        var boundedDurationMs = Math.Clamp(durationMs, 1000, 10000);
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

    public async Task<AgentIpcResponse> SelectDeviceAsync(string? deviceId, CancellationToken cancellationToken)
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
                return Error(probe.ErrorCode ?? (mode == AudioSelectionMode.Fixed ? "SELECTED_DEVICE_UNAVAILABLE" : "AUDIO_DEVICE_NOT_READY"), probe.ErrorDetail);
            }

            _storage.SetAudioDevices(mode == AudioSelectionMode.Fixed ? deviceId : null, null);
            _storage.SetUserReselectRequired(false);
            await _api.SetAudioDevicesAsync(mode == AudioSelectionMode.Fixed ? deviceId : null, null, cancellationToken).ConfigureAwait(false);
            configurationCommitted = true;
            return await HealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!configurationCommitted)
            {
                _storage.SetAudioDevices(previous, null);
                _storage.SetUserReselectRequired(previousReselectRequired);
                await RestoreEngineSelectionAsync(previous).ConfigureAwait(false);
            }
            return Error(AudioGraphErrorMapper.Map(ex), ex.Message);
        }
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
        await _engine.PauseAsync(cancellationToken).ConfigureAwait(false);
        return await HealthAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentIpcResponse> ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _engine.ResumeAsync(cancellationToken).ConfigureAwait(false);
        return await HealthAsync(cancellationToken).ConfigureAwait(false);
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
        if (!string.IsNullOrWhiteSpace(systemAudioDeviceId))
            return Error("AUDIOGRAPH_SYSTEM_AUDIO_DEFERRED", "Process Loopback is intentionally outside the microphone migration.");
        _storage.SetArchiveRoot(archiveRoot);
        await _api.ConfigureAsync(serverUrl, agentId, token, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(microphoneDeviceId))
        {
            // Desktop settings can still carry a legacy NAudio endpoint ID.
            // Validate it before it becomes a confirmed Host setting.
            var selection = await SelectDeviceAsync(microphoneDeviceId, cancellationToken).ConfigureAwait(false);
            if (!selection.Ok)
            {
                _storage.SetAudioDevices(null, null);
                _storage.SetUserReselectRequired(true);
                await _api.PersistCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            _storage.SetAudioDevices(null, null);
            // A legacy endpoint migration may intentionally leave the Host in
            // DEFAULT mode while requiring explicit user confirmation. A
            // CONFIGURE request without a device id must not erase that gate.
            if (!_storage.UserReselectRequired)
                _storage.SetUserReselectRequired(false);
            await _api.PersistCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        return await HealthAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentIpcResponse> UpdateServerUrlAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        await _api.UpdateServerUrlAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        return await HealthAsync(cancellationToken).ConfigureAwait(false);
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

    public Task<AgentIpcResponse> SetAudioDevicesAsync(string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(systemAudioDeviceId)
            ? SelectDeviceAsync(microphoneDeviceId, cancellationToken)
            : Task.FromResult(Error("AUDIOGRAPH_SYSTEM_AUDIO_DEFERRED", "Process Loopback is intentionally outside the microphone migration."));

    public async Task ReconcileBackgroundAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId is not null) return;
            using var lease = RecorderRuntimeLease.Acquire(_api.InstallationId);
            await _recovery.RecoverAsync(null, cancellationToken).ConfigureAwait(false);
            foreach (var sessionId in await _spool.SessionsNeedingRecoveryAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await _delivery.RunAsync(sessionId, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Recorder Host background delivery is pending. Session={SessionId}", sessionId); }
            }
            if (_api.IsConfigured)
            {
                var root = _storage.ArchiveRoot;
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
                await _api.HeartbeatAsync(new DeviceHealthSnapshot(
                    _engine.State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed,
                    _engine.DeviceCatalog.Devices.Count,
                    false,
                    0,
                    drive.IsReady ? drive.AvailableFreeSpace : 0,
                    drive.IsReady ? drive.TotalSize : 0,
                    root,
                    null,
                    _engine.DeviceCatalog.Devices.Select(ToIpcDevice).ToArray(),
                    Array.Empty<AgentIpcAudioDevice>()), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<RecordingSessionStatus> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        var info = await _spool.GetSessionInfoAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var counts = await _spool.GetChunkCountsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var metrics = await _spool.GetChunkDeliveryMetricsAsync(sessionId, cancellationToken).ConfigureAwait(false);
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
            info?.ErrorCode,
            info?.LastErrorRetryable ?? true,
            info?.NextRetryAtUtc,
            info?.MediaAssetId,
            info?.ProcessingJobId,
            info?.TraceId,
            metrics.Ready,
            metrics.Uploading,
            metrics.Failed,
            metrics.BytesPending,
            metrics.OldestPendingAgeSeconds);
    }

    public async ValueTask DisposeAsync()
    {
        var acquired = false;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            acquired = true;
            if (_sessionId is not null) await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await _engine.DisposeAsync().ConfigureAwait(false);
            _api.Dispose();
        }
        finally
        {
            if (acquired) _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
            if (_writer is not null) await _writer.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer = null;
            _sessionId = null;
            _runtimeLease?.Dispose();
            _runtimeLease = null;
        }
    }

    private static AgentIpcAudioDevice ToIpcDevice(AudioDeviceDescriptor device) => new(
        device.Id,
        device.Name,
        device.IsDefault,
        device.State,
        "Capture",
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

    private static AgentIpcResponse Error(string error, string? detail = null) =>
        new(false, "ERROR", null, error + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $":{detail}"), null);
}

internal sealed class AudioGraphSessionWriter
{
    private const int SampleRate = RecordingContract.MicrophoneSampleRate;
    private const int ChunkSamples = SampleRate * RecordingContract.ChunkDurationSeconds;
    private readonly string _sessionId;
    private readonly string _trackId;
    private readonly SpoolStore _spool;
    private readonly AgentStorageSettings _storage;
    private readonly AudioGraphCaptureEngine _engine;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<bool> _notStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<RawChunkWorkItem> _encoderQueue = Channel.CreateUnbounded<RawChunkWorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
    });
    private AudioFrameDurableConsumer? _consumer;
    private CancellationTokenSource _stop = new();
    private Task? _worker;
    private Task? _encoderWorker;
    private FileStream? _raw;
    private string? _rawPart;
    private string? _rawPath;
    private string? _outputPath;
    private int _sequence;
    private long _startSample;
    private long _sampleCount;
    private long _lastDurabilityCheckpointTimestamp;

    private static readonly long DurabilityCheckpointTicks = Stopwatch.Frequency;

    public AudioGraphSessionWriter(string sessionId, string trackId, SpoolStore spool, AgentStorageSettings storage, AudioGraphCaptureEngine engine, ILogger logger)
    {
        _sessionId = sessionId;
        _trackId = trackId;
        _spool = spool;
        _storage = storage;
        _engine = engine;
        _logger = logger;
    }

    public Task FirstDurableBytes => _consumer?.FirstDurableWrite ?? _notStarted.Task;

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
            _worker = Task.Run(ProcessAsync);
            _encoderWorker = Task.Run(ProcessEncodingAsync);
        }
        catch
        {
            await DisposeRawAsync().ConfigureAwait(false);
            throw;
        }
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
                _encoderQueue.Writer.TryComplete(ex);
                try { await _worker.ConfigureAwait(false); } catch { }
            }
        }
        if (_encoderWorker is not null)
        {
            try { await _encoderWorker.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                failure ??= ex;
                _stop.Cancel();
                _encoderQueue.Writer.TryComplete(ex);
                try { await _encoderWorker.ConfigureAwait(false); } catch { }
            }
        }
        _stop.Dispose();
        if (failure is not null) throw failure;
    }

    private async Task ProcessAsync()
    {
        try
        {
            var consumer = _consumer ?? throw new InvalidOperationException("AUDIO_WRITER_NOT_STARTED");
            await consumer.RunAsync(_engine.Frames, ConsumeFrameAsync, _stop.Token).ConfigureAwait(false);
            if (_sampleCount > 0) await CompleteChunkAsync().ConfigureAwait(false);
            _encoderQueue.Writer.TryComplete();
            if (_encoderWorker is not null) await _encoderWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioGraph local writer failed. Session={SessionId}", _sessionId);
            _encoderQueue.Writer.TryComplete(ex);
            try { await _engine.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
        finally { await DisposeRawAsync().ConfigureAwait(false); }
    }

    private async Task ProcessEncodingAsync()
    {
        try
        {
            await foreach (var work in _encoderQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await EncodeChunkAsync(work).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioGraph chunk encoder failed. Session={SessionId}", _sessionId);
            try { await _engine.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private async Task ConsumeFrameAsync(AudioFrame frame)
    {
        await AppendAsync(frame).ConfigureAwait(false);
        if (_sampleCount >= ChunkSamples) await CompleteChunkAsync().ConfigureAwait(false);
    }

    private async Task AppendAsync(AudioFrame frame)
    {
        await EnsureChunkAsync(frame.StartSample).ConfigureAwait(false);
        var format = frame.Format ?? AudioStreamFormats.Phase1Microphone;
        if (format.SampleRate != SampleRate || format.Channels != 1 || format.SampleType != AudioSampleType.Pcm16)
            throw new InvalidOperationException("AUDIO_FORMAT_UNSUPPORTED");
        await _raw!.WriteAsync(frame.Pcm16Bytes).ConfigureAwait(false);
        _sampleCount += frame.SampleCount;
        if (Stopwatch.GetTimestamp() - _lastDurabilityCheckpointTimestamp >= DurabilityCheckpointTicks)
        {
            await _raw.FlushAsync().ConfigureAwait(false);
            _raw.Flush(flushToDisk: true);
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
        _spool.RegisterRawChunk(new RawRecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, _sequence, _rawPath, _outputPath, _startSample, 0, SampleRate, 1, "room-microphone", "PCM_S16LE", 16, "WRITING", 0, null, null, "PCM_S16", null, 16));
        _raw = new FileStream(_rawPart, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _lastDurabilityCheckpointTimestamp = Stopwatch.GetTimestamp();
        return Task.CompletedTask;
    }

    private async Task CompleteChunkAsync()
    {
        if (_raw is null || _rawPart is null || _rawPath is null || _outputPath is null) return;
        await _raw.FlushAsync().ConfigureAwait(false);
        _raw.Flush(true);
        await _raw.DisposeAsync().ConfigureAwait(false);
        _raw = null;
        File.Move(_rawPart, _rawPath, true);
        var rawSize = new FileInfo(_rawPath).Length;
        var rawSha = FlacEncoder.ComputeSha256(_rawPath);
        await _spool.SetRawChunkStateAsync(_sessionId, _trackId, _sequence, "RAW_READY", rawSize, rawSha, sampleCount: _sampleCount).ConfigureAwait(false);
        var raw = new RawRecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, _sequence, _rawPath, _outputPath, _startSample, _sampleCount, SampleRate, 1, "room-microphone", "PCM_S16LE", 16, "RAW_READY", rawSize, rawSha, null, "PCM_S16", null, 16);
        await _encoderQueue.Writer.WriteAsync(new RawChunkWorkItem(raw), _stop.Token).ConfigureAwait(false);
        _sequence++;
        _rawPart = _rawPath = _outputPath = null;
        _sampleCount = 0;
    }

    private async Task EncodeChunkAsync(RawChunkWorkItem work)
    {
        var raw = work.Chunk;
        var outputPart = raw.OutputPath + ".part";
        FlacEncoder.Encode(RecorderToolPaths.Ffmpeg(), raw.RawPath, outputPart, FlacEncoder.RawFormat(raw));
        File.Move(outputPart, raw.OutputPath, true);
        var size = new FileInfo(raw.OutputPath).Length;
        var sha = FlacEncoder.ComputeSha256(raw.OutputPath);
        await _spool.UpsertChunkAsync(new RecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, raw.Sequence, raw.OutputPath, raw.StartSample, raw.SampleCount, SampleRate, 1, "room-microphone", size, sha, "READY", 0)).ConfigureAwait(false);
        await _spool.SetRawChunkStateAsync(_sessionId, _trackId, raw.Sequence, "READY", size, sha).ConfigureAwait(false);
        File.Delete(raw.RawPath);
    }

    private async Task DisposeRawAsync()
    {
        if (_raw is not null)
        {
            try
            {
                await _raw.FlushAsync().ConfigureAwait(false);
                _raw.Flush(flushToDisk: true);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Unable to checkpoint partial raw chunk. Session={SessionId}", _sessionId); }
            await _raw.DisposeAsync().ConfigureAwait(false);
        }
        _raw = null;
    }
}

internal sealed record RawChunkWorkItem(RawRecordingChunk Chunk);

public sealed class RecorderHostPipeServer : BackgroundService
{
    private readonly RecorderHostRuntime _runtime;
    private readonly ILogger<RecorderHostPipeServer> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private string? _initializationError;

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
        try
        {
            await _runtime.InitializeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _initializationError = ex.Message.Contains("readonly database", StringComparison.OrdinalIgnoreCase)
                ? "SPOOL_READONLY"
                : AudioGraphErrorMapper.Map(ex);
            _logger.LogError(ex, "Recorder Host initialization failed: {ErrorCode}", _initializationError);
            // A failed Host must not retain an apparently live pipe. Desktop
            // can then surface the exact startup code and retry a clean Host.
            Environment.ExitCode = 12;
            throw new InvalidOperationException(_initializationError, ex);
        }
        var connections = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pipe = RecorderHostPipeSecurity.CreateServer();
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

    private async Task<AgentIpcResponse> ExecuteAsync(AgentIpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (_initializationError is not null)
                return new AgentIpcResponse(false, "ERROR", null, _initializationError, null);

            return request.Command.Trim().ToUpperInvariant() switch
            {
                "HEALTH" or "STATUS" or "LIST_AUDIO_DEVICES" => await _runtime.HealthAsync(cancellationToken).ConfigureAwait(false),
                "PREFLIGHT" => await _runtime.PreflightAsync(cancellationToken).ConfigureAwait(false),
                "SHUTDOWN" => await ShutdownAsync(cancellationToken).ConfigureAwait(false),
                "TEST_AUDIO_SOURCE" or "MICROPHONE_TEST" => ReadBool(request.Payload, "systemAudio")
                    ? new AgentIpcResponse(false, "DISABLED", null, "AUDIOGRAPH_SYSTEM_AUDIO_DEFERRED", null)
                    : await _runtime.ProbeAsync(ReadString(request.Payload, "deviceId"), cancellationToken, ReadDurationMs(request.Payload)).ConfigureAwait(false),
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
}

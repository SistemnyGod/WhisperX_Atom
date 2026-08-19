using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

public sealed class AgentPipeHost(
    RecordingCoordinator recorder,
    AgentStateMachine state,
    SpoolStore spool,
    AgentApiClient api,
    AgentStorageSettings storage,
    RecordingDeliveryCoordinator delivery,
    DeviceHealthMonitor deviceHealth,
    RawFinalizerQueueMetrics rawFinalizerMetrics,
    RawEncoderRuntimeState encoderRuntimeState,
    ILogger<AgentPipeHost> logger) : BackgroundService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Task> _finalizations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _finalizationErrors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    // Keep only active IPC tasks. HEALTH polling is frequent and retaining every
    // completed connection task would make service memory grow forever.
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Desktop IPC listening on named pipe {PipeName}", AgentIpcProtocol.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = AgentPipeSecurity.CreateServer();

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                var connection = HandleConnectionAsync(pipe, stoppingToken);
                _connections.TryAdd(connection, 0);
                _ = connection.ContinueWith(
                    completed => _connections.TryRemove(completed, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                await pipe.DisposeAsync();
                logger.LogWarning(ex, "Desktop IPC request failed.");
            }
        }

        try { await Task.WhenAll(_connections.Keys.ToArray()); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) return;
                var request = JsonSerializer.Deserialize<AgentIpcRequest>(line, _json);
                var response = request is null
                    ? Error("invalid_request")
                    : !IsCompatible(request.ProtocolVersion)
                        ? new AgentIpcResponse(false, "ERROR", null, "IPC_VERSION_INCOMPATIBLE", null,
                            ProtocolVersion: AgentIpcProtocol.Version,
                            MinimumSupportedProtocolVersion: AgentIpcProtocol.MinimumSupportedVersion,
                            CurrentProtocolVersion: AgentIpcProtocol.Version)
                        : await ExecuteAsync(request, cancellationToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Desktop IPC connection failed.");
            }
        }
    }

    private async Task<AgentIpcResponse> ExecuteAsync(AgentIpcRequest request, CancellationToken cancellationToken)
    {
        var command = request.Command.Trim().ToUpperInvariant();
        if (!RecorderServiceRuntime.IsActive && IsCaptureOrDeliveryCommand(command))
            return Error("LEGACY_RUNTIME_STANDBY");
        var gated = IsMutatingCommand(command);
        if (gated) await _commandGate.WaitAsync(cancellationToken);
        try
        {
            switch (command)
            {
                case "STATUS":
                    return Status();
                case "HEALTH":
                    return await StatusAsync(cancellationToken);
                case "LIST_AUDIO_DEVICES":
                    return await StatusAsync(cancellationToken);
                case "SUBSCRIBE_AUDIO_DEVICE_EVENTS":
                    // v6 keeps device notifications on the watcher-backed status
                    // channel. A future long-lived subscription can reuse this
                    // command without making HEALTH the device polling source.
                    return await StatusAsync(cancellationToken);
                case "SELECT_AUDIO_DEVICE":
                    await api.SetAudioDevicesAsync(ReadString(request.Payload, "deviceId"), null, cancellationToken);
                    return await StatusAsync(cancellationToken);
                case "TEST_AUDIO_DEVICE":
                    var selectedTestDevice = ReadString(request.Payload, "deviceId");
                    var selectedTest = await recorder.TestAudioSourceAsync(selectedTestDevice, false, TimeSpan.FromSeconds(3), cancellationToken);
                    return new AgentIpcResponse(selectedTest.Success, "IDLE", null, selectedTest.Success ? null : selectedTest.ErrorCode, null, null,
                        null, AgentIpcProtocol.Version, null, null, selectedTest);
                case "PREFLIGHT":
                    return await PreflightAsync(cancellationToken);
                case "GET_SESSION_STATUS":
                    var statusSessionId = ReadString(request.Payload, "sessionId");
                    if (string.IsNullOrWhiteSpace(statusSessionId)) return Error("session_required");
                    return await SessionStatusAsync(statusSessionId, cancellationToken);
                case "CONFIGURE":
                    var serverUrl = ReadString(request.Payload, "serverUrl");
                    var agentToken = ReadString(request.Payload, "token");
                    var agentId = ReadGuid(request.Payload, "agentId");
                    if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(agentToken) || agentId is not Guid configuredAgent)
                        return Error("agent_configuration_invalid");
                    await api.ConfigureAsync(serverUrl, configuredAgent, agentToken, cancellationToken);
                    var configureSessionId = ReadString(request.Payload, "sessionId");
                    if (!string.IsNullOrWhiteSpace(configureSessionId))
                        await spool.UnblockTerminalUploadsAsync(configureSessionId, cancellationToken);
                    var configuredArchiveRoot = ReadString(request.Payload, "archiveRoot");
                    if (!string.IsNullOrWhiteSpace(configuredArchiveRoot)) await api.SetArchiveRootAsync(configuredArchiveRoot, cancellationToken);
                    if (HasProperty(request.Payload, "microphoneDeviceId") || HasProperty(request.Payload, "systemAudioDeviceId"))
                        await api.SetAudioDevicesAsync(ReadString(request.Payload, "microphoneDeviceId"), ReadString(request.Payload, "systemAudioDeviceId"), cancellationToken);
                    return Status();
                case "UPDATE_SERVER_URL":
                    var updatedServerUrl = ReadString(request.Payload, "serverUrl");
                    if (string.IsNullOrWhiteSpace(updatedServerUrl)) return Error("server_url_required");
                    await api.UpdateServerUrlAsync(updatedServerUrl, cancellationToken);
                    var updateSessionId = ReadString(request.Payload, "sessionId");
                    if (!string.IsNullOrWhiteSpace(updateSessionId))
                        await spool.UnblockTerminalUploadsAsync(updateSessionId, cancellationToken);
                    return Status();
                case "SET_ARCHIVE_ROOT":
                    var archiveRoot = ReadString(request.Payload, "archiveRoot");
                    if (string.IsNullOrWhiteSpace(archiveRoot)) return Error("archive_root_required");
                    await api.SetArchiveRootAsync(archiveRoot, cancellationToken);
                    return Status();
                case "SET_AUDIO_DEVICES":
                    var microphoneDeviceId = ReadString(request.Payload, "microphoneDeviceId");
                    var systemAudioDeviceId = ReadString(request.Payload, "systemAudioDeviceId");
                    await api.SetAudioDevicesAsync(microphoneDeviceId, systemAudioDeviceId, cancellationToken);
                    return await StatusAsync(cancellationToken);
                case "SET_RECORDING_PROFILE":
                    if (storage.RecordingProfileManaged) return Error("recording_profile_managed_by_environment");
                    var requestedProfile = ReadString(request.Payload, "recordingProfile");
                    if (string.IsNullOrWhiteSpace(requestedProfile)) return Error("recording_profile_required");
                    if (state.State is RecorderState.Starting or RecorderState.Recording or RecorderState.Paused or RecorderState.Finalizing)
                        return Error("recording_profile_locked");
                    await api.SetRecordingProfileAsync(requestedProfile, cancellationToken);
                    return await StatusAsync(cancellationToken);
                case "TEST_AUDIO_SOURCE":
                case "MICROPHONE_TEST":
                    var testDeviceId = ReadString(request.Payload, "deviceId");
                    var testSystemAudio = request.Payload.TryGetProperty("systemAudio", out var systemValue)
                        && systemValue.ValueKind == JsonValueKind.True;
                    var testSeconds = request.Payload.TryGetProperty("durationSeconds", out var durationValue)
                        && durationValue.ValueKind == JsonValueKind.Number
                        && durationValue.TryGetInt32(out var requestedSeconds)
                        ? Math.Clamp(requestedSeconds, 1, 10)
                        : 3;
                    var test = await recorder.TestAudioSourceAsync(testDeviceId, testSystemAudio, TimeSpan.FromSeconds(testSeconds), cancellationToken);
                    return new AgentIpcResponse(test.Success, "IDLE", null, test.Success ? null : test.ErrorCode, null, null,
                        null, AgentIpcProtocol.Version, null, null, test);
                case "RETRY_UPLOAD":
                    var retrySessionId = ReadString(request.Payload, "sessionId");
                    if (string.IsNullOrWhiteSpace(retrySessionId)) return Error("session_required");
                    if (await spool.GetSessionInfoAsync(retrySessionId, cancellationToken) is null) return Error("session_not_found");
                    await spool.UnblockTerminalUploadsAsync(retrySessionId, cancellationToken);
                    QueueFinalization(retrySessionId);
                    return new AgentIpcResponse(true, RecorderState.Finalizing.ToString(), retrySessionId,
                        null, null, null, null, AgentIpcProtocol.Version, null,
                        await BuildSessionStatusAsync(retrySessionId, cancellationToken));
                case "START":
                    var meetingId = ReadGuid(request.Payload, "meetingId");
                    var ownerUserId = ReadGuid(request.Payload, "ownerUserId");
                    var localOnly = request.Payload.TryGetProperty("localOnly", out var localOnlyValue)
                        && localOnlyValue.ValueKind == JsonValueKind.True;
                    // Offline/local-first capture is deliberately allowed without
                    // a server meeting and without a cached owner. The server
                    // resolves the linked user when the spool is later bound.
                    var title = ReadString(request.Payload, "title");
                    var acousticProfile = ReadAcousticProfile(request.Payload);
                    string sessionId;
                    try { sessionId = await recorder.StartAsync(meetingId, title, cancellationToken, ownerUserId, localOnly, acousticProfile); }
                    catch (Exception exception) { return Error(MapStartError(exception)); }
                    Guid? boundMeetingId = null;
                    try { boundMeetingId = await spool.GetMeetingIdAsync(sessionId, cancellationToken); }
                    catch (Exception exception)
                    {
                        // Capture has already crossed its local start
                        // boundary.  A transient SQLite read failure must not
                        // turn a live recording into a misleading
                        // "Recorder Agent did not answer" error.
                        logger.LogWarning(exception, "Recording started but meeting binding could not be read immediately. Session={SessionId}", sessionId);
                    }
                    return new AgentIpcResponse(true, state.State.ToString(), sessionId, null, null, boundMeetingId);
                case "PAUSE":
                    await recorder.PauseAsync(cancellationToken);
                    return Status();
                case "RESUME":
                    await recorder.ResumeAsync(cancellationToken);
                    return Status();
                case "MARKER":
                    return await RecordEventAsync("MARKER", request.Payload, cancellationToken);
                case "DECISION":
                    return await RecordEventAsync("DECISION", request.Payload, cancellationToken);
                case "ACTION_ITEM":
                    return await RecordEventAsync("ACTION_ITEM", request.Payload, cancellationToken);
                case "VOICE_EVENT":
                    var eventType = ReadString(request.Payload, "eventType") ?? "VOICE_COMMAND";
                    // A START command is emitted before Recorder has a session.
                    // Keep it in the local spool and allow Voice Host to replay
                    // the same event id with the newly returned session id.
                    var eventSessionId = ReadString(request.Payload, "localSessionId");
                    return await RecordEventAsync(eventType, request.Payload, cancellationToken, eventSessionId);
                case "STOP":
                    try
                    {
                        var localSessionId = recorder.SessionId;
                        var stoppedMeetingId = string.IsNullOrWhiteSpace(localSessionId)
                            ? null
                            : await spool.GetMeetingIdAsync(localSessionId, cancellationToken);
                        var stop = await recorder.RequestStopAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(stop.SessionId)) TrackFinalization(stop, stoppedMeetingId);
                        return new AgentIpcResponse(true, RecorderState.Finalizing.ToString(), localSessionId, "server_finalize_pending", null, stoppedMeetingId);
                    }
                    catch (Exception exception) { return Error(MapStopError(exception)); }
                default:
                    return Error("unsupported_command");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Desktop command {Command} failed", request.Command);
            return Error(MapError(ex));
        }
        finally
        {
            if (gated) _commandGate.Release();
        }
    }

    private static bool IsCompatible(int version)
        => version is AgentIpcProtocol.LegacyVersion or AgentIpcProtocol.Version;

    private static bool IsMutatingCommand(string command) => command is
        "CONFIGURE" or "UPDATE_SERVER_URL" or "SET_ARCHIVE_ROOT" or "SET_AUDIO_DEVICES" or "SET_RECORDING_PROFILE" or
        "START" or "PAUSE" or "RESUME" or "STOP" or "RETRY_UPLOAD" or "MARKER" or "DECISION" or
        "ACTION_ITEM" or "VOICE_EVENT";

    private static bool IsCaptureOrDeliveryCommand(string command) => command is
        "START" or "PAUSE" or "RESUME" or "STOP" or "RETRY_UPLOAD" or
        "SET_AUDIO_DEVICES" or "SET_RECORDING_PROFILE" or "TEST_AUDIO_SOURCE" or
        "MICROPHONE_TEST" or "TEST_AUDIO_DEVICE" or "SELECT_AUDIO_DEVICE";

    private void TrackFinalization(RecordingStopHandle stop, Guid? meetingId)
    {
        if (string.IsNullOrWhiteSpace(stop.SessionId)) return;
        _finalizationErrors.TryRemove(stop.SessionId, out _);
        var task = CompleteFinalizationAsync(stop, meetingId);
        _finalizations[stop.SessionId] = task;
        _ = task.ContinueWith(completed => _finalizations.TryRemove(stop.SessionId, out _), TaskScheduler.Default);
    }

    private void QueueFinalization(string sessionId)
    {
        if (_finalizations.ContainsKey(sessionId)) return;
        var completion = new TaskCompletionSource<FinalizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_finalizations.TryAdd(sessionId, completion.Task)) return;
        _ = CompleteQueuedFinalizationAsync(sessionId, completion);
    }

    private async Task CompleteQueuedFinalizationAsync(string sessionId, TaskCompletionSource<FinalizationResult> completion)
    {
        try
        {
            var result = await FinalizeAsync(sessionId, CancellationToken.None);
            if (result.Success) _finalizationErrors.TryRemove(sessionId, out _);
            else _finalizationErrors[sessionId] = result.ErrorCode ?? "server_finalize_pending";
            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Queued recording finalization failed. Session={SessionId}", sessionId);
            _finalizationErrors[sessionId] = "SERVER_FINALIZE_FAILED";
            completion.TrySetResult(new FinalizationResult(false, "DELIVERY", "SERVER_FINALIZE_FAILED", true));
        }
        finally
        {
            _finalizations.TryRemove(sessionId, out _);
        }
    }

    private async Task CompleteFinalizationAsync(RecordingStopHandle stop, Guid? meetingId)
    {
        var stage = "LOCAL_FINALIZATION";
        try
        {
            await spool.SetFinalizationStateAsync(stop.SessionId!, localFinalizeState: "FINALIZING_LOCAL", deliveryState: "NOT_REQUESTED", preserveError: true);
            await stop.LocalFinalization;
            stage = "LOCAL_ARCHIVE";
            var result = await FinalizeAsync(stop.SessionId, CancellationToken.None);
            if (!result.Success)
                _finalizationErrors[stop.SessionId!] = result.ErrorCode ?? "server_finalize_pending";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording finalization failed after local stop. Session={SessionId}, Meeting={MeetingId}", stop.SessionId, meetingId);
            var durability = await spool.GetLocalDurabilityAsync(stop.SessionId!, CancellationToken.None).ConfigureAwait(false);
            var code = durability.State switch
            {
                "LOCAL_READY" => stage == "LOCAL_FINALIZATION" ? "RAW_DURABILITY_FAILED" : "LOCAL_ARCHIVE_FAILED",
                "RECOVERY_PENDING" => "RAW_RECOVERY_PENDING",
                _ => durability.ErrorCode ?? (stage == "LOCAL_FINALIZATION" ? "RAW_DURABILITY_FAILED" : "NO_AUDIO_CAPTURED")
            };
            var detail = durability.State == "RECOVERY_PENDING"
                ? "A non-empty PCM part remains and will be recovered on the next startup/reconciliation pass."
                : ex.Message;
            await spool.SetSessionStateAsync(stop.SessionId!, durability.State == "LOCAL_FAILED" ? "FAILED" : "FINALIZING", CancellationToken.None).ConfigureAwait(false);
            await spool.SetFinalizationStateAsync(
                stop.SessionId!,
                localFinalizeState: durability.State,
                deliveryState: durability.State == "LOCAL_FAILED" ? "NOT_REQUESTED" : "PENDING_SERVER",
                errorCode: code,
                errorDetail: detail,
                retryCount: 0,
                nextRetryAtUtc: durability.State == "RECOVERY_PENDING" ? DateTimeOffset.UtcNow.AddSeconds(5) : null,
                clearNextRetry: durability.State != "RECOVERY_PENDING",
                preserveError: false,
                cancellationToken: CancellationToken.None);
            _finalizationErrors[stop.SessionId!] = code;
        }
    }
    private Task<FinalizationResult> FinalizeAsync(string? localSessionId, CancellationToken cancellationToken)
        => delivery.RunAsync(localSessionId, cancellationToken);

    private AgentIpcResponse Status()
    {
        var visible = VisibleStatus();
        return new AgentIpcResponse(true, visible.State.ToString(), visible.SessionId, visible.Error, null, null, recorder.CurrentMediaTimeMs);
    }

    private async Task<AgentIpcResponse> StatusAsync(CancellationToken cancellationToken)
    {
        var health = deviceHealth.Collect(DataRoot(), storage);
        var watermark = StorageRetentionPolicy.FromEnvironment().Evaluate(health.FreeBytes, health.TotalBytes);
        var pendingUploadSessions = 0;
        var activeSessionId = state.State is RecorderState.Recording or RecorderState.Paused
            ? recorder.SessionId
            : state.State == RecorderState.Starting ? recorder.SessionId : null;
        var backgroundPendingSessions = 0;
        var backgroundFailedSessions = 0;
        try { pendingUploadSessions = await spool.PendingUploadSessionCountAsync(cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "Spool database is not initialized while reporting health."); }
        try
        {
            backgroundPendingSessions = await spool.BackgroundPendingUploadSessionCountAsync(activeSessionId, cancellationToken);
            backgroundFailedSessions = await spool.BackgroundFailedSessionCountAsync(activeSessionId, cancellationToken);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Background delivery counters are not available while reporting health."); }
        var rawBacklog = new RawChunkBacklog(0, 0, 0, 0, 0);
        try { rawBacklog = await spool.GetRawChunkBacklogAsync(cancellationToken: cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "Raw chunk backlog is not available while reporting health."); }
        var encoder = encoderRuntimeState.Snapshot();
        var visible = VisibleStatus();
        RecordingSessionStatus? sessionStatus = null;
        if (!string.IsNullOrWhiteSpace(visible.SessionId))
        {
            try { sessionStatus = await BuildSessionStatusAsync(visible.SessionId!, cancellationToken); }
            catch (Exception ex) { logger.LogDebug(ex, "Session status is not available while reporting Agent health."); }
        }
        var peaks = recorder.CurrentAudioPeaks;
        return new AgentIpcResponse(true, visible.State.ToString(), visible.SessionId, visible.Error, new AgentIpcHealth(
            health.Microphone, health.SystemAudio, health.CaptureDeviceCount, health.RenderDeviceCount, health.FreeBytes, health.TotalBytes, health.Error,
            storage.ArchiveRoot, pendingUploadSessions, health.CaptureDevices, health.RenderDevices,
            storage.MicrophoneDeviceId, storage.SystemAudioDeviceId,
            rawBacklog.Pending, rawBacklog.Writing, rawBacklog.Encoding, rawBacklog.Failed, rawBacklog.Bytes,
            peaks.MicrophonePeak, peaks.SystemAudioPeak, peaks.MicrophoneDb, peaks.SystemAudioDb,
            api.InstallationId, api.AgentId, api.ServerConnectionState, api.LastHeartbeatAtUtc, api.LastServerError,
            peaks.MicrophoneRms, peaks.SystemAudioRms, peaks.MicrophoneRmsDb, peaks.SystemAudioRmsDb,
            peaks.MicrophoneClipping, peaks.SystemAudioClipping, peaks.MicrophoneLastAudioAtUtc, peaks.SystemAudioLastAudioAtUtc,
            peaks.MicrophoneSilenceDurationMs, peaks.SystemAudioSilenceDurationMs,
            peaks.MicrophoneTelemetryStale, peaks.SystemAudioTelemetryStale, rawBacklog.Health,
            activeSessionId, backgroundPendingSessions, backgroundFailedSessions,
            rawBacklog.Ready, rawBacklog.ReadyForUpload, watermark.State.ToString(), watermark.FreePercent, watermark.Reason,
            storage.RecordingProfile, storage.RecordingProfileManaged,
            recorder.LastMicrophoneProbe, recorder.LastSystemAudioProbe,
            recorder.GetCaptureReadiness(storage.RecordingProfile).Microphone,
            recorder.GetCaptureReadiness(storage.RecordingProfile).SystemAudio,
            AudioDeviceProbe.State(recorder.LastMicrophoneProbe),
            AudioDeviceProbe.State(recorder.LastSystemAudioProbe),
            EncoderState: encoder.State,
            RawTerminalFailedCount: rawBacklog.TerminalFailed,
            EncoderLastHeartbeatAtUtc: encoder.LastHeartbeatAtUtc,
            EncoderCurrentChunkId: encoder.CurrentChunkId,
            EncoderLastSuccessAtUtc: encoder.LastSuccessAtUtc,
            EncoderLastErrorCode: encoder.LastErrorCode,
            EncoderQueueDepth: encoder.QueueDepth), null, recorder.CurrentMediaTimeMs,
            AgentIpcProtocol.Version, null, sessionStatus);
    }

    private async Task<AgentIpcResponse> PreflightAsync(CancellationToken cancellationToken)
    {
        var health = deviceHealth.Collect(DataRoot(), storage);
        var watermark = StorageRetentionPolicy.FromEnvironment().Evaluate(health.FreeBytes, health.TotalBytes);
        var warnings = new List<string>();
        var errors = new List<string>();
        var ffmpeg = File.Exists(RecorderToolPaths.Ffmpeg());
        var ffprobe = File.Exists(RecorderToolPaths.Ffprobe());
        var archive = true;
        var spoolReady = true;
        try { recorder.ValidatePreflight(); }
        catch (Exception ex)
        {
            // ValidatePreflight covers storage and device-independent local
            // prerequisites. FFmpeg availability is reported separately and
            // is intentionally only a warning: raw PCM capture can start
            // while the singleton encoder waits for the tools to return.
            archive = !ex.Message.Contains("archive", StringComparison.OrdinalIgnoreCase);
            errors.Add(ex.Message);
        }
        if (!ffmpeg || !ffprobe) warnings.Add("LOCAL_ENCODER_UNAVAILABLE");
        try { _ = await spool.PendingUploadSessionCountAsync(cancellationToken); }
        catch (Exception ex) { spoolReady = false; errors.Add("spool_unavailable"); logger.LogDebug(ex, "Recorder spool preflight failed."); }
        if (!health.Microphone && !health.SystemAudio) errors.Add("no_audio_source_available");
        if (!string.IsNullOrWhiteSpace(health.Error)) warnings.Add(health.Error);
        if (!api.IsConfigured) warnings.Add("backend_not_configured_recording_can_start_offline");
        var captureReadiness = recorder.GetCaptureReadiness(storage.RecordingProfile);
        if (!captureReadiness.Microphone && storage.RecordingProfile is not "SYSTEM_ONLY")
            errors.Add(recorder.LastMicrophoneProbe?.ErrorCode ?? "microphone_probe_required");
        if (!captureReadiness.SystemAudio && storage.RecordingProfile is "ONLINE" or "SYSTEM_ONLY")
            errors.Add(recorder.LastSystemAudioProbe?.ErrorCode ?? "system_audio_probe_required");
        var minimumBytes = watermark.BlockFreeBytes;
        var requiredSourceReady = storage.RecordingProfile switch
        {
            "SYSTEM_ONLY" => captureReadiness.SystemAudio,
            "ONLINE" => captureReadiness.Microphone && captureReadiness.SystemAudio,
            _ => captureReadiness.Microphone
        };
        var ready = requiredSourceReady && archive && spoolReady && watermark.AllowsRecording;
        return new AgentIpcResponse(true, state.State.ToString(), recorder.SessionId, null, null, null, recorder.CurrentMediaTimeMs,
            AgentIpcProtocol.Version, new AgentPreflightResult(ready, captureReadiness.Microphone, captureReadiness.SystemAudio, ffmpeg && ffprobe, spoolReady, archive,
                health.FreeBytes, minimumBytes, api.ServerConnectionState, warnings, errors, watermark.State.ToString(), watermark.FreePercent, watermark.Reason,
                CaptureReady: ready,
                EncodingReady: ffmpeg && ffprobe,
                DeliveryReady: api.IsConfigured && string.Equals(api.ServerConnectionState, "CONNECTED", StringComparison.OrdinalIgnoreCase),
                Ffprobe: ffprobe));
    }

    private async Task<AgentIpcResponse> SessionStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        var status = await BuildSessionStatusAsync(sessionId, cancellationToken);
        if (status is null) return Error("session_not_found");
        return new AgentIpcResponse(true, status.CaptureState, sessionId, status.ErrorCode ?? status.Error, null, status.MeetingId, recorder.CurrentMediaTimeMs,
            AgentIpcProtocol.Version, null, status);
    }

    private async Task<RecordingSessionStatus?> BuildSessionStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        var info = await spool.GetSessionInfoAsync(sessionId, cancellationToken);
        if (info is null) return null;
        var counts = await spool.GetChunkCountsAsync(sessionId, cancellationToken);
        var metrics = await spool.GetChunkDeliveryMetricsAsync(sessionId, cancellationToken);
        var rawBacklog = await spool.GetRawChunkBacklogAsync(sessionId, cancellationToken);
        rawBacklog = rawBacklog with
        {
            FinalizerQueueDepth = rawFinalizerMetrics.Depth,
            FinalizerMaximumDepth = rawFinalizerMetrics.MaximumDepth,
            FinalizerCapacity = rawFinalizerMetrics.Capacity
        };
        var serverSessionId = await spool.GetServerSessionIdAsync(sessionId, cancellationToken);
        var capture = string.Equals(recorder.SessionId, sessionId, StringComparison.Ordinal)
            ? state.State.ToString().ToUpperInvariant()
            : info.LocalFinalizeState switch
            {
                "FINALIZING_LOCAL" => "FINALIZING_LOCAL",
                "LOCAL_READY" => "LOCAL_READY",
                "RECOVERY_PENDING" => "RECOVERY_PENDING",
                "LOCAL_FAILED" => "LOCAL_FAILED",
                _ => info.State
            };
        // DeliveryState is persisted truth. Do not infer CONFIRMED from a
        // successful request: server media assembly may still be pending.
        var delivery = string.IsNullOrWhiteSpace(info.DeliveryState) ? "NOT_STARTED" : info.DeliveryState;
        var errorCode = _finalizationErrors.TryGetValue(sessionId, out var finalizationError)
            ? finalizationError
            : rawBacklog.LastErrorCode ?? info.ErrorCode;
        var error = errorCode is null ? null : SafeErrorText(errorCode);
        var encodingState = rawBacklog.TerminalFailed > 0
            ? "TERMINAL_FAILED"
            : rawBacklog.Pending > 0
             ? rawBacklog.Failed > 0 && rawBacklog.Encoding == 0 && rawBacklog.Ready == 0
                 ? (string.IsNullOrWhiteSpace(rawBacklog.LastErrorCode) ? "WAITING_FOR_ENCODER" : "ENCODE_FAILED")
                 : "ENCODING"
            : rawBacklog.ReadyForUpload > 0 ? "FLAC_READY" : "IDLE";
        var archiveState = info.LocalFinalizeState == "LOCAL_FAILED"
            ? "FAILED"
            : string.IsNullOrWhiteSpace(info.ArchivePath)
                ? info.LocalFinalizeState == "LOCAL_READY" ? "PENDING" : "NOT_STARTED"
                : "READY";
        return new RecordingSessionStatus(sessionId, info.MeetingId, capture, delivery,
            counts.Total, counts.Confirmed, counts.Pending, error, serverSessionId,
            info.LocalFinalizeState, info.ArchivePath, errorCode, IsRetryableCode(errorCode), info.NextRetryAtUtc,
            info.MediaAssetId, info.ProcessingJobId, info.TraceId,
            metrics.Ready, metrics.Uploading, metrics.Failed, metrics.BytesPending, metrics.OldestPendingAgeSeconds,
            encodingState, archiveState,
            rawBacklog.Total, rawBacklog.Writing, rawBacklog.Ready, rawBacklog.Encoding, rawBacklog.Completed,
            rawBacklog.Failed, rawBacklog.Bytes,
            rawBacklog.OldestPendingAgeMs is null ? null : rawBacklog.OldestPendingAgeMs.Value / 1000d,
            rawBacklog.Health, rawBacklog.FinalizerQueueDepth, rawBacklog.FinalizerMaximumDepth, rawBacklog.FinalizerCapacity,
            archiveState == "FAILED" ? errorCode : null,
            archiveState == "FAILED" ? error : null,
            RawTerminalFailedCount: rawBacklog.TerminalFailed);
    }

    private static bool IsRetryableCode(string? code) => code is "SERVER_UNAVAILABLE" or "SERVER_FINALIZE_REJECTED" or "SERVER_CHUNKS_MISSING" or "recording_chunks_incomplete" or "CHUNK_UPLOAD_FAILED" or "RAW_RECOVERY_PENDING" or "LOCAL_ENCODER_UNAVAILABLE" or "ENCODER_FAILED" or "FLAC_VALIDATION_FAILED" or "FFMPEG_ENCODE_FAILED" or "ENCODER_TIMEOUT";

    private static string SafeErrorText(string code) => code switch
    {
        "AUDIO_SOURCE_FAILED" => "Источник аудио остановился. Проверьте устройство; сохранённые данные останутся доступны для восстановления.",
        "STORAGE_WRITE_FAILED" => "Не удалось сохранить аудио на диск. Запись остановлена, исходные данные сохранены насколько это возможно.",
        "RAW_DURABILITY_FAILED" => "Не удалось подтвердить сохранение исходного PCM. Запись не считается сохранённой; проверьте локальное хранилище.",
        "ENCODER_FAILED" => "Не удалось закодировать аудиочанк. Запись остановлена, локальные данные сохранены для восстановления.",
        "ENCODER_RETRY_EXHAUSTED" => "Кодировщик несколько раз не смог обработать аудиочанк. Исходный PCM сохранён; проверьте FFmpeg и журнал Recorder.",
        "FLAC_VALIDATION_FAILED" or "FFMPEG_ENCODE_FAILED" or "ENCODER_TIMEOUT" => "Кодирование аудиочанка временно не завершилось. Исходный PCM сохранён, повторная попытка будет выполнена автоматически.",
        "LOCAL_ENCODING_FAILED" or "LOCAL_ARCHIVE_FAILED" => "Не удалось собрать локальный master-файл. Исходные аудиочанки сохранены.",
        "LOCAL_CHUNK_MISSING" or "LOCAL_CHUNK_INVALID" => "Локальные аудиочанки неполные или повреждены. Исходные файлы сохранены для диагностики.",
        "SERVER_UNAVAILABLE" or "CHUNK_UPLOAD_FAILED" => "Запись сохранена локально. Сервер пока не подтвердил получение. Повторная отправка выполняется автоматически.",
        "SERVER_FINALIZE_REJECTED" => "Запись сохранена локально. Сервер не подтвердил завершение, повторная отправка выполняется автоматически.",
        "SERVER_CHUNKS_MISSING" or "recording_chunks_incomplete" => "Серверу не хватает частей записи. Выполняется повторная отправка.",
        "RAW_RECOVERY_PENDING" => "Локальный PCM-файл не закрыт. Agent восстановит его автоматически.",
        "RAW_DURABILITY_FAILED" or "RAW_FINALIZER_BACKLOG_EXCEEDED" => "Не удалось подтвердить закрытие локального PCM. Исходные данные оставлены для recovery.",
        "NO_AUDIO_CAPTURED" or "AUDIO_CAPTURE_START_FAILED" => "Не удалось подтвердить сохранённый аудиопоток. Запись не считается сохранённой.",
        "REQUIRES_MANUAL_REPAIR" or "RAW_SOURCE_MISSING" or "RAW_SOURCE_EMPTY" or "RAW_TIMELINE_INVALID" or "RAW_CHECKSUM_MISMATCH" or "UNSUPPORTED_AUDIO_FORMAT" => "Кодировщик не смог обработать исходный PCM. Требуется проверка FFmpeg или ручное восстановление.",
        "recording_timeline_inconsistent" => "Сервер отклонил временную шкалу записи. Исходные части сохранены для диагностики.",
        "chunk_checksum_mismatch" => "Сервер отклонил повреждённую часть записи. Исходные файлы сохранены для диагностики.",
        "AGENT_AUTH_REJECTED" => "Сервер отклонил авторизацию Agent. Переподключите Agent в настройках.",
        _ => "Запись сохранена локально, но требует повторной отправки."
    };

    private (RecorderState State, string? SessionId, string? Error) VisibleStatus()
    {
        // The active capture is authoritative for the primary status. A
        // previous session waiting for delivery must not turn a live recording
        // into a red "recording error" in Desktop.
        if ((state.State is RecorderState.Recording or RecorderState.Paused || state.State == RecorderState.Starting)
            && !string.IsNullOrWhiteSpace(recorder.SessionId))
            return (state.State, recorder.SessionId, null);

        var finalizingSession = _finalizations.Keys.OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(finalizingSession))
            return (RecorderState.Finalizing, finalizingSession, "server_finalize_pending");

        var failedSession = _finalizationErrors.Keys.OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(failedSession) && _finalizationErrors.TryGetValue(failedSession, out var error))
            return (RecorderState.Error, failedSession, error);

        return (state.State, recorder.SessionId, null);
    }

    private async Task<AgentIpcResponse> RecordEventAsync(string eventType, JsonElement payload, CancellationToken cancellationToken, string? targetSessionId = null)
    {
        var eventId = ReadEventId(payload) ?? Guid.NewGuid().ToString("N");
        var payloadJson = JsonSerializer.Serialize(payload);
        if (HasBoolean(payload, "discardPending") || HasNestedBoolean(payload, "discardPending"))
        {
            await spool.RemovePendingEventAsync(eventId, cancellationToken);
            return new AgentIpcResponse(true, RecorderState.Idle.ToString(), null, null, null,
                MediaTimeMs: null, ErrorDetail: "VOICE_EVENT_DISCARDED");
        }
        var sessionId = string.IsNullOrWhiteSpace(targetSessionId) ? recorder.SessionId : targetSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            if (string.Equals(eventType, "VOICE_COMMAND", StringComparison.OrdinalIgnoreCase)
                && IsStartVoiceCommand(payload))
            {
                await spool.AddPendingEventAsync(eventId, eventType, payloadJson, cancellationToken: cancellationToken);
                return new AgentIpcResponse(true, RecorderState.Idle.ToString(), null, null, null,
                    MediaTimeMs: null, ErrorDetail: "VOICE_EVENT_PENDING_SESSION");
            }
            return Error("recording_not_active");
        }
        if (await spool.GetSessionInfoAsync(sessionId, cancellationToken) is null)
            return Error("session_not_found");
        var mediaTimeMs = string.Equals(sessionId, recorder.SessionId, StringComparison.Ordinal)
            ? recorder.CurrentMediaTimeMs ?? 0
            : await spool.GetSessionMediaTimeMsAsync(sessionId, cancellationToken) ?? 0;
        await spool.AddEventAsync(sessionId, eventType, mediaTimeMs, payloadJson, cancellationToken, eventId);
        await spool.RemovePendingEventAsync(eventId, cancellationToken);
        var meetingId = await spool.GetMeetingIdAsync(sessionId, cancellationToken);
        return new AgentIpcResponse(true, state.State.ToString(), sessionId, null, null, meetingId, mediaTimeMs);
    }

    private static string? ReadEventId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("eventId", out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
        if (payload.TryGetProperty("payload", out var nested)) return ReadEventId(nested);
        return null;
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

    private static bool HasNestedBoolean(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("payload", out var nested)
        && HasBoolean(nested, name);

    private static bool HasBoolean(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static AgentIpcResponse Error(string error) => new(false, RecorderState.Idle.ToString(), null, error, null);

    private static string MapError(Exception exception)
    {
        var message = exception.ToString();
        foreach (var code in new[]
        {
            "AUDIO_CAPTURE_BUSY", "AUDIO_CAPTURE_START_FAILED", "AUDIO_CALLBACK_TIMEOUT",
            "AUDIO_SOURCE_FAILED", "AUDIO_DEVICE_UNAVAILABLE", "AUDIO_DEVICE_NOT_FOUND",
            "AUDIO_DEVICE_ACCESS_DENIED", "AUDIO_INPUT_NODE_CREATE_FAILED",
            "AUDIO_GRAPH_CREATE_FAILED", "AUDIO_PIPELINE_OVERRUN", "AUDIO_BUFFER_FORMAT_MISMATCH",
            "RAW_FINALIZER_BACKLOG_EXCEEDED", "RAW_DURABILITY_FAILED", "RAW_RECOVERY_PENDING"
        })
        {
            if (message.Contains(code, StringComparison.OrdinalIgnoreCase)) return code;
        }
        if (message.Contains("no_audio_source_available", StringComparison.OrdinalIgnoreCase)) return "AUDIO_CAPTURE_START_FAILED";
        if (exception is InvalidOperationException && exception.Message.Contains("Cannot pause", StringComparison.OrdinalIgnoreCase)) return "recording_cannot_pause";
        if (exception is InvalidOperationException && exception.Message.Contains("Cannot resume", StringComparison.OrdinalIgnoreCase)) return "recording_cannot_resume";
        if (exception is InvalidOperationException && exception.Message.Contains("already", StringComparison.OrdinalIgnoreCase)) return "recording_already_active";
        if (message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) return "LOCAL_ENCODER_UNAVAILABLE";
        if (message.Contains("storage", StringComparison.OrdinalIgnoreCase)) return "recording_storage_unavailable";
        if (exception is UnauthorizedAccessException) return "recording_archive_access_denied";
        if (exception is FileNotFoundException or DirectoryNotFoundException or PathTooLongException) return "recording_archive_path_unavailable";
        return "recorder_command_failed";
    }

    private static string MapStartError(Exception exception)
    {
        var mapped = MapError(exception);
        return mapped == "recorder_command_failed" ? "AUDIO_CAPTURE_START_FAILED" : mapped;
    }

    private static string MapStopError(Exception exception)
    {
        var mapped = MapError(exception);
        return mapped == "recorder_command_failed" ? "RAW_DURABILITY_FAILED" : mapped;
    }

    private static Guid? ReadGuid(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string ReadAcousticProfile(JsonElement payload)
    {
        var value = ReadString(payload, "acousticProfile")?.Trim().ToUpperInvariant();
        return value is "STANDARD" or "LARGE_ROOM" ? value : "AUTO";
    }

    private static bool HasProperty(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out _);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var pending = _finalizations.Values.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            catch (TimeoutException) { logger.LogWarning("Timed out waiting for {Count} recording finalizations during service shutdown.", pending.Length); }
        }
        await base.StopAsync(cancellationToken);
        _commandGate.Dispose();
    }
    private static string DataRoot() => Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
}

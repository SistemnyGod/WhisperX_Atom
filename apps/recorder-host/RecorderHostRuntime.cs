using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _spool.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _recovery.RecoverAsync(null, cancellationToken).ConfigureAwait(false);
        await _engine.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Device IDs are opaque endpoint identities. A legacy ID is migrated
        // only on an exact DeviceInformation.Id match; friendly-name matching
        // is intentionally forbidden. The user must reselect when it vanished.
        if (!string.IsNullOrWhiteSpace(_storage.MicrophoneDeviceId)
            && _engine.DeviceCatalog.Resolve(AudioSelectionMode.Fixed, _storage.MicrophoneDeviceId) is null)
        {
            _storage.SetAudioDevices(null, _storage.SystemAudioDeviceId);
            _storage.SetUserReselectRequired(true);
            await _api.SetAudioDevicesAsync(null, _storage.SystemAudioDeviceId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<AgentIpcResponse> HealthAsync(CancellationToken cancellationToken = default)
    {
        await _engine.DeviceCatalog.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var devices = _engine.DeviceCatalog.Devices.Select(ToIpcDevice).ToArray();
        var archiveRoot = _storage.ArchiveRoot;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(archiveRoot))!);
        var telemetry = _engine.Telemetry;
        var ready = _engine.SelectedDevice is not null && _engine.State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed;
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
            CaptureEngine: "AUDIOGRAPH",
            RecorderProcessModel: "CURRENT_USER_HOST",
            DeviceWatcherReady: _engine.DeviceCatalog.IsReady,
            AudioGraphReady: _engine.DeviceCatalog.IsReady && _engine.SelectedDevice is not null && _engine.State is not AudioCaptureState.DeviceLost and not AudioCaptureState.Failed,
            FirstFrameConfirmed: telemetry.FrameCount > 0);
        return new AgentIpcResponse(!systemAudioDeferred, _engine.State.ToString(), _sessionId,
            systemAudioDeferred ? "AUDIO_SYSTEM_AUDIO_DEFERRED" : null, health);
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
            var selected = _engine.SelectedDevice ?? _engine.DeviceCatalog.Resolve(AudioSelectionMode.Default, null)
                ?? throw new InvalidOperationException("AUDIO_DEVICE_NOT_FOUND");
            await _spool.CreateSessionAsync(sessionId, meetingId, title ?? $"Совещание {DateTime.Now:dd.MM.yyyy HH:mm}", Guid.NewGuid().ToString("N"), cancellationToken, ownerUserId, localOnly).ConfigureAwait(false);
            var trackId = "room-microphone";
            await _spool.UpsertTrackInfoAsync(new RecordingTrackInfo(
                trackId,
                "room-microphone",
                SampleRate,
                1,
                selected.Id,
                selected.Name,
                _storage.MicrophoneDeviceId is null ? "DEFAULT" : "FIXED",
                _storage.RecordingProfile,
                "Pcm16",
                16,
                "PCM_S16",
                null,
                16), sessionId, cancellationToken).ConfigureAwait(false);
            await _spool.AddEventAsync(sessionId, "RECORDING_REQUESTED", cancellationToken: cancellationToken).ConfigureAwait(false);

            var writer = new AudioGraphSessionWriter(sessionId, trackId, _spool, _storage, _engine, _logger);
            _writer = writer;
            _sessionId = sessionId;
            writer.Start();
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
            var result = await _delivery.RunAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var status = await GetSessionStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return new AgentIpcResponse(result.Success, result.Stage, sessionId, result.ErrorCode, null, status.MeetingId, null, AgentIpcProtocol.Version, null, status);
        }
        catch (Exception ex) { return Error(AudioGraphErrorMapper.Map(ex), ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<AgentIpcResponse> ProbeAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var mode = string.IsNullOrWhiteSpace(deviceId) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed;
        var result = await _engine.ProbeAsync(mode, deviceId, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
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
        var mode = string.IsNullOrWhiteSpace(deviceId) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed;
        try
        {
            var probe = await _engine.ProbeAsync(mode, deviceId, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (!probe.Ready)
                return Error(probe.ErrorCode ?? (mode == AudioSelectionMode.Fixed ? "SELECTED_DEVICE_UNAVAILABLE" : "AUDIO_DEVICE_NOT_READY"), probe.ErrorDetail);

            _storage.SetAudioDevices(mode == AudioSelectionMode.Fixed ? deviceId : null, null);
            _storage.SetUserReselectRequired(false);
            await _api.SetAudioDevicesAsync(mode == AudioSelectionMode.Fixed ? deviceId : null, null, cancellationToken).ConfigureAwait(false);
            return await HealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await _engine.SelectDeviceAsync(
                    string.IsNullOrWhiteSpace(previous) ? AudioSelectionMode.Default : AudioSelectionMode.Fixed,
                    previous,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            return Error(AudioGraphErrorMapper.Map(ex), ex.Message);
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
        _storage.SetAudioDevices(microphoneDeviceId, systemAudioDeviceId);
        await _api.ConfigureAsync(serverUrl, agentId, token, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(microphoneDeviceId))
            await SelectDeviceAsync(microphoneDeviceId, cancellationToken).ConfigureAwait(false);
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
        await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_writer is not null) await _writer.StopAsync(cancellationToken).ConfigureAwait(false);
        _writer = null;
        _sessionId = null;
        _runtimeLease?.Dispose();
        _runtimeLease = null;
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
    private AudioFrameDurableConsumer? _consumer;
    private CancellationTokenSource _stop = new();
    private Task? _worker;
    private FileStream? _raw;
    private string? _rawPart;
    private string? _rawPath;
    private string? _outputPath;
    private int _sequence;
    private long _startSample;
    private long _sampleCount;

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

    public void Start()
    {
        _consumer = new AudioFrameDurableConsumer();
        _worker = Task.Run(ProcessAsync);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null)
        {
            try
            {
                // AudioGraph.StopAsync completes the frame channel first. Drain
                // it before cancelling so the final partial chunk is durable.
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _stop.Cancel();
                try { await _worker.ConfigureAwait(false); } catch { }
                throw;
            }
        }
        _stop.Dispose();
    }

    private async Task ProcessAsync()
    {
        try
        {
            var consumer = _consumer ?? throw new InvalidOperationException("AUDIO_WRITER_NOT_STARTED");
            await consumer.RunAsync(_engine.Frames, ConsumeFrameAsync, _stop.Token).ConfigureAwait(false);
            if (_sampleCount > 0) await CompleteChunkAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioGraph local writer failed. Session={SessionId}", _sessionId);
        }
        finally { await DisposeRawAsync().ConfigureAwait(false); }
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
        await _raw.FlushAsync().ConfigureAwait(false);
        _raw.Flush(flushToDisk: true);
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
        _spool.RegisterRawChunk(new RawRecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, _sequence, _rawPath, _outputPath, _startSample, 0, SampleRate, 1, "room-microphone", "Pcm16", 16, "WRITING", 0, null, null, "PCM_S16", null, 16));
        _raw = new FileStream(_rawPart, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
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
        var raw = new RawRecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, _sequence, _rawPath, _outputPath, _startSample, _sampleCount, SampleRate, 1, "room-microphone", "Pcm16", 16, "RAW_READY", rawSize, rawSha, null, "PCM_S16", null, 16);
        var outputPart = _outputPath + ".part";
        FlacEncoder.Encode(RecorderToolPaths.Ffmpeg(), _rawPath, outputPart, FlacEncoder.RawFormat(raw));
        File.Move(outputPart, _outputPath, true);
        var size = new FileInfo(_outputPath).Length;
        var sha = FlacEncoder.ComputeSha256(_outputPath);
        await _spool.UpsertChunkAsync(new RecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, _sequence, _outputPath, _startSample, _sampleCount, SampleRate, 1, "room-microphone", size, sha, "READY", 0)).ConfigureAwait(false);
        await _spool.SetRawChunkStateAsync(_sessionId, _trackId, _sequence, "READY", size, sha).ConfigureAwait(false);
        File.Delete(_rawPath);
        _sequence++;
        _rawPart = _rawPath = _outputPath = null;
        _sampleCount = 0;
    }

    private async Task DisposeRawAsync()
    {
        if (_raw is not null) await _raw.DisposeAsync().ConfigureAwait(false);
        _raw = null;
    }
}

public sealed class RecorderHostPipeServer : BackgroundService
{
    private readonly RecorderHostRuntime _runtime;
    private readonly ILogger<RecorderHostPipeServer> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private string? _initializationError;

    public RecorderHostPipeServer(RecorderHostRuntime runtime, ILogger<RecorderHostPipeServer> logger)
    {
        _runtime = runtime;
        _logger = logger;
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
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = RecorderHostPipeSecurity.CreateServer();
            await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            await HandleAsync(pipe, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line)) return;
        var request = JsonSerializer.Deserialize<AgentIpcRequest>(line, _json);
            var response = request is null
                ? new AgentIpcResponse(false, "ERROR", null, "invalid_request", null)
                : request.ProtocolVersion != AgentIpcProtocol.Version
                ? new AgentIpcResponse(false, "ERROR", null, "IPC_VERSION_INCOMPATIBLE", null,
                    ProtocolVersion: AgentIpcProtocol.Version,
                    MinimumSupportedProtocolVersion: AgentIpcProtocol.Version,
                    CurrentProtocolVersion: AgentIpcProtocol.Version)
                : await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json)).ConfigureAwait(false);
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
                "SUBSCRIBE_AUDIO_DEVICE_EVENTS" => await _runtime.HealthAsync(cancellationToken).ConfigureAwait(false),
                "PREFLIGHT" => await _runtime.HealthAsync(cancellationToken).ConfigureAwait(false),
                "TEST_AUDIO_SOURCE" or "MICROPHONE_TEST" => ReadBool(request.Payload, "systemAudio")
                    ? new AgentIpcResponse(false, "DISABLED", null, "AUDIOGRAPH_SYSTEM_AUDIO_DEFERRED", null)
                    : await _runtime.ProbeAsync(ReadString(request.Payload, "deviceId"), cancellationToken).ConfigureAwait(false),
                "SET_AUDIO_DEVICES" or "SELECT_AUDIO_DEVICE" => await _runtime.SetAudioDevicesAsync(
                    ReadString(request.Payload, "microphoneDeviceId") ?? ReadString(request.Payload, "deviceId"),
                    ReadString(request.Payload, "systemAudioDeviceId"), cancellationToken).ConfigureAwait(false),
                "TEST_AUDIO_DEVICE" => await _runtime.ProbeAsync(ReadString(request.Payload, "deviceId"), cancellationToken).ConfigureAwait(false),
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
}

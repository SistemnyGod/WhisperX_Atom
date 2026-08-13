using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperX.Atom.Recorder;

public sealed record RecordingStopHandle(string? SessionId, Task LocalFinalization);
public sealed record AudioPeakSnapshot(
    double? MicrophonePeak,
    double? SystemAudioPeak,
    double? MicrophoneDb,
    double? SystemAudioDb,
    double? MicrophoneRms = null,
    double? SystemAudioRms = null,
    double? MicrophoneRmsDb = null,
    double? SystemAudioRmsDb = null,
    bool? MicrophoneClipping = null,
    bool? SystemAudioClipping = null,
    DateTimeOffset? MicrophoneLastAudioAtUtc = null,
    DateTimeOffset? SystemAudioLastAudioAtUtc = null,
    long? MicrophoneSilenceDurationMs = null,
    long? SystemAudioSilenceDurationMs = null,
    bool MicrophoneTelemetryStale = true,
    bool SystemAudioTelemetryStale = true);

public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly SpoolStore _spool;
    private readonly AgentStorageSettings _storage;
    private readonly AgentStateMachine _state;
    private readonly ILogger<RecordingCoordinator> _logger;
    private readonly string _dataRoot;
    private readonly string _ffmpegPath;
    private readonly object _gate = new();
    private CaptureTrack? _microphone;
    private CaptureTrack? _systemAudio;
    private string? _sessionId;
    private int _fatalCaptureFailureStarted;
    private AudioSourceTestResult? _lastMicrophoneProbe;
    private AudioSourceTestResult? _lastSystemAudioProbe;

    public RecordingCoordinator(SpoolStore spool, AgentStateMachine state, AgentStorageSettings storage, ILogger<RecordingCoordinator> logger)
    {
        _spool = spool;
        _state = state;
        _storage = storage;
        _logger = logger;
        _dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        _ffmpegPath = Environment.GetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH") ?? "ffmpeg";
    }

    public string? SessionId => _sessionId;

    public AudioSourceTestResult? LastMicrophoneProbe => Volatile.Read(ref _lastMicrophoneProbe);
    public AudioSourceTestResult? LastSystemAudioProbe => Volatile.Read(ref _lastSystemAudioProbe);

    public (bool Microphone, bool SystemAudio, string? Error) GetCaptureReadiness(string profile)
    {
        var microphone = AudioDeviceProbe.IsReady(LastMicrophoneProbe, _storage.MicrophoneDeviceId);
        var systemAudio = AudioDeviceProbe.IsReady(LastSystemAudioProbe, _storage.SystemAudioDeviceId);
        return profile switch
        {
            "SYSTEM_ONLY" => (false, systemAudio, systemAudio ? null : "system_audio_probe_required"),
            "ONLINE" => (microphone, systemAudio, microphone && systemAudio ? null : "audio_source_probe_required"),
            _ => (microphone, false, microphone ? null : "microphone_probe_required")
        };
    }

    public long? CurrentMediaTimeMs
    {
        get
        {
            lock (_gate)
            {
                var values = new[] { _microphone?.MediaTimeMs, _systemAudio?.MediaTimeMs }.Where(value => value is not null).Select(value => value!.Value).ToArray();
                return values.Length == 0 ? null : values.Max();
            }
        }
    }

    public IReadOnlyList<RecordingTrackInfo> ActiveTracks
    {
        get
        {
            lock (_gate)
            {
                var tracks = new List<RecordingTrackInfo>();
                if (_microphone is not null) tracks.Add(_microphone.Info);
                if (_systemAudio is not null) tracks.Add(_systemAudio.Info);
                return tracks;
            }
        }
    }

    public AudioPeakSnapshot CurrentAudioPeaks
    {
        get
        {
            lock (_gate)
            {
                return new AudioPeakSnapshot(
                    _microphone?.Peak,
                    _systemAudio?.Peak,
                    _microphone?.PeakDb,
                    _systemAudio?.PeakDb,
                    _microphone?.Rms,
                    _systemAudio?.Rms,
                    _microphone?.RmsDb,
                    _systemAudio?.RmsDb,
                    _microphone?.Clipping,
                    _systemAudio?.Clipping,
                    _microphone?.LastAudioAtUtc,
                    _systemAudio?.LastAudioAtUtc,
                    _microphone?.SilenceDurationMs,
                    _systemAudio?.SilenceDurationMs,
                    _microphone?.TelemetryStale ?? true,
                    _systemAudio?.TelemetryStale ?? true);
            }
        }
    }

    public async Task<string> StartAsync(Guid? meetingId = null, string? title = null, CancellationToken cancellationToken = default, Guid? ownerUserId = null)
    {
        EnsureStorageAvailable();
        EnsureFfmpegAvailable();
        lock (_gate)
        {
            if (_state.State is RecorderState.Recording or RecorderState.Paused)
                throw new InvalidOperationException("Recording is already active.");
            if (!_state.TryTransition(RecorderState.Starting, "capture-start-requested"))
                throw new InvalidOperationException($"Cannot start recording from {_state.State}.");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var pipelineCorrelationId = Guid.NewGuid().ToString("N");
        try
        {
            // Offline sessions deliberately keep meeting_id NULL. The server meeting is
            // created later by BindSessionAsync and persisted back into the spool.
            await _spool.CreateSessionAsync(sessionId, meetingId, title ?? $"Совещание {DateTime.Now:dd.MM.yyyy HH:mm}", pipelineCorrelationId, cancellationToken, ownerUserId);
            await _spool.AddEventAsync(sessionId, "RECORDING_STARTED", cancellationToken: cancellationToken);
            // Publish the local session id before starting WASAPI. A first callback
            // can fail immediately; the failure handler must still be able to
            // persist the error and stop the session instead of silently returning
            // a successful START response.
            lock (_gate) _sessionId = sessionId;

            CaptureTrack? microphone = null;
            CaptureTrack? systemAudio = null;
            var sessionClock = new RecordingSessionClock();
            string? microphoneWarning = null;
            string? systemWarning = null;
            using var deviceEnumerator = new MMDeviceEnumerator();
            var profile = _storage.RecordingProfile;
            if (profile is not ("ROOM" or "ONLINE" or "MIC_ONLY" or "SYSTEM_ONLY"))
                throw new InvalidOperationException("recording_profile_invalid");
            var captureMicrophone = profile is not "SYSTEM_ONLY";
            var captureSystemAudio = profile is "ONLINE" or "SYSTEM_ONLY";
            if (captureMicrophone)
            {
                try
                {
                    MMDevice? selectedDevice = null;
                    try
                    {
                        selectedDevice = ResolveSelectedDevice(deviceEnumerator, DataFlow.Capture, _storage.MicrophoneDeviceId);
                        var mic = selectedDevice is null ? new WasapiCapture() : new WasapiCapture(selectedDevice);
                        microphone = new CaptureTrack(sessionId, "room-microphone", mic, _spool, _dataRoot, _ffmpegPath, _logger, sessionClock, selectedDevice, HandleCaptureFailure, profile);
                        selectedDevice = null;
                        await _spool.UpsertTrackInfoAsync(microphone.Info, sessionId, cancellationToken);
                        _microphone = microphone;
                        microphone.Start();
                    }
                    finally { selectedDevice?.Dispose(); }
                }
                catch (Exception ex)
                {
                    microphoneWarning = ex.Message;
                    if (microphone is not null) await microphone.DisposeAsync();
                    _microphone = null;
                }
            }
            if (captureSystemAudio)
            {
                try
                {
                    MMDevice? selectedDevice = null;
                    try
                    {
                        selectedDevice = ResolveSelectedDevice(deviceEnumerator, DataFlow.Render, _storage.SystemAudioDeviceId);
                        var loopback = selectedDevice is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(selectedDevice);
                        systemAudio = new CaptureTrack(sessionId, "system-audio", loopback, _spool, _dataRoot, _ffmpegPath, _logger, sessionClock, selectedDevice, HandleCaptureFailure, profile);
                        selectedDevice = null;
                        await _spool.UpsertTrackInfoAsync(systemAudio.Info, sessionId, cancellationToken);
                        _systemAudio = systemAudio;
                        systemAudio.Start();
                    }
                    finally { selectedDevice?.Dispose(); }
                }
                catch (Exception ex)
                {
                    systemWarning = ex.Message;
                    if (systemAudio is not null) await systemAudio.DisposeAsync();
                    _systemAudio = null;
                }
            }
            if (microphone is null && systemAudio is null)
                throw new IOException($"no_audio_source_available; microphone={microphoneWarning}; system={systemWarning}");
            _microphone = microphone;
            _systemAudio = systemAudio;
            if (microphone is not null && (profile is "ROOM" or "ONLINE" or "MIC_ONLY"))
            {
                using var callbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                callbackTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await microphone.FirstAudio.WaitAsync(callbackTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("AUDIO_CALLBACK_TIMEOUT");
                }
                if (microphone.IsFailed)
                    throw new IOException("AUDIO_SOURCE_FAILED");
            }
            if (systemAudio is not null)
            {
                // Keep the explicit SYSTEM_ONLY failure guard as a distinct
                // diagnostic condition; a silent loopback still needs packet
                // confirmation below, but a stopped capture is not silence.
                if (profile == "SYSTEM_ONLY" && systemAudio?.IsFailed == true)
                    throw new IOException("AUDIO_SOURCE_FAILED");
                var activeSystemAudio = systemAudio!;
                using var callbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                callbackTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await activeSystemAudio.FirstAudio.WaitAsync(callbackTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("AUDIO_CALLBACK_TIMEOUT");
                }
                if (activeSystemAudio.IsFailed)
                    throw new IOException("AUDIO_SOURCE_FAILED");
            }
            if (!_state.TryTransition(RecorderState.Recording, "first-audio-buffer-accepted"))
                throw new InvalidOperationException("recording_state_transition_failed");
            Interlocked.Exchange(ref _fatalCaptureFailureStarted, 0);
            if (microphoneWarning is not null || systemWarning is not null)
                await _spool.AddEventAsync(sessionId, "AUDIO_SOURCE_WARNING", payloadJson: JsonSerializer.Serialize(new { microphone = microphoneWarning, systemAudio = systemWarning }), cancellationToken: cancellationToken);
            _logger.LogInformation("Recording started. Session={SessionId}, microphone={Microphone}, systemAudio={SystemAudio}", sessionId, microphone is not null, systemAudio is not null);
            return sessionId;
        }
        catch
        {
            await StopTracksAsync();
            _sessionId = null;
            _state.Restore(RecorderState.Idle, "recording-start-failed");
            try { await _spool.SetSessionStateAsync(sessionId, "FAILED", CancellationToken.None); }
            catch (Exception stateError) { _logger.LogWarning(stateError, "Could not persist failed start state. Session={SessionId}", sessionId); }
            throw;
        }
    }

    private void EnsureStorageAvailable()
    {
        var policy = StorageRetentionPolicy.FromEnvironment();
        var root = Path.GetPathRoot(Path.GetFullPath(_dataRoot));
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("recording_storage_root_unavailable");
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException("recording_storage_root_unavailable");
        var dataWatermark = policy.Evaluate(drive.AvailableFreeSpace, drive.TotalSize);
        if (!dataWatermark.AllowsRecording)
            throw new IOException($"recording_storage_low:{dataWatermark.FreeBytes}:{dataWatermark.BlockFreeBytes}");
        var archiveRoot = Path.GetFullPath(_storage.ArchiveRoot);
        Directory.CreateDirectory(Path.Combine(archiveRoot, "Meetings"));
        var archiveDriveRoot = Path.GetPathRoot(archiveRoot);
        if (string.IsNullOrWhiteSpace(archiveDriveRoot)) throw new IOException("archive_storage_root_unavailable");
        var archiveDrive = new DriveInfo(archiveDriveRoot);
        if (!archiveDrive.IsReady) throw new IOException("archive_storage_root_unavailable");
        var archiveWatermark = policy.Evaluate(archiveDrive.AvailableFreeSpace, archiveDrive.TotalSize);
        if (!archiveWatermark.AllowsRecording)
            throw new IOException($"archive_storage_low:{archiveWatermark.FreeBytes}:{archiveWatermark.BlockFreeBytes}");
    }

    public void ValidatePreflight()
    {
        EnsureStorageAvailable();
        EnsureFfmpegAvailable();
    }

    public async Task<AudioSourceTestResult> TestAudioSourceAsync(string? deviceId, bool systemAudio, CancellationToken cancellationToken = default)
        => await TestAudioSourceAsync(deviceId, systemAudio, TimeSpan.FromSeconds(3), cancellationToken);

    public async Task<AudioSourceTestResult> TestAudioSourceAsync(string? deviceId, bool systemAudio, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var result = await AudioRuntimeProbe.RunAsync(deviceId, systemAudio, duration, cancellationToken);
        if (systemAudio) Volatile.Write(ref _lastSystemAudioProbe, result);
        else Volatile.Write(ref _lastMicrophoneProbe, result);
        return result;
    }

    private static MMDevice? ResolveSelectedDevice(MMDeviceEnumerator enumerator, DataFlow flow, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        try
        {
            var device = enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active)
            {
                device.Dispose();
                throw new InvalidOperationException("selected_audio_device_inactive");
            }
            return device;
        }
        catch (COMException)
        {
            throw new InvalidOperationException("selected_audio_device_unavailable");
        }
    }
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_state.TryTransition(RecorderState.Paused, "recording-paused"))
                throw new InvalidOperationException($"Cannot pause from {_state.State}.");
        }
        _microphone?.Pause();
        _systemAudio?.Pause();
        if (_sessionId is not null)
            await _spool.AddEventAsync(_sessionId, "PAUSE_STARTED", cancellationToken: cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_state.TryTransition(RecorderState.Recording, "recording-resumed"))
                throw new InvalidOperationException($"Cannot resume from {_state.State}.");
        }
        if (_microphone is null && _systemAudio is null || _sessionId is null)
            throw new InvalidOperationException("No paused recording is available.");
        _microphone?.Resume();
        _systemAudio?.Resume();
        await _spool.AddEventAsync(_sessionId, "PAUSE_FINISHED", cancellationToken: cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var stop = await RequestStopAsync(cancellationToken);
        await stop.LocalFinalization;
    }

    public async Task<RecordingStopHandle> RequestStopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state.State is RecorderState.Idle or RecorderState.Finalizing)
                return new RecordingStopHandle(null, Task.CompletedTask);
            if (!_state.TryTransition(RecorderState.Finalizing, "recording-stopping"))
                throw new InvalidOperationException($"Cannot stop from {_state.State}.");
        }

        var sessionId = _sessionId;
        Task localFinalization;
        try
        {
            localFinalization = BeginStopTracks();
        }
        catch
        {
            if (sessionId is not null) await _spool.SetSessionStateAsync(sessionId, "FAILED");
            lock (_gate) _state.Restore(RecorderState.Idle, "recording-finalize-failed");
            throw;
        }
        if (sessionId is not null)
        {
            await _spool.AddEventAsync(sessionId, "RECORDING_STOPPED", cancellationToken: cancellationToken);
            await _spool.SetSessionStateAsync(sessionId, "FINALIZING", cancellationToken);
        }
        return new RecordingStopHandle(sessionId, ObserveLocalFinalizationAsync(sessionId, localFinalization));
    }

    private async Task ObserveLocalFinalizationAsync(string? sessionId, Task localFinalization)
    {
        try
        {
            await localFinalization;
            lock (_gate)
            {
                if (_state.State == RecorderState.Finalizing)
                    _state.TryTransition(RecorderState.Idle, "recording-local-finalized");
                if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal)) _sessionId = null;
            }
        }
        catch
        {
            if (sessionId is not null) await _spool.SetSessionStateAsync(sessionId, "FAILED");
            lock (_gate)
            {
                if (_state.State == RecorderState.Finalizing)
                    _state.TryTransition(RecorderState.Idle, "recording-local-finalize-failed");
            }
            throw;
        }
    }

    private void EnsureFfmpegAvailable()
    {
        if (Path.IsPathRooted(_ffmpegPath) && !File.Exists(_ffmpegPath))
            throw new FileNotFoundException("ffmpeg_unavailable", _ffmpegPath);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = "-version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("ffmpeg_unavailable");
            if (!process.WaitForExit(3000) || process.ExitCode != 0)
                throw new InvalidOperationException("ffmpeg_unavailable");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("ffmpeg_unavailable", ex);
        }
    }

    private Task BeginStopTracks()
    {
        var mic = _microphone;
        var system = _systemAudio;
        _microphone = null;
        _systemAudio = null;
        return Task.WhenAll(
            mic?.BeginDisposeAsync() ?? Task.CompletedTask,
            system?.BeginDisposeAsync() ?? Task.CompletedTask);
    }
    private async Task StopTracksAsync()
    {
        var mic = _microphone;
        var system = _systemAudio;
        _microphone = null;
        _systemAudio = null;
        if (mic is not null) await mic.DisposeAsync();
        if (system is not null) await system.DisposeAsync();
    }

    private void HandleCaptureFailure(CaptureTrack failedTrack, string errorCode, Exception exception)
    {
        var sessionId = _sessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        bool fatal;
        lock (_gate)
        {
            var otherSourceAlive = (_microphone is not null && !ReferenceEquals(_microphone, failedTrack) && !_microphone.IsFailed)
                || (_systemAudio is not null && !ReferenceEquals(_systemAudio, failedTrack) && !_systemAudio.IsFailed);
            fatal = errorCode == "STORAGE_WRITE_FAILED" || !otherSourceAlive;
        }

        _ = PersistCaptureFailureAsync(sessionId, errorCode, exception, fatal);
    }

    private async Task PersistCaptureFailureAsync(string sessionId, string errorCode, Exception exception, bool fatal)
    {
        if (fatal && Interlocked.Exchange(ref _fatalCaptureFailureStarted, 1) != 0) return;
        try
        {
            await _spool.AddEventAsync(sessionId, errorCode, payloadJson: JsonSerializer.Serialize(new { message = exception.Message }), cancellationToken: CancellationToken.None);
            if (!fatal)
            {
                await _spool.SetFinalizationStateAsync(sessionId, errorCode: errorCode, errorDetail: exception.Message, cancellationToken: CancellationToken.None);
                _logger.LogWarning(exception, "One audio source failed; the other source remains active. Code={ErrorCode}, Session={SessionId}", errorCode, sessionId);
                return;
            }

            lock (_gate)
            {
                if (_state.State is RecorderState.Recording or RecorderState.Paused)
                    _state.TryTransition(RecorderState.Finalizing, "capture-failure");
            }

            try { await BeginStopTracks(); }
            catch (Exception stopError) { _logger.LogError(stopError, "Failed to stop capture after audio failure. Session={SessionId}", sessionId); }

            await _spool.SetFinalizationStateAsync(
                sessionId,
                localFinalizeState: "FINALIZING_LOCAL",
                errorCode: errorCode,
                errorDetail: exception.Message,
                cancellationToken: CancellationToken.None);
            await _spool.SetSessionStateAsync(sessionId, "FINALIZING");
            lock (_gate)
            {
                _state.Restore(RecorderState.Error, "capture-failure");
                _sessionId = sessionId;
            }
            _logger.LogError(exception, "Capture stopped because no safe audio path remains. Code={ErrorCode}, Session={SessionId}", errorCode, sessionId);
        }
        catch (Exception persistenceError)
        {
            _logger.LogCritical(persistenceError, "Could not persist capture failure. OriginalCode={ErrorCode}, Session={SessionId}", errorCode, sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_state.State is RecorderState.Recording or RecorderState.Paused)
                await StopAsync();
            else if (_state.State is RecorderState.Finalizing)
            {
                await StopTracksAsync();
                lock (_gate) _state.Restore(RecorderState.Idle, "recorder-cleanup");
            }
            else
                await StopTracksAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recorder cleanup failed.");
        }
    }
    private sealed class CaptureTrack : IAsyncDisposable
    {
        private readonly string _sessionId;
        private readonly IWaveIn _capture;
        private readonly PcmFlacChunkWriter _writer;
        private readonly ILogger _logger;
        private readonly RecordingTrackInfo _info;
        private readonly AudioSampleFormatDescriptor _sampleFormat;
        private readonly MMDevice? _device;
        private readonly Action<CaptureTrack, string, Exception>? _failureHandler;
        private readonly object _peakGate = new();
        private int _started;
        private int _failureReported;
        private double? _peak;
        private double? _peakDb;
        private double? _rms;
        private double? _rmsDb;
        private bool _clipping;
        private DateTimeOffset? _lastAudioAtUtc;
        private DateTimeOffset? _silenceStartedAtUtc;
        private long _captureStartedTimestamp;
        private int _packetCount;
        private long _bytesReceived;
        private long? _firstPacketLatencyMs;
        private readonly TaskCompletionSource<bool> _firstAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CaptureTrack(string sessionId, string trackType, IWaveIn capture, SpoolStore spool, string dataRoot, string ffmpegPath, ILogger logger, RecordingSessionClock sessionClock, MMDevice? device = null, Action<CaptureTrack, string, Exception>? failureHandler = null, string profile = "ROOM")
        {
            _sessionId = sessionId;
            _capture = capture;
            _device = device;
            _logger = logger;
            _failureHandler = failureHandler;
            var trackId = Guid.NewGuid().ToString("N");
            _sampleFormat = AudioSampleFormatResolver.Resolve(capture.WaveFormat);
            _info = new RecordingTrackInfo(
                trackId,
                trackType,
                capture.WaveFormat.SampleRate,
                capture.WaveFormat.Channels,
                device?.ID,
                device?.FriendlyName,
                device is null ? "DEFAULT" : "FIXED",
                profile,
                _sampleFormat.CanonicalEncoding,
                _sampleFormat.BitsPerSample,
                _sampleFormat.SourceEncoding,
                _sampleFormat.SourceSubFormat,
                _sampleFormat.ValidBitsPerSample);
            var startSample = sessionClock.GetStartSample(capture.WaveFormat.SampleRate);
            _writer = new PcmFlacChunkWriter(sessionId, trackId, trackType, capture.WaveFormat, spool, dataRoot, ffmpegPath, logger, startSample);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public RecordingTrackInfo Info => _info;
        public Task FirstAudio => _firstAudio.Task;
        public long MediaTimeMs => _writer.MediaTimeMs;
        public double? Peak { get { lock (_peakGate) return TelemetryStaleUnsafe() ? null : _peak; } }
        public double? PeakDb { get { lock (_peakGate) return TelemetryStaleUnsafe() ? null : _peakDb; } }
        public double? Rms { get { lock (_peakGate) return TelemetryStaleUnsafe() ? null : _rms; } }
        public double? RmsDb { get { lock (_peakGate) return TelemetryStaleUnsafe() ? null : _rmsDb; } }
        public bool? Clipping { get { lock (_peakGate) return TelemetryStaleUnsafe() ? null : _clipping; } }
        public DateTimeOffset? LastAudioAtUtc { get { lock (_peakGate) return _lastAudioAtUtc; } }
        public long? SilenceDurationMs { get { lock (_peakGate) return TelemetryStaleUnsafe() || _silenceStartedAtUtc is null ? null : (long)Math.Max(0, (DateTimeOffset.UtcNow - _silenceStartedAtUtc.Value).TotalMilliseconds); } }
        public bool TelemetryStale { get { lock (_peakGate) return TelemetryStaleUnsafe(); } }
        public bool IsFailed => Volatile.Read(ref _failureReported) != 0;
        public int PacketCount => Volatile.Read(ref _packetCount);
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);
        public long? FirstPacketLatencyMs { get { lock (_peakGate) return _firstPacketLatencyMs; } }
        public string NormalizedSampleFormat => _sampleFormat.CanonicalEncoding;

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                Interlocked.Exchange(ref _captureStartedTimestamp, Stopwatch.GetTimestamp());
                _capture.StartRecording();
            }
        }

        public void Pause()
        {
            if (Interlocked.Exchange(ref _started, 0) == 1)
            {
                try { _capture.StopRecording(); } catch (InvalidOperationException) { }
                _writer.FlushCurrentChunk();
            }
        }

        public void Resume() => Start();

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (IsFailed) return;
            try
            {
                _writer.Append(e.Buffer.AsSpan(0, e.BytesRecorded));
                Interlocked.Increment(ref _packetCount);
                Interlocked.Add(ref _bytesReceived, e.BytesRecorded);
                lock (_peakGate)
                {
                    _firstPacketLatencyMs ??= _captureStartedTimestamp == 0
                        ? null
                        : (long)Math.Round((Stopwatch.GetTimestamp() - _captureStartedTimestamp) * 1000d / Stopwatch.Frequency);
                }
                UpdatePeak(e.Buffer, e.BytesRecorded);
                // The capture is considered started only after the first buffer
                // has been accepted by the durable writer. A callback that is
                // merely raised by WASAPI is not sufficient evidence.
                _firstAudio.TrySetResult(true);
            }
            catch (Exception ex) { ReportFailure(ClassifyWriteFailure(ex), ex); }
        }

        private void UpdatePeak(byte[] buffer, int bytesRecorded)
        {
            var telemetry = CalculateTelemetry(buffer, bytesRecorded, _capture.WaveFormat);
            if (telemetry is null) return;
            var now = DateTimeOffset.UtcNow;
            var peak = telemetry.Value.Peak;
            var rms = telemetry.Value.Rms;
            var db = peak <= 0 ? -60d : Math.Clamp(20d * Math.Log10(peak), -60d, 0d);
            var rmsDb = rms <= 0 ? -60d : Math.Clamp(20d * Math.Log10(rms), -60d, 0d);
            lock (_peakGate)
            {
                _peak = peak;
                _peakDb = db;
                _rms = rms;
                _rmsDb = rmsDb;
                _clipping = telemetry.Value.Clipping;
                _lastAudioAtUtc = now;
                if (rms < 0.01d)
                    _silenceStartedAtUtc ??= now;
                else
                    _silenceStartedAtUtc = null;
            }
        }

        private bool TelemetryStaleUnsafe()
            => _lastAudioAtUtc is null || DateTimeOffset.UtcNow - _lastAudioAtUtc.Value > TimeSpan.FromMilliseconds(750);

        internal static (double Peak, double Rms, bool Clipping)? CalculateTelemetry(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            if (bytesRecorded <= 0 || format.Channels <= 0) return null;
            AudioSampleFormatDescriptor descriptor;
            try { descriptor = AudioSampleFormatResolver.Resolve(format); }
            catch (NotSupportedException) { return null; }
            var peak = 0d;
            var sumSquares = 0d;
            var samples = 0;
            var clippedSamples = 0;
            if (descriptor.Kind == RawAudioSampleFormat.Float32)
            {
                for (var offset = 0; offset + 4 <= bytesRecorded; offset += 4)
                {
                    var sample = Math.Abs(BitConverter.ToSingle(buffer, offset));
                    peak = Math.Max(peak, sample);
                    sumSquares += sample * sample;
                    samples++;
                    if (sample >= 0.995d) clippedSamples++;
                }
            }
            else if (descriptor.Kind == RawAudioSampleFormat.Pcm16)
            {
                for (var offset = 0; offset + 2 <= bytesRecorded; offset += 2)
                {
                    var sample = Math.Abs(BitConverter.ToInt16(buffer, offset) / 32768d);
                    peak = Math.Max(peak, sample);
                    sumSquares += sample * sample;
                    samples++;
                    if (sample >= 0.995d) clippedSamples++;
                }
            }
            else if (descriptor.Kind == RawAudioSampleFormat.Pcm24)
            {
                for (var offset = 0; offset + 3 <= bytesRecorded; offset += 3)
                {
                    var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                    if ((value & 0x00800000) != 0) value |= unchecked((int)0xFF000000);
                    var sample = Math.Abs(value / 8388608d);
                    peak = Math.Max(peak, sample);
                    sumSquares += sample * sample;
                    samples++;
                    if (sample >= 0.995d) clippedSamples++;
                }
            }
            else if (descriptor.Kind == RawAudioSampleFormat.Pcm32)
            {
                for (var offset = 0; offset + 4 <= bytesRecorded; offset += 4)
                {
                    var sample = Math.Abs(BitConverter.ToInt32(buffer, offset) / 2147483648d);
                    peak = Math.Max(peak, sample);
                    sumSquares += sample * sample;
                    samples++;
                    if (sample >= 0.995d) clippedSamples++;
                }
            }
            else return null;
            if (samples == 0) return null;
            return (Math.Clamp(peak, 0d, 1d), Math.Sqrt(sumSquares / samples), clippedSamples / (double)samples >= 0.005d);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null) ReportFailure("AUDIO_SOURCE_FAILED", e.Exception);
        }

        private void ReportFailure(string errorCode, Exception exception)
        {
            if (Interlocked.Exchange(ref _failureReported, 1) != 0) return;
            try { _capture.StopRecording(); }
            catch (Exception stopError) { _logger.LogDebug(stopError, "Capture stop after failure was already completed. Session={SessionId}", _sessionId); }
            _failureHandler?.Invoke(this, errorCode, exception);
        }

        private static string ClassifyWriteFailure(Exception exception)
        {
            var message = exception.ToString();
            return message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
                || message.Contains("encoder", StringComparison.OrdinalIgnoreCase)
                || message.Contains("queue", StringComparison.OrdinalIgnoreCase)
                ? "ENCODER_FAILED"
                : "STORAGE_WRITE_FAILED";
        }

        public async ValueTask DisposeAsync()
        {
            await BeginDisposeAsync();
        }

        public Task BeginDisposeAsync()
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            if (Interlocked.Exchange(ref _started, 0) == 1)
            {
                try { _capture.StopRecording(); } catch (InvalidOperationException) { }
            }
            _capture.Dispose();
            _device?.Dispose();
            return _writer.DisposeAsync().AsTask();
        }
    }
}

internal sealed class PcmFlacChunkWriter : IAsyncDisposable
{
    // This is deliberately just metadata. A completed raw file is closed and
    // renamed before its descriptor is offered to the channel, so the channel
    // can never retain a FileStream when the encoder is behind.
    private sealed record PendingRawChunk(
        string Id,
        int Sequence,
        string RawPath,
        long StartSample,
        long SampleCount);

    private readonly string _sessionId;
    private readonly string _trackId;
    private readonly string _trackType;
    private readonly WaveFormat _format;
    private readonly AudioSampleFormatDescriptor _sampleFormat;
    private readonly SpoolStore _spool;
    private readonly string _root;
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    // The capture callback must never wait for the encoder. The bounded channel
    // is only a fast path. A full channel leaves the closed .pcm on disk, where
    // the encoder loop and restart recovery can discover it later.
    private readonly Channel<PendingRawChunk> _pending;
    private readonly SemaphoreSlim _workSignal = new(0);
    private readonly int _queueCapacity;
    private readonly Task _encoderTask;
    private FileStream? _raw;
    private string? _rawPath;
    private string? _rawPartPath;
    private string? _rawId;
    private int _sequence;
    private long _startSample;
    private long _sampleCount;
    private bool _disposed;
    private Exception? _encoderFailure;

    public long MediaTimeMs
    {
        get
        {
            lock (_gate) return _format.SampleRate <= 0 ? 0 : (long)Math.Round((_startSample + _sampleCount) * 1000d / _format.SampleRate);
        }
    }

    public PcmFlacChunkWriter(string sessionId, string trackId, string trackType, WaveFormat format, SpoolStore spool, string root, string ffmpegPath, ILogger logger, long startSample)
    {
        _sessionId = sessionId;
        _trackId = trackId;
        _trackType = trackType;
        _format = format;
        _sampleFormat = AudioSampleFormatResolver.Resolve(format);
        _spool = spool;
        _root = root;
        _ffmpegPath = ffmpegPath;
        _logger = logger;
        _startSample = Math.Max(0, startSample);
        _queueCapacity = ReadQueueCapacity();
        _pending = Channel.CreateBounded<PendingRawChunk>(new BoundedChannelOptions(_queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // TryWrite returns false when full; the descriptor then goes to the
            // disk-backed recovery/overflow path instead of being dropped.
            FullMode = BoundedChannelFullMode.Wait
        });
        _encoderTask = Task.Run(ProcessQueueAsync);
    }

    private static int ReadQueueCapacity()
    {
        var configured = Environment.GetEnvironmentVariable("ATOM_AGENT_ENCODER_QUEUE_CAPACITY");
        return int.TryParse(configured, out var value) ? Math.Clamp(value, 1, 64) : 24;
    }

    public void Append(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var frameBytes = Math.Max(1, _format.BlockAlign);
            var usable = pcm.Length - pcm.Length % frameBytes;
            var offset = 0;
            var targetSamples = Math.Max(1, _format.SampleRate * RecordingContract.ChunkDurationSeconds);
            while (offset < usable)
            {
                EnsureOpen();
                var framesAvailable = (usable - offset) / frameBytes;
                var framesToWrite = Math.Min(framesAvailable, targetSamples - _sampleCount);
                var bytesToWrite = checked((int)(framesToWrite * frameBytes));
                _raw!.Write(pcm.Slice(offset, bytesToWrite));
                _sampleCount += framesToWrite;
                offset += bytesToWrite;
                if (_sampleCount >= targetSamples) QueueCurrentChunk();
            }
        }
    }

    public void FlushCurrentChunk()
    {
        lock (_gate)
        {
            if (!_disposed && _sampleCount > 0) QueueCurrentChunk();
        }
    }

    private void EnsureOpen()
    {
        if (_raw is not null) return;
        var directory = Path.Combine(_root, "recordings", _sessionId, _trackId);
        Directory.CreateDirectory(directory);
        _rawPath = Path.Combine(directory, $"{_sequence:D8}.pcm");
        _rawPartPath = _rawPath + ".part";
        var output = Path.Combine(directory, $"{_sequence:D8}.flac");
        _rawId = Guid.NewGuid().ToString("N");
        _raw = new FileStream(_rawPartPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        _sampleCount = 0;
    }

    private void QueueCurrentChunk()
    {
        if (_raw is null || _rawPath is null || _sampleCount <= 0) return;
        var raw = _raw;
        var rawPath = _rawPath;
        var rawPartPath = _rawPartPath;
        var rawId = _rawId;
        var sequence = _sequence;
        var sampleCount = _sampleCount;
        var startSample = _startSample;
        _raw = null;
        _rawPath = null;
        _rawPartPath = null;
        _rawId = null;
        _sampleCount = 0;
        if (rawPartPath is null) throw new InvalidOperationException("raw_chunk_part_path_missing");
        if (rawId is null) throw new InvalidOperationException("raw_chunk_id_missing");
        // Closing and promoting the file is the handoff boundary. It is the
        // durable fallback queue if TryWrite below finds the in-memory channel
        // full. Do not move this work into the encoder loop: that would retain
        // an unbounded number of open capture streams.
        raw.Dispose();
        var durableRawPath = Path.Combine(Path.GetDirectoryName(rawPath)!, RawChunkFileName.Create(sequence, startSample, sampleCount));
        File.Move(rawPartPath, durableRawPath, true);
        var descriptor = new PendingRawChunk(rawId, sequence, durableRawPath, startSample, sampleCount);
        if (_pending.Writer.TryWrite(descriptor)) _workSignal.Release();
        _sequence++;
        _startSample += sampleCount;
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            while (_pending.Reader.TryRead(out var chunk))
            {
                await ProcessChunkAsync(chunk);
            }

            // A closed .pcm with no SQLite row is the durable overflow queue.
            // Scanning here also covers a process that stopped after closing a
            // raw file but before it could enqueue a descriptor.
            await ProcessDiscoveredChunksAsync();

            if (_disposed) break;
            await _workSignal.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
    }

    private async Task ProcessDiscoveredChunksAsync()
    {
        var directory = Path.Combine(_root, "recordings", _sessionId, _trackId);
        if (!Directory.Exists(directory)) return;

        foreach (var rawPath in Directory.EnumerateFiles(directory, "*.pcm").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var exactName = RawChunkFileName.TryParse(rawPath, out var sequence, out var exactStartSample, out var exactSampleCount);
            var name = Path.GetFileNameWithoutExtension(rawPath);
            if (!exactName && (!int.TryParse(name, out sequence) || sequence < 0)) continue;
            if (await _spool.RawChunkExistsAsync(_sessionId, _trackId, sequence)) continue;

            var size = new FileInfo(rawPath).Length;
            var sampleCount = exactName ? exactSampleCount : size / Math.Max(1, _format.BlockAlign);
            if (sampleCount <= 0) continue;
            var startSample = exactName
                ? exactStartSample
                : checked((long)sequence * _format.SampleRate * RecordingContract.ChunkDurationSeconds);
            if (!exactName)
                _logger.LogWarning("RAW_CHUNK_LEGACY_TIMELINE_INFERRED Session={SessionId} Track={TrackId} Sequence={Sequence}", _sessionId, _trackId, sequence);
            await ProcessChunkAsync(new PendingRawChunk(Guid.NewGuid().ToString("N"), sequence, rawPath, startSample, sampleCount));
        }
    }

    private async Task ProcessChunkAsync(PendingRawChunk chunk)
    {
        var directory = Path.GetDirectoryName(chunk.RawPath)!;
        var outputPart = Path.Combine(directory, $"{chunk.Sequence:D8}.flac.part");
        var output = Path.Combine(directory, $"{chunk.Sequence:D8}.flac");
        try
        {
            // All expensive durability and persistence work is deliberately
            // outside the realtime callback.
            _spool.RegisterRawChunk(new RawRecordingChunk(
                chunk.Id, _sessionId, _trackId, chunk.Sequence, chunk.RawPath,
                output, chunk.StartSample, chunk.SampleCount,
                _format.SampleRate, _format.Channels, _trackType,
                _sampleFormat.CanonicalEncoding, _sampleFormat.BitsPerSample, "WRITING", 0, null, null,
                _sampleFormat.SourceEncoding, _sampleFormat.SourceSubFormat, _sampleFormat.ValidBitsPerSample));
            // The callback only closes the stream. The expensive durability
            // flush happens here, after the descriptor has left realtime code.
            await using (var raw = new FileStream(chunk.RawPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.SequentialScan))
                raw.Flush(true);
            var rawSize = new FileInfo(chunk.RawPath).Length;
            var rawSha = FlacEncoder.ComputeSha256(chunk.RawPath);
            _spool.MarkRawChunkReady(_sessionId, _trackId, chunk.Sequence, chunk.SampleCount, rawSize, rawSha);
            await _spool.SetRawChunkStateAsync(_sessionId, _trackId, chunk.Sequence, "ENCODING");
            FlacEncoder.Encode(_ffmpegPath, chunk.RawPath, outputPart, _format);
            File.Move(outputPart, output, true);
            var size = new FileInfo(output).Length;
            var sha = FlacEncoder.ComputeSha256(output);
            await _spool.UpsertChunkAsync(new RecordingChunk(chunk.Id, _sessionId, _trackId, chunk.Sequence, output, chunk.StartSample, chunk.SampleCount, _format.SampleRate, _format.Channels, _trackType, size, sha, "READY", 0));
            await _spool.SetRawChunkStateAsync(_sessionId, _trackId, chunk.Sequence, "READY", size, sha);
            File.Delete(chunk.RawPath);
            _logger.LogInformation("Audio chunk ready. Session={SessionId}, Track={TrackType}, Sequence={Sequence}, Samples={Samples}", _sessionId, _trackType, chunk.Sequence, chunk.SampleCount);
        }
        catch (Exception ex)
        {
            _encoderFailure ??= ex;
            _logger.LogError(ex, "Failed to encode audio chunk. Session={SessionId}, Track={TrackType}, Sequence={Sequence}", _sessionId, _trackType, chunk.Sequence);
            try { if (File.Exists(outputPart)) File.Delete(outputPart); } catch (IOException) { }
            try { await _spool.SetRawChunkStateAsync(_sessionId, _trackId, chunk.Sequence, "ENCODE_FAILED", error: ex.Message); } catch (Exception stateError) { _logger.LogDebug(stateError, "Could not persist encoder failure. Session={SessionId}, Sequence={Sequence}", _sessionId, chunk.Sequence); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        string? emptyRawPart = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_raw is not null && _sampleCount > 0) QueueCurrentChunk();
            _disposed = true;
            if (_raw is not null && _sampleCount == 0) emptyRawPart = _rawPartPath;
            _raw?.Dispose();
            _raw = null;
            _pending.Writer.TryComplete();
            _workSignal.Release();
        }
        if (emptyRawPart is not null)
        {
            try { if (File.Exists(emptyRawPart)) File.Delete(emptyRawPart); }
            catch (IOException) { _logger.LogDebug("Could not remove empty raw chunk part {Path}.", emptyRawPart); }
        }
        await _encoderTask;
        _workSignal.Dispose();
        if (_encoderFailure is not null) throw new InvalidOperationException("One or more audio chunks failed to encode.", _encoderFailure);
    }
}

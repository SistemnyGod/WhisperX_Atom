using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperX.Atom.Recorder;

public sealed record RecordingTrackInfo(string TrackId, string TrackType, int SampleRate, int Channels);

public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly SpoolStore _spool;
    private readonly AgentStateMachine _state;
    private readonly ILogger<RecordingCoordinator> _logger;
    private readonly string _dataRoot;
    private readonly string _ffmpegPath;
    private readonly object _gate = new();
    private CaptureTrack? _microphone;
    private CaptureTrack? _systemAudio;
    private string? _sessionId;

    public RecordingCoordinator(SpoolStore spool, AgentStateMachine state, ILogger<RecordingCoordinator> logger)
    {
        _spool = spool;
        _state = state;
        _logger = logger;
        _dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        _ffmpegPath = Environment.GetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH") ?? "ffmpeg";
    }

    public string? SessionId => _sessionId;

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

    public async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state.State is RecorderState.Recording or RecorderState.Paused)
                throw new InvalidOperationException("Recording is already active.");
            if (!_state.TryTransition(RecorderState.Recording, "recording-started"))
                throw new InvalidOperationException($"Cannot start recording from {_state.State}.");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        try
        {
            await _spool.CreateSessionAsync(sessionId, cancellationToken: cancellationToken);
            await _spool.AddEventAsync(sessionId, "RECORDING_STARTED", cancellationToken: cancellationToken);

            var mic = new WasapiCapture();
            var loopback = new WasapiLoopbackCapture();
            _microphone = new CaptureTrack(sessionId, "room-microphone", mic, _spool, _dataRoot, _ffmpegPath, _logger);
            _systemAudio = new CaptureTrack(sessionId, "system-audio", loopback, _spool, _dataRoot, _ffmpegPath, _logger);
            _sessionId = sessionId;
            _microphone.Start();
            _systemAudio.Start();
            _logger.LogInformation("Recording started. Session={SessionId}, microphone={MicrophoneFormat}, system={SystemFormat}", sessionId, mic.WaveFormat, loopback.WaveFormat);
            return sessionId;
        }
        catch
        {
            await StopTracksAsync();
            _sessionId = null;
            _state.Restore(RecorderState.Idle, "recording-start-failed");
            await _spool.SetSessionStateAsync(sessionId, "FAILED", cancellationToken);
            throw;
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
        if (_microphone is null || _systemAudio is null || _sessionId is null)
            throw new InvalidOperationException("No paused recording is available.");
        _microphone.Resume();
        _systemAudio.Resume();
        await _spool.AddEventAsync(_sessionId, "PAUSE_FINISHED", cancellationToken: cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state.State is RecorderState.Idle) return;
            if (_state.State is RecorderState.Finalizing) return;
            if (!_state.TryTransition(RecorderState.Finalizing, "recording-stopping"))
                throw new InvalidOperationException($"Cannot stop from {_state.State}.");
        }

        var sessionId = _sessionId;
        try
        {
            await StopTracksAsync();
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
        lock (_gate) _state.TryTransition(RecorderState.Idle, "recording-finalized");
        _sessionId = null;
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
        private int _started;

        public CaptureTrack(string sessionId, string trackType, IWaveIn capture, SpoolStore spool, string dataRoot, string ffmpegPath, ILogger logger)
        {
            _sessionId = sessionId;
            _capture = capture;
            _logger = logger;
            var trackId = Guid.NewGuid().ToString("N");
            _info = new RecordingTrackInfo(trackId, trackType, capture.WaveFormat.SampleRate, capture.WaveFormat.Channels);
            _writer = new PcmFlacChunkWriter(sessionId, trackId, trackType, capture.WaveFormat, spool, dataRoot, ffmpegPath, logger);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public RecordingTrackInfo Info => _info;

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0) _capture.StartRecording();
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
            try { _writer.Append(e.Buffer.AsSpan(0, e.BytesRecorded)); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to spool audio chunk. Session={SessionId}", _sessionId); }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null) _logger.LogError(e.Exception, "Audio capture stopped with an error. Session={SessionId}", _sessionId);
        }

        public async ValueTask DisposeAsync()
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            if (Interlocked.Exchange(ref _started, 0) == 1)
            {
                try { _capture.StopRecording(); } catch (InvalidOperationException) { }
            }
            _capture.Dispose();
            await _writer.DisposeAsync();
        }
    }
}

internal sealed class PcmFlacChunkWriter : IAsyncDisposable
{
    private readonly string _sessionId;
    private readonly string _trackId;
    private readonly string _trackType;
    private readonly WaveFormat _format;
    private readonly SpoolStore _spool;
    private readonly string _root;
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private FileStream? _raw;
    private string? _rawPath;
    private int _sequence;
    private long _startSample;
    private long _sampleCount;
    private bool _disposed;

    public PcmFlacChunkWriter(string sessionId, string trackId, string trackType, WaveFormat format, SpoolStore spool, string root, string ffmpegPath, ILogger logger)
    {
        _sessionId = sessionId;
        _trackId = trackId;
        _trackType = trackType;
        _format = format;
        _spool = spool;
        _root = root;
        _ffmpegPath = ffmpegPath;
        _logger = logger;
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
                if (_sampleCount >= targetSamples) FinalizeCurrentChunk();
            }
        }
    }

    public void FlushCurrentChunk()
    {
        lock (_gate)
        {
            if (!_disposed && _sampleCount > 0) FinalizeCurrentChunk();
        }
    }

    private void EnsureOpen()
    {
        if (_raw is not null) return;
        var directory = Path.Combine(_root, "recordings", _sessionId, _trackId);
        Directory.CreateDirectory(directory);
        _rawPath = Path.Combine(directory, $"{_sequence:D8}.pcm.part");
        _raw = new FileStream(_rawPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        _sampleCount = 0;
    }

    private void FinalizeCurrentChunk()
    {
        if (_raw is null || _rawPath is null || _sampleCount <= 0) return;
        var raw = _raw;
        var rawPath = _rawPath;
        var sampleCount = _sampleCount;
        var startSample = _startSample;
        _raw = null;
        _rawPath = null;
        _sampleCount = 0;
        raw.Flush(true);
        raw.Dispose();

        var directory = Path.GetDirectoryName(rawPath)!;
        var outputPart = Path.Combine(directory, $"{_sequence:D8}.flac.part");
        var output = Path.Combine(directory, $"{_sequence:D8}.flac");
        EncodeFlac(rawPath, outputPart);
        File.Move(outputPart, output, true);
        File.Delete(rawPath);
        var size = new FileInfo(output).Length;
        var sha = ComputeSha256(output);
        _spool.UpsertChunkAsync(new RecordingChunk(Guid.NewGuid().ToString("N"), _sessionId, _trackId, _sequence, output, startSample, sampleCount, _format.SampleRate, _format.Channels, size, sha, "READY", 0)).GetAwaiter().GetResult();
        _logger.LogInformation("Audio chunk ready. Session={SessionId}, Track={TrackType}, Sequence={Sequence}, Samples={Samples}", _sessionId, _trackType, _sequence, sampleCount);
        _sequence++;
        _startSample += sampleCount;
    }

    private void EncodeFlac(string input, string output)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = _ffmpegPath, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-loglevel"); process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-f"); process.StartInfo.ArgumentList.Add(FfmpegFormat());
        process.StartInfo.ArgumentList.Add("-ar"); process.StartInfo.ArgumentList.Add(_format.SampleRate.ToString());
        process.StartInfo.ArgumentList.Add("-ac"); process.StartInfo.ArgumentList.Add(_format.Channels.ToString());
        process.StartInfo.ArgumentList.Add("-i"); process.StartInfo.ArgumentList.Add(input);
        process.StartInfo.ArgumentList.Add("-c:a"); process.StartInfo.ArgumentList.Add("flac");
        process.StartInfo.ArgumentList.Add("-compression_level"); process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-f"); process.StartInfo.ArgumentList.Add("flac");
        process.StartInfo.ArgumentList.Add("-y"); process.StartInfo.ArgumentList.Add(output);
        try
        {
            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg FLAC encode failed ({process.ExitCode}): {error.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"FFmpeg was not found. Set ATOM_AGENT_FFMPEG_PATH or add ffmpeg.exe to PATH. {_ffmpegPath}", ex);
        }
    }

    private string FfmpegFormat() => _format.Encoding == WaveFormatEncoding.IeeeFloat && _format.BitsPerSample == 32 ? "f32le" : _format.BitsPerSample switch
    {
        16 => "s16le",
        24 => "s24le",
        32 => "s32le",
        _ => throw new NotSupportedException($"Unsupported audio format: {_format.Encoding}/{_format.BitsPerSample} bits")
    };

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            if (_raw is not null && _sampleCount > 0) FlushCurrentChunk();
            _disposed = true;
            _raw?.Dispose();
            _raw = null;
        }
        return ValueTask.CompletedTask;
    }
}

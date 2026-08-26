using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Non-blocking, in-memory fan-out for provisional ASR. Durable capture never
/// waits for this broadcaster or for Voice Host. Each track has its own queue.
/// </summary>
public sealed class LiveAudioBroadcaster
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Channel<LiveAudioFrame>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _sequences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _drops = new(StringComparer.OrdinalIgnoreCase);
    private string? _sessionId;
    private Guid? _meetingId;
    private bool _systemAudioEnabled;
    private bool _paused;
    private DateTimeOffset _startedAtUtc;

    internal event Action? SessionChanged;

    internal string PipeName => LiveAudioIpc.PipeName;

    internal void StartSession(string sessionId, Guid? meetingId, bool systemAudioEnabled)
    {
        lock (_gate)
        {
            CompleteQueuesLocked();
            _queues.Clear();
            _sequences.Clear();
            _drops.Clear();
            _sessionId = sessionId;
            _meetingId = meetingId;
            _systemAudioEnabled = systemAudioEnabled;
            _paused = false;
            _startedAtUtc = DateTimeOffset.UtcNow;
            EnsureQueueLocked("room-microphone");
            EnsureQueueLocked("system-audio");
        }
        SessionChanged?.Invoke();
    }

    internal void StopSession(string? sessionId)
    {
        lock (_gate)
        {
            if (sessionId is not null && !string.Equals(sessionId, _sessionId, StringComparison.OrdinalIgnoreCase)) return;
            CompleteQueuesLocked();
            _sessionId = null;
            _meetingId = null;
            _systemAudioEnabled = false;
            _paused = false;
            _startedAtUtc = default;
        }
        SessionChanged?.Invoke();
    }

    internal bool IsActive(string sessionId)
        => string.Equals(sessionId, _sessionId, StringComparison.OrdinalIgnoreCase);

    internal ChannelReader<LiveAudioFrame>? Subscribe(string trackType)
    {
        lock (_gate)
        {
            if (_sessionId is null) return null;
            return EnsureQueueLocked(trackType).Reader;
        }
    }

    internal LiveAudioSession? Snapshot()
    {
        lock (_gate)
        {
            return _sessionId is null
                ? null
                : new("LIVE_AUDIO_V1", _sessionId, _meetingId, "SESSION_STARTED", _startedAtUtc, _systemAudioEnabled, _paused, _sessionId);
        }
    }

    internal void SetPaused(string sessionId, bool paused)
    {
        lock (_gate)
        {
            if (!string.Equals(sessionId, _sessionId, StringComparison.OrdinalIgnoreCase)) return;
            _paused = paused;
        }
    }

    internal bool IsPaused(string sessionId)
    {
        lock (_gate)
            return string.Equals(sessionId, _sessionId, StringComparison.OrdinalIgnoreCase) && _paused;
    }

    internal bool TryPublish(string sessionId, string trackId, string trackType, ReadOnlySpan<byte> pcm48k, DateTimeOffset capturedAtUtc, int sourceSampleCount)
    {
        if (pcm48k.Length == 0 || !IsActive(sessionId)) return false;
        var pcm16k = Downsample48kTo16k(pcm48k);
        if (pcm16k.Length == 0) return false;
        Channel<LiveAudioFrame> queue;
        LiveAudioFrame frame;
        lock (_gate)
        {
            if (!string.Equals(sessionId, _sessionId, StringComparison.OrdinalIgnoreCase)) return false;
            queue = EnsureQueueLocked(trackType);
            var sequence = _sequences.TryGetValue(trackType, out var current) ? current + 1 : 0;
            _sequences[trackType] = sequence;
            var droppedBefore = _drops.TryGetValue(trackType, out var drops) ? drops : 0;
            var startMs = Math.Max(0, (long)(capturedAtUtc - _startedAtUtc).TotalMilliseconds);
            frame = new(
                "LIVE_AUDIO_V1", sessionId, _meetingId, trackId, trackType,
                string.Equals(trackType, "system-audio", StringComparison.OrdinalIgnoreCase) ? "REMOTE_SYSTEM" : "LOCAL_ROOM",
                startMs, Math.Max(1, sourceSampleCount * 1000 / 48_000), sequence,
                LiveAudioIpc.SampleRate, LiveAudioIpc.Channels,
                Convert.ToBase64String(pcm16k), droppedBefore > 0, droppedBefore, sessionId);
            if (queue.Writer.TryWrite(frame)) return true;
            _drops[trackType] = droppedBefore + 1;
            return false;
        }
    }

    private Channel<LiveAudioFrame> EnsureQueueLocked(string trackType)
    {
        if (_queues.TryGetValue(trackType, out var existing)) return existing;
        var queue = Channel.CreateBounded<LiveAudioFrame>(new BoundedChannelOptions(LiveAudioIpc.QueueCapacityPerTrack)
        {
            SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _queues[trackType] = queue;
        return queue;
    }

    private void CompleteQueuesLocked()
    {
        foreach (var queue in _queues.Values) queue.Writer.TryComplete();
    }

    private static byte[] Downsample48kTo16k(ReadOnlySpan<byte> source)
    {
        var sourceSamples = source.Length / 2;
        if (sourceSamples < 3) return Array.Empty<byte>();
        var result = new byte[(sourceSamples / 3) * 2];
        var target = 0;
        for (var sourceIndex = 0; sourceIndex + 2 < sourceSamples; sourceIndex += 3)
        {
            var sample = BitConverter.ToInt16(source.Slice(sourceIndex * 2, 2));
            result[target++] = (byte)(sample & 0xff);
            result[target++] = (byte)(sample >> 8);
        }
        return result;
    }
}

internal sealed class LiveAudioPipeServer : BackgroundService
{
    private readonly LiveAudioBroadcaster _broadcaster;
    private readonly ILogger<LiveAudioPipeServer> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public LiveAudioPipeServer(LiveAudioBroadcaster broadcaster, ILogger<LiveAudioPipeServer> logger)
    {
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = LiveAudioPipeSecurity.CreateServer();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await StreamConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogDebug(ex, "Live Audio IPC connection ended."); }
        }
    }

    private async Task StreamConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var snapshot = _broadcaster.Snapshot();
        if (snapshot is null) return;
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot with { Event = "SESSION_STARTED" }, _json)).ConfigureAwait(false);
        var room = _broadcaster.Subscribe("room-microphone");
        var system = _broadcaster.Subscribe("system-audio");
        if (room is null || system is null) return;
        // A reconnect must never replay frames that were already queued for a
        // previous Voice Host connection. Provisional audio is disposable; the
        // durable PCM/FLAC path remains the source of truth. Dropping this
        // backlog avoids duplicate live segments after a pipe reconnect.
        while (room.TryRead(out _)) { }
        while (system.TryRead(out _)) { }
        var readers = new[] { room, system };
        var paused = snapshot.Paused;
        while (!cancellationToken.IsCancellationRequested && _broadcaster.IsActive(snapshot.SessionId))
        {
            var delay = Task.Delay(250, cancellationToken);
            var waits = readers.Select(reader => reader.WaitToReadAsync(cancellationToken).AsTask()).Append(delay).ToArray();
            var completed = await Task.WhenAny(waits).ConfigureAwait(false);
            if (completed == delay)
            {
                var currentPaused = _broadcaster.IsPaused(snapshot.SessionId);
                if (currentPaused != paused)
                {
                    paused = currentPaused;
                    await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot with
                    {
                        Event = paused ? "SESSION_PAUSED" : "SESSION_RESUMED",
                        Paused = paused
                    }, _json)).ConfigureAwait(false);
                }
                continue;
            }
            if (completed is Task<bool> completedReader && !await completedReader.ConfigureAwait(false)) break;
            foreach (var reader in readers)
                while (reader.TryRead(out var frame))
                    await writer.WriteLineAsync(JsonSerializer.Serialize(frame, _json)).ConfigureAwait(false);
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot with { Event = "SESSION_STOPPED" }, _json)).ConfigureAwait(false);
    }
}

internal static class LiveAudioPipeSecurity
{
    public static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null) throw new InvalidOperationException("LIVE_AUDIO_SID_UNAVAILABLE");
        AddRule(security, identity.User);
        var configuredSid = Environment.GetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID")
            ?? Environment.GetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(configuredSid))
        {
            var dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
            try
            {
                var sidPath = Path.Combine(dataRoot, "allowed-user.sid");
                configuredSid = File.Exists(sidPath) ? File.ReadAllText(sidPath).Trim() : null;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (!string.IsNullOrWhiteSpace(configuredSid)
            && !string.Equals(configuredSid, identity.User.Value, StringComparison.OrdinalIgnoreCase))
        {
            try { AddRule(security, new SecurityIdentifier(configuredSid)); }
            catch (ArgumentException exception) { throw new InvalidOperationException("LIVE_AUDIO_ALLOWED_SID_INVALID", exception); }
        }
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return NamedPipeServerStreamAcl.Create(LiveAudioIpc.PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private static void AddRule(PipeSecurity security, SecurityIdentifier sid)
        => security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.Read, AccessControlType.Allow));
}

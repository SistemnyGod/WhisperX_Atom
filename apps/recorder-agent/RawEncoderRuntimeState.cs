namespace WhisperX.Atom.Recorder;

public sealed record RawEncoderRuntimeSnapshot(
    string State,
    DateTimeOffset? LastHeartbeatAtUtc,
    string? CurrentChunkId,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastErrorCode,
    int QueueDepth);

/// <summary>
/// Process-wide observable state for the singleton raw encoder.  The worker
/// owns transitions; health only reads an immutable snapshot and therefore
/// never touches the encoder's SQLite/FFmpeg critical path.
/// </summary>
public sealed class RawEncoderRuntimeState
{
    private readonly object _gate = new();
    private string _state = "STARTING";
    private DateTimeOffset? _lastHeartbeatAtUtc;
    private string? _currentChunkId;
    private DateTimeOffset? _lastSuccessAtUtc;
    private string? _lastErrorCode;
    private int _queueDepth;

    public RawEncoderRuntimeSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RawEncoderRuntimeSnapshot(_state, _lastHeartbeatAtUtc, _currentChunkId, _lastSuccessAtUtc, _lastErrorCode, _queueDepth);
        }
    }

    public void Mark(string state, string? chunkId = null, string? errorCode = null, int? queueDepth = null)
    {
        lock (_gate)
        {
            _state = state;
            _lastHeartbeatAtUtc = DateTimeOffset.UtcNow;
            _currentChunkId = chunkId;
            if (errorCode is not null) _lastErrorCode = errorCode;
            if (queueDepth.HasValue) _queueDepth = Math.Max(0, queueDepth.Value);
        }
    }

    public void MarkSuccess(string? chunkId = null, int? queueDepth = null)
    {
        lock (_gate)
        {
            _state = "READY";
            _lastHeartbeatAtUtc = DateTimeOffset.UtcNow;
            _lastSuccessAtUtc = _lastHeartbeatAtUtc;
            _currentChunkId = chunkId;
            if (queueDepth.HasValue) _queueDepth = Math.Max(0, queueDepth.Value);
            _lastErrorCode = null;
        }
    }
}

namespace WhisperX.Atom.Recorder;

public enum RecorderState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Finalizing,
    Recovering,
    Offline,
    Error
}

public enum RecorderCommand
{
    Start,
    Pause,
    Resume,
    Stop,
    Marker,
    Decision,
    ActionItem,
    Status
}

public sealed record StateTransition(RecorderState From, RecorderState To, DateTimeOffset At, string Reason);

public sealed class AgentStateMachine
{
    private readonly object _gate = new();
    private readonly List<StateTransition> _history = [];
    private RecorderState _state = RecorderState.Idle;

    public RecorderState State { get { lock (_gate) return _state; } }
    public IReadOnlyList<StateTransition> History { get { lock (_gate) return _history.ToArray(); } }

    public bool TryTransition(RecorderState next, string reason)
    {
        lock (_gate)
        {
            if (!IsAllowed(_state, next)) return false;
            var previous = _state;
            _state = next;
            _history.Add(new StateTransition(previous, next, DateTimeOffset.UtcNow, reason));
            return true;
        }
    }

    public void Restore(RecorderState state, string reason)
    {
        lock (_gate)
        {
            var previous = _state;
            _state = state;
            _history.Add(new StateTransition(previous, state, DateTimeOffset.UtcNow, reason));
        }
    }

    private static bool IsAllowed(RecorderState from, RecorderState to) => (from, to) switch
    {
        (RecorderState.Idle, RecorderState.Starting or RecorderState.Recovering or RecorderState.Offline) => true,
        (RecorderState.Offline, RecorderState.Starting or RecorderState.Idle) => true,
        (RecorderState.Starting, RecorderState.Recording or RecorderState.Error or RecorderState.Idle) => true,
        (RecorderState.Recording, RecorderState.Paused or RecorderState.Finalizing or RecorderState.Offline) => true,
        (RecorderState.Paused, RecorderState.Recording or RecorderState.Finalizing or RecorderState.Offline) => true,
        (RecorderState.Finalizing, RecorderState.Idle or RecorderState.Offline) => true,
        (RecorderState.Recovering, RecorderState.Recording or RecorderState.Finalizing or RecorderState.Idle) => true,
        (RecorderState.Error, RecorderState.Idle or RecorderState.Recording) => true,
        _ => false
    };
}

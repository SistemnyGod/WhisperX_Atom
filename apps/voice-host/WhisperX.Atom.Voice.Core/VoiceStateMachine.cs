namespace WhisperX.Atom.Voice;

public sealed class VoiceStateMachine
{
    private readonly object _gate = new();
    private readonly List<VoiceTransition> _history = [];
    private VoiceHostState _state = VoiceHostState.Disabled;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _heartbeatAtUtc = DateTimeOffset.UtcNow;
    private bool _enabled;
    private string? _lastText;
    private string? _lastResponse;
    private string? _pendingConfirmation;
    private bool _pushToTalk;
    private VoiceHostState _responseReturnState = VoiceHostState.Listening;

    public VoiceHostSnapshot Snapshot { get { lock (_gate) return CreateSnapshot(); } }
    public IReadOnlyList<VoiceTransition> History { get { lock (_gate) return _history.ToArray(); } }

    /// <summary>Marks that the host answered a health/status request.</summary>
    public void TouchHeartbeat()
    {
        lock (_gate) _heartbeatAtUtc = DateTimeOffset.UtcNow;
    }

    public void Enable(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _pendingConfirmation = null;
            if (!enabled)
            {
                var previous = _state;
                _state = VoiceHostState.Disabled;
                _updatedAt = DateTimeOffset.UtcNow;
                _history.Add(new VoiceTransition(previous, _state, _updatedAt, "disabled"));
            }
            else
                TransitionUnsafe(VoiceHostState.Listening, "enabled");
        }
    }

    public void BeginStartup()
    {
        lock (_gate)
        {
            _enabled = true;
            _pendingConfirmation = null;
            TransitionUnsafe(VoiceHostState.Starting, "startup");
        }
    }

    public void MarkReady()
    {
        lock (_gate)
        {
            if (_enabled) TransitionUnsafe(VoiceHostState.Listening, "startup-ready");
        }
    }

    public void SetPushToTalk(bool enabled) { lock (_gate) _pushToTalk = enabled; }

    public void SetDegraded(string reason)
    {
        lock (_gate)
        {
            if (!_enabled) _enabled = true;
            _pendingConfirmation = null;
            TransitionUnsafe(VoiceHostState.Degraded, reason);
        }
    }

    public bool TryWake()
    {
        lock (_gate)
        {
            if (!_enabled || _state is VoiceHostState.Degraded or VoiceHostState.Cooldown or VoiceHostState.Responding) return false;
            return TransitionUnsafe(VoiceHostState.WakeDetected, "wake-word");
        }
    }

    public bool BeginCapture() { lock (_gate) return TransitionUnsafe(VoiceHostState.Capturing, "capture-started"); }

    public bool BeginRecognition(string text)
    {
        lock (_gate)
        {
            _lastText = text;
            return TransitionUnsafe(VoiceHostState.Recognizing, "recognizing");
        }
    }

    public bool ReturnToListening(string reason = "capture-cancelled")
    {
        lock (_gate)
        {
            _pendingConfirmation = null;
            return TransitionUnsafe(VoiceHostState.Listening, reason);
        }
    }

    public bool RequestConfirmation(VoiceCommand command)
    {
        lock (_gate)
        {
            _pendingConfirmation = command.Intent.ToString();
            return TransitionUnsafe(VoiceHostState.Confirming, "confirmation-required");
        }
    }

    public string? PendingConfirmation { get { lock (_gate) return _pendingConfirmation; } }

    public bool TryExecute(VoiceCommand command)
    {
        lock (_gate)
        {
            if (!_enabled) return false;
            _pendingConfirmation = null;
            return TransitionUnsafe(VoiceHostState.Executing, "execute:" + command.Intent);
        }
    }

    public bool TryRespond(string response, VoiceHostState? returnState = null)
    {
        lock (_gate)
        {
            _lastResponse = response;
            _responseReturnState = returnState ?? (_state == VoiceHostState.Confirming ? VoiceHostState.Confirming : VoiceHostState.Listening);
            return TransitionUnsafe(VoiceHostState.Responding, "response");
        }
    }

    public void FinishResponse()
    {
        lock (_gate)
        {
            if (!_enabled) { TransitionUnsafe(VoiceHostState.Disabled, "response-finished-disabled"); return; }
            if (_responseReturnState is VoiceHostState.Confirming or VoiceHostState.Capturing)
            {
                var target = _responseReturnState;
                TransitionUnsafe(target, target == VoiceHostState.Confirming ? "response-finished-confirmation-prompt" : "response-finished-command-listening");
                return;
            }
            TransitionUnsafe(VoiceHostState.Cooldown, "response-finished");
        }
    }
    public void FinishCooldown() { lock (_gate) if (_enabled && _state == VoiceHostState.Cooldown) TransitionUnsafe(VoiceHostState.Listening, "cooldown-finished"); }

    public bool CancelConfirmation()
    {
        lock (_gate)
        {
            if (_state != VoiceHostState.Confirming) return false;
            _pendingConfirmation = null;
            return TransitionUnsafe(VoiceHostState.Listening, "confirmation-cancelled");
        }
    }

    private bool TransitionUnsafe(VoiceHostState next, string reason)
    {
        if (_state == next) return true;
        if (!Allowed(_state, next)) return false;
        var previous = _state;
        _state = next;
        _updatedAt = DateTimeOffset.UtcNow;
        _history.Add(new VoiceTransition(previous, next, _updatedAt, reason));
        return true;
    }

    private VoiceHostSnapshot CreateSnapshot() => new(
        _state,
        _enabled,
        _pushToTalk,
        _lastText,
        _lastResponse,
        _pendingConfirmation,
        _updatedAt,
        HeartbeatAtUtc: _heartbeatAtUtc);

    private static bool Allowed(VoiceHostState from, VoiceHostState to) => (from, to) switch
    {
        (VoiceHostState.Disabled, VoiceHostState.Starting or VoiceHostState.Listening or VoiceHostState.Degraded) => true,
        (VoiceHostState.Starting, VoiceHostState.Listening or VoiceHostState.Degraded or VoiceHostState.Disabled) => true,
        (VoiceHostState.Listening, VoiceHostState.WakeDetected or VoiceHostState.Disabled or VoiceHostState.Degraded or VoiceHostState.Confirming or VoiceHostState.Responding) => true,
        (VoiceHostState.WakeDetected, VoiceHostState.Capturing or VoiceHostState.Listening or VoiceHostState.Degraded) => true,
        (VoiceHostState.Capturing, VoiceHostState.Recognizing or VoiceHostState.Listening or VoiceHostState.Responding) => true,
        (VoiceHostState.Recognizing, VoiceHostState.Confirming or VoiceHostState.Executing or VoiceHostState.Responding or VoiceHostState.Listening) => true,
        (VoiceHostState.Confirming, VoiceHostState.Executing or VoiceHostState.Listening or VoiceHostState.Responding) => true,
        (VoiceHostState.Executing, VoiceHostState.Responding or VoiceHostState.Listening) => true,
        (VoiceHostState.Responding, VoiceHostState.Cooldown or VoiceHostState.Confirming or VoiceHostState.Capturing or VoiceHostState.Disabled) => true,
        (VoiceHostState.Cooldown, VoiceHostState.Listening or VoiceHostState.Disabled) => true,
        (VoiceHostState.Degraded, VoiceHostState.Listening or VoiceHostState.Disabled) => true,
        (VoiceHostState.Error, VoiceHostState.Listening or VoiceHostState.Disabled or VoiceHostState.Degraded) => true,
        _ => false
    };
}

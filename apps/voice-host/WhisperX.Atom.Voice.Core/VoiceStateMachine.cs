namespace WhisperX.Atom.Voice;

public sealed class VoiceStateMachine
{
    private readonly object _gate = new();
    private readonly List<VoiceTransition> _history = [];
    private VoiceHostState _state = VoiceHostState.Disabled;
    private bool _enabled;
    private string? _lastText;
    private string? _lastResponse;
    private string? _pendingConfirmation;
    private bool _pushToTalk;

    public VoiceHostSnapshot Snapshot { get { lock (_gate) return CreateSnapshot(); } }
    public IReadOnlyList<VoiceTransition> History { get { lock (_gate) return _history.ToArray(); } }

    public void Enable(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _pendingConfirmation = null;
            TransitionUnsafe(enabled ? VoiceHostState.Listening : VoiceHostState.Disabled, enabled ? "enabled" : "disabled");
        }
    }

    public void SetPushToTalk(bool enabled) { lock (_gate) _pushToTalk = enabled; }

    public bool TryWake()
    {
        lock (_gate)
        {
            if (!_enabled || _state is VoiceHostState.Cooldown or VoiceHostState.Responding) return false;
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

    public bool TryRespond(string response)
    {
        lock (_gate)
        {
            _lastResponse = response;
            return TransitionUnsafe(VoiceHostState.Responding, "response");
        }
    }

    public void FinishResponse() { lock (_gate) TransitionUnsafe(_enabled ? VoiceHostState.Cooldown : VoiceHostState.Disabled, "response-finished"); }
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
        _history.Add(new VoiceTransition(previous, next, DateTimeOffset.UtcNow, reason));
        return true;
    }

    private VoiceHostSnapshot CreateSnapshot() => new(_state, _enabled, _pushToTalk, _lastText, _lastResponse, _pendingConfirmation, DateTimeOffset.UtcNow);

    private static bool Allowed(VoiceHostState from, VoiceHostState to) => (from, to) switch
    {
        (VoiceHostState.Disabled, VoiceHostState.Listening) => true,
        (VoiceHostState.Listening, VoiceHostState.WakeDetected or VoiceHostState.Disabled) => true,
        (VoiceHostState.WakeDetected, VoiceHostState.Capturing or VoiceHostState.Listening) => true,
        (VoiceHostState.Capturing, VoiceHostState.Recognizing or VoiceHostState.Listening) => true,
        (VoiceHostState.Recognizing, VoiceHostState.Confirming or VoiceHostState.Executing or VoiceHostState.Responding or VoiceHostState.Listening) => true,
        (VoiceHostState.Confirming, VoiceHostState.Executing or VoiceHostState.Listening or VoiceHostState.Responding) => true,
        (VoiceHostState.Executing, VoiceHostState.Responding or VoiceHostState.Listening) => true,
        (VoiceHostState.Responding, VoiceHostState.Cooldown or VoiceHostState.Disabled) => true,
        (VoiceHostState.Cooldown, VoiceHostState.Listening or VoiceHostState.Disabled) => true,
        (VoiceHostState.Error, VoiceHostState.Listening or VoiceHostState.Disabled) => true,
        _ => false
    };
}

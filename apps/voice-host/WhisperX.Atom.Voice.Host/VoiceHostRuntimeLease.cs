namespace WhisperX.Atom.Voice.Host;

/// <summary>Cross-session single-instance lease for the managed Voice Host.</summary>
internal sealed class VoiceHostRuntimeLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _released;

    private VoiceHostRuntimeLease(Mutex mutex) => _mutex = mutex;

    public static VoiceHostRuntimeLease? TryAcquire()
    {
        try
        {
            var global = new Mutex(true, @"Global\WhisperXAtomVoiceHost", out var ownsGlobal);
            if (ownsGlobal) return new VoiceHostRuntimeLease(global);
            global.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Some locked-down Windows profiles deny Global\ object creation;
            // retain the same single-instance guarantee within the user session.
            var local = new Mutex(true, @"Local\WhisperXAtomVoiceHost", out var ownsLocal);
            if (ownsLocal) return new VoiceHostRuntimeLease(local);
            local.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

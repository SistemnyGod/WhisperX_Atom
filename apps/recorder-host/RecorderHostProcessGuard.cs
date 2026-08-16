namespace WhisperX.Atom.Recorder.Host;

public sealed class RecorderHostProcessGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _held;

    private RecorderHostProcessGuard(Mutex mutex)
    {
        _mutex = mutex;
        _held = true;
    }

    public static RecorderHostProcessGuard? TryAcquire()
    {
        // Keep the process guard on the same canonical spool identity as the
        // cross-session runtime lease. A mismatched installationId must not
        // create a second Host against the same SQLite/raw storage.
        var root = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        var identity = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        // Global scope covers RDP/fast-user-switch sessions as well. The
        // spool-scoped runtime semaphore remains the authority shared with the
        // legacy Service; this mutex prevents a second interactive Host from
        // even opening an IPC endpoint in another session.
        var mutex = new Mutex(false, $"Global\\WhisperXAtomRecorderHost-{hash}");
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous Host crashed. Windows transfers ownership to this
            // waiter and reports the abandoned state; it is safe to continue.
            acquired = true;
        }
        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }
        return new RecorderHostProcessGuard(mutex);
    }

    public void Dispose()
    {
        if (!_held) return;
        _held = false;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

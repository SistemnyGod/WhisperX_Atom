namespace WhisperX.Atom.Recorder;

/// <summary>
/// Prevents a legacy Service and the current-user Host from mutating the
/// canonical spool for the same installation at the same time. The lease is
/// acquired only while a capture session or recovery pass is active, so the
/// fallback Service can remain installed and idle during AudioGraph A/B runs.
/// </summary>
public sealed class RecorderRuntimeLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _held;

    private RecorderRuntimeLease(Mutex mutex)
    {
        _mutex = mutex;
        _held = true;
    }

    public static RecorderRuntimeLease Acquire(Guid installationId)
    {
        if (installationId == Guid.Empty)
            throw new InvalidOperationException("RECORDER_INSTALLATION_ID_REQUIRED");

        // The runtime lease is deliberately installation-scoped, not user-scoped:
        // LocalSystem Service and the current-user Host must not mutate the same
        // canonical spool concurrently. Global makes the ownership visible across
        // Windows sessions; the Host process guard remains SID-scoped separately.
        var safe = installationId.ToString("N");
        var mutex = new Mutex(false, $"Global\\WhisperXAtomRecorderRuntime-{safe}");
        if (!mutex.WaitOne(TimeSpan.Zero))
        {
            mutex.Dispose();
            throw new InvalidOperationException("RECORDER_RUNTIME_LEASE_HELD");
        }
        return new RecorderRuntimeLease(mutex);
    }

    public void Dispose()
    {
        if (!_held) return;
        _held = false;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

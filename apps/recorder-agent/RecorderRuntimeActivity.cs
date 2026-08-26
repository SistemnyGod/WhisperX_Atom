namespace WhisperX.Atom.Recorder;

/// <summary>
/// Process-local ownership marker used by the shared raw encoder.  The
/// current-user AudioGraph Host owns the marker for its lifetime; the legacy
/// Service sets it only after it has acquired the installation runtime lease.
/// This prevents a standby Service encoder from touching the canonical spool.
/// </summary>
public static class RecorderRuntimeActivity
{
    private static int _active;

    public static bool IsActive => Volatile.Read(ref _active) == 1;

    public static void SetActive(bool active) => Volatile.Write(ref _active, active ? 1 : 0);
}

using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Prevents a legacy Service and the current-user Host from mutating the
/// canonical spool for the same installation at the same time. The current-
/// user Host holds the lease for its process lifetime; the fallback Service
/// acquires the same lease only when it is the active runtime, so two spool
/// owners cannot overlap.
/// The lease uses a named semaphore rather than a mutex so it can safely be
/// held across async continuations (capture/recovery are not thread-affine).
/// </summary>
public sealed class RecorderRuntimeLease : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _held;

    private RecorderRuntimeLease(Semaphore semaphore)
    {
        _semaphore = semaphore;
        _held = true;
    }

    [SupportedOSPlatform("windows")]
    public static RecorderRuntimeLease Acquire(Guid installationId, string? dataRoot = null)
    {
        if (installationId == Guid.Empty)
            throw new InvalidOperationException("RECORDER_INSTALLATION_ID_REQUIRED");

        // The lease is scoped to the canonical spool, not only to installationId.
        // A stale user/machine configuration must never allow two runtimes to
        // mutate the same SQLite database and raw files concurrently.
        var root = ResolveCanonicalDataRoot(dataRoot);
        var rootHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
        var name = $"Global\\WhisperXAtomRecorderRuntime-{rootHash}";
        // The interactive Host and the legacy Windows Service run under
        // different identities.  An implicit creator-only DACL would make
        // whichever process starts second fail with ACCESS_DENIED instead of
        // observing the intended runtime lease. The name is derived from the
        // canonical spool path, so grant only the synchronization rights
        // required by authenticated users and SYSTEM.
        var security = new SemaphoreSecurity();
        security.AddAccessRule(new SemaphoreAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            SemaphoreRights.Synchronize | SemaphoreRights.Modify,
            AccessControlType.Allow));
        security.AddAccessRule(new SemaphoreAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            SemaphoreRights.FullControl,
            AccessControlType.Allow));
        var semaphore = SemaphoreAcl.Create(1, 1, name, out _, security);
        if (!semaphore.WaitOne(TimeSpan.Zero))
        {
            semaphore.Dispose();
            throw new InvalidOperationException("RECORDER_RUNTIME_LEASE_HELD");
        }
        return new RecorderRuntimeLease(semaphore);
    }

    private static string ResolveCanonicalDataRoot(string? dataRoot)
    {
        var configured = dataRoot;
        if (string.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(configured))
            configured = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        return Path.GetFullPath(configured.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
    }

    public void Dispose()
    {
        if (!_held) return;
        _held = false;
        try { _semaphore.Release(); } catch (SemaphoreFullException) { }
        _semaphore.Dispose();
    }
}

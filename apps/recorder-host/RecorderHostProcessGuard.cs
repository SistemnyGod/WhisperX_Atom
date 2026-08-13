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
        var sid = ResolveCurrentSid();
        var installationId = Environment.GetEnvironmentVariable("ATOM_AGENT_INSTALLATION_ID");
        if (string.IsNullOrWhiteSpace(installationId))
            installationId = TryReadMachineInstallationId();
        var identity = sid + "-" + (installationId ?? "unknown");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var mutex = new Mutex(false, $"Local\\WhisperXAtomRecorderHost-{hash}");
        if (!mutex.WaitOne(TimeSpan.Zero))
        {
            mutex.Dispose();
            return null;
        }
        return new RecorderHostProcessGuard(mutex);
    }

    private static string ResolveCurrentSid()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { return Environment.UserName; }
    }

    private static string? TryReadMachineInstallationId()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WhisperXAtom", "client-config.json");
            if (!File.Exists(path)) return null;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("installationId", out var value)
                ? value.GetString()
                : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (!_held) return;
        _held = false;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

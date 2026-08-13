namespace WhisperX.Atom.Recorder;

/// <summary>
/// Resolves the recorder process model once for a client process. The release
/// default is the current-user AudioGraph host; Legacy WASAPI is retained only
/// as an explicit, operator-controlled fallback.
/// </summary>
public sealed record RecorderRuntimeSelection(string CaptureEngine, string Source)
{
    public bool IsAudioGraph => string.Equals(CaptureEngine, "AUDIOGRAPH", StringComparison.Ordinal);
    public string PipeName => IsAudioGraph ? RecorderPipeNames.AudioGraphHost : RecorderPipeNames.LegacyService;
}

public static class RecorderRuntimeResolver
{
    public const string AudioGraph = "AUDIOGRAPH";
    public const string LegacyWasapi = "LEGACY_WASAPI";
    public const string EnvironmentVariable = "AUDIO_CAPTURE_ENGINE";

    private static readonly object Gate = new();
    private static RecorderRuntimeSelection? _current;

    /// <summary>
    /// Gets the startup selection. Desktop calls InitializeForDesktop during
    /// launch, so every later IPC/controller decision uses the same value.
    /// </summary>
    public static RecorderRuntimeSelection Current
    {
        get
        {
            lock (Gate)
                return _current ??= Resolve();
        }
    }

    public static RecorderRuntimeSelection InitializeForDesktop()
    {
        var selection = Current;
        // Existing Desktop components and child Host processes consume this
        // variable. Setting it only in the Desktop process keeps the legacy
        // Windows Service independently operable as an explicit fallback.
        Environment.SetEnvironmentVariable(EnvironmentVariable, selection.CaptureEngine, EnvironmentVariableTarget.Process);
        return selection;
    }

    public static RecorderRuntimeSelection Resolve(string? environmentOverride = null, string? machineConfigPath = null)
    {
        var explicitEngine = Normalize(environmentOverride ?? Environment.GetEnvironmentVariable(EnvironmentVariable));
        if (explicitEngine is not null)
            return new RecorderRuntimeSelection(explicitEngine, "ENVIRONMENT");

        var configuredEngine = ReadConfiguredEngine(machineConfigPath ?? MachineConfigPath);
        if (configuredEngine is not null)
            return new RecorderRuntimeSelection(configuredEngine, "MACHINE_CONFIG");

        return new RecorderRuntimeSelection(AudioGraph, "RELEASE_DEFAULT");
    }

    internal static void ResetForTests()
    {
        lock (Gate) _current = null;
    }

    private static string MachineConfigPath => MachineServerConfig.FilePath;

    private static string? ReadConfiguredEngine(string path)
    {
        var config = MachineServerConfig.Load(path);
        // A v1 file may still provide ServerOrigin, but it is not a valid
        // source for the recorder engine. Installer/config migration owns the
        // v1 -> v2 transition; until then use the release default explicitly.
        if (config is null || config.SchemaVersion != 2 || config.AudioConfiguration is null)
            return null;
        return Normalize(config.AudioConfiguration.CaptureEngine);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized is AudioGraph or LegacyWasapi ? normalized : null;
    }
}

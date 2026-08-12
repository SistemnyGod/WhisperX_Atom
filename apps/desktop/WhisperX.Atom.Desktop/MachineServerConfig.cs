using System.Text.Json;

namespace WhisperX.Atom.Desktop;

public sealed record MachineServerConfig(int SchemaVersion, string ServerOrigin, bool Managed)
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WhisperXAtom", "client-config.json");

    public static MachineServerConfig? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var value = JsonSerializer.Deserialize<MachineServerConfig>(File.ReadAllText(FilePath));
            return value is not null && Uri.TryCreate(value.ServerOrigin, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" ? value with { ServerOrigin = uri.ToString().TrimEnd('/') } : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public static string? ServerOriginOrNull() => Load()?.ServerOrigin;
    public static string? ManagedServerOriginOrNull() => Load() is { Managed: true } config ? config.ServerOrigin : null;
}

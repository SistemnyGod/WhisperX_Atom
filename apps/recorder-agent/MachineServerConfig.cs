using System.Text.Json;

namespace WhisperX.Atom.Recorder;

internal sealed record MachineServerConfig(int SchemaVersion, string ServerOrigin, bool Managed)
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WhisperXAtom", "client-config.json");

    public static string? ServerOriginOrNull()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var value = JsonSerializer.Deserialize<MachineServerConfig>(File.ReadAllText(FilePath));
            return value is not null && Uri.TryCreate(value.ServerOrigin, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" ? uri.ToString().TrimEnd('/') : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}


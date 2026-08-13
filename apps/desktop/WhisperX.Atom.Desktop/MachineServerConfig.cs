using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Desktop;

/// <summary>
/// Desktop view of the shared machine, non-secret configuration. Keep this
/// shape aligned with Recorder's contract so a Desktop settings save cannot
/// silently discard the runtime selection or installation identity.
/// </summary>
public sealed record MachineServerConfig(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("serverOrigin")] string ServerOrigin,
    [property: JsonPropertyName("managed")] bool Managed,
    [property: JsonPropertyName("installationId")] Guid? InstallationId = null,
    [property: JsonPropertyName("audioConfiguration")] AudioConfigurationV2? AudioConfiguration = null)
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WhisperXAtom", "client-config.json");

    public static MachineServerConfig? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var value = JsonSerializer.Deserialize<MachineServerConfig>(File.ReadAllText(FilePath), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            });
            if (value is null || !Uri.TryCreate(value.ServerOrigin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
                return null;

            var audio = value.AudioConfiguration;
            if (audio is not null && (audio.AudioConfigurationVersion != 2
                || string.IsNullOrWhiteSpace(audio.CaptureEngine)
                || audio.Microphone is null
                || audio.SystemAudio is null))
                return null;

            return value with { ServerOrigin = uri.ToString().TrimEnd('/'), AudioConfiguration = audio };
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public static string? ServerOriginOrNull() => Load()?.ServerOrigin;
    public static string? ManagedServerOriginOrNull() => Load() is { Managed: true } config ? config.ServerOrigin : null;
}

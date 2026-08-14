using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// The non-secret machine contract shared by Desktop, Recorder Host and the
/// legacy Service.  Version 1 is read for upgrade compatibility only; callers
/// must preserve all recognised v2 fields when writing the file.
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

    public static string? ServerOriginOrNull()
    {
        return Load()?.ServerOrigin;
    }

    public static MachineServerConfig? Load(string? path = null)
    {
        try
        {
            var source = path ?? FilePath;
            if (!File.Exists(source)) return null;
            var value = JsonSerializer.Deserialize<MachineServerConfig>(File.ReadAllText(source), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            });
            if (value is null || !Uri.TryCreate(value.ServerOrigin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return null;

            var audio = NormalizeAudio(value.AudioConfiguration);
            var managed = value.Managed && !(uri.IsLoopback && uri.Port == 0);
            return value with { ServerOrigin = uri.ToString().TrimEnd('/'), Managed = managed, AudioConfiguration = audio };
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static AudioConfigurationV2 DefaultAudioConfiguration => AudioConfigurationV2.FromCurrent(
        microphoneId: null,
        systemAudioId: null,
        engine: RecorderRuntimeResolver.AudioGraph);

    private static AudioConfigurationV2? NormalizeAudio(AudioConfigurationV2? value)
    {
        if (value is null) return null;
        if (value.AudioConfigurationVersion != 2 || string.IsNullOrWhiteSpace(value.CaptureEngine)
            || value.Microphone is null || value.SystemAudio is null)
            return null;
        return value;
    }
}

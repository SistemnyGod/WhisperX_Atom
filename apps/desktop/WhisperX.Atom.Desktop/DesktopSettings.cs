using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WhisperX.Atom.Desktop;

public sealed record DesktopSettings(
    string ApiUrl,
    string Username,
    string? ProtectedSessionCookie,
    string? ArchiveRoot = null,
    string? MicrophoneDeviceId = null,
    string? SystemAudioDeviceId = null,
    DateTimeOffset? SessionExpiresAtUtc = null,
    string? RecordingProfile = "ROOM",
    Guid? OwnerUserId = null,
    bool AgentBootstrapConfirmed = false,
    bool VoiceAlwaysListening = true,
    bool VoiceQuietMode = false,
    string VoiceSensitivity = "balanced",
    string AcousticProfile = "AUTO",
    string VoiceName = "Microsoft Irina",
    int VoiceRate = 0,
    int VoiceVolume = 90,
    string UpdateChannel = "stable",
    string TtsEngine = "SILERO",
    string TtsVoice = "aidar",
    int TtsSampleRate = 48000,
    int TtsCpuThreads = 4,
    bool TtsFallbackEnabled = true,
    string WindowsFallbackVoice = "Microsoft Irina",
    int VoiceProcessingGainDb = 0,
    string TtsVoiceProfile = "MIFODIY_TECH")
{
    private const string UnconfiguredApiUrl = "http://127.0.0.1:0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperXAtom", "desktop-settings.json");

    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return CreateDefault();
            var raw = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<DesktopSettings>(raw, JsonOptions)
                ?? CreateDefault();
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("windowsFallbackVoice", out _))
                loaded = loaded with { WindowsFallbackVoice = string.IsNullOrWhiteSpace(loaded.VoiceName) ? "Microsoft Irina" : loaded.VoiceName };
            if (!document.RootElement.TryGetProperty("ttsVoiceProfile", out _))
                loaded = loaded with { TtsVoiceProfile = string.Equals(loaded.TtsVoice, "aidar", StringComparison.OrdinalIgnoreCase) ? "MIFODIY_TECH" : "CLEAN" };
            else
                loaded = loaded with { TtsVoiceProfile = NormalizeTtsVoiceProfile(loaded.TtsVoiceProfile) };
            return MigrateApiUrl(loaded);
        }
        catch (IOException) { return CreateDefault(); }
        catch (JsonException) { return CreateDefault(); }
    }

    public static string DefaultApiUrl() => MachineServerConfig.ManagedServerOriginOrNull()
        ?? ReadHttpUrlEnvironment("WHISPERX_API_URL")
        ?? UnconfiguredApiUrl;

    public static void Save(string apiUrl, string username, string? sessionCookie, string? archiveRoot = null, string? microphoneDeviceId = null, string? systemAudioDeviceId = null, DateTimeOffset? sessionExpiresAtUtc = null, string? recordingProfile = "ROOM", Guid? ownerUserId = null, bool agentBootstrapConfirmed = false, bool voiceAlwaysListening = true, bool voiceQuietMode = false, string voiceSensitivity = "balanced", string acousticProfile = "AUTO", string voiceName = "Microsoft Irina", int voiceRate = 0, int voiceVolume = 90, string? updateChannel = null, string ttsEngine = "SILERO", string ttsVoice = "aidar", int ttsSampleRate = 48000, int ttsCpuThreads = 4, bool ttsFallbackEnabled = true, string windowsFallbackVoice = "Microsoft Irina", int voiceProcessingGainDb = 0, string ttsVoiceProfile = "MIFODIY_TECH")
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var settings = new DesktopSettings(apiUrl.TrimEnd('/'), username.Trim(),
            string.IsNullOrWhiteSpace(sessionCookie) ? null : Protect(sessionCookie),
            string.IsNullOrWhiteSpace(archiveRoot) ? DefaultArchiveRoot() : Path.GetFullPath(archiveRoot.Trim()),
            NormalizeDeviceId(microphoneDeviceId),
            NormalizeDeviceId(systemAudioDeviceId),
            sessionExpiresAtUtc,
            NormalizeRecordingProfile(recordingProfile),
            ownerUserId,
            agentBootstrapConfirmed,
            voiceAlwaysListening,
            voiceQuietMode,
            NormalizeVoiceSensitivity(voiceSensitivity),
            NormalizeAcousticProfile(acousticProfile),
            string.IsNullOrWhiteSpace(voiceName) ? "Microsoft Irina" : voiceName.Trim(),
            Math.Clamp(voiceRate, -10, 10),
            Math.Clamp(voiceVolume, 0, 100),
            NormalizeUpdateChannel(updateChannel),
            NormalizeTtsEngine(ttsEngine),
            string.IsNullOrWhiteSpace(ttsVoice) ? "aidar" : ttsVoice.Trim().ToLowerInvariant(),
            ttsSampleRate is 24000 or 48000 ? ttsSampleRate : 48000,
            Math.Clamp(ttsCpuThreads, 1, 32),
            ttsFallbackEnabled,
            string.IsNullOrWhiteSpace(windowsFallbackVoice) ? "Microsoft Irina" : windowsFallbackVoice.Trim(),
            Math.Clamp(voiceProcessingGainDb, 0, 18),
            NormalizeTtsVoiceProfile(ttsVoiceProfile));
        var temporary = FilePath + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, FilePath, true);
    }

    public static string DefaultArchiveRoot()
    {
        // Documents is commonly protected by Windows Defender Controlled Folder Access.
        // Keep the archive user-visible, but outside Documents so the Recorder Service
        // can write it without requiring a broad antivirus exception.
        var commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var publicRoot = Directory.GetParent(commonDocuments)?.FullName;
        return Path.Combine(
            string.IsNullOrWhiteSpace(publicRoot) ? commonDocuments : publicRoot,
            "WhisperX Atom");
    }

    public string? UnprotectSessionCookie()
    {
        if (string.IsNullOrWhiteSpace(ProtectedSessionCookie)) return null;
        try { return Unprotect(ProtectedSessionCookie); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    private static string? NormalizeDeviceId(string? deviceId) => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();

    private static string NormalizeRecordingProfile(string? profile)
    {
        var normalized = string.IsNullOrWhiteSpace(profile) ? "ROOM" : profile.Trim().ToUpperInvariant();
        return normalized is "ROOM" or "ONLINE" or "MIC_ONLY" or "SYSTEM_ONLY" ? normalized : "ROOM";
    }

    private static string NormalizeVoiceSensitivity(string? sensitivity)
    {
        var normalized = sensitivity?.Trim().ToLowerInvariant();
        return normalized is "low" or "high" or "balanced" ? normalized : "balanced";
    }

    private static string NormalizeAcousticProfile(string? profile)
    {
        var normalized = profile?.Trim().ToUpperInvariant();
        return normalized is "AUTO" or "STANDARD" or "LARGE_ROOM" ? normalized : "AUTO";
    }

    private static string NormalizeUpdateChannel(string? channel)
        => string.Equals(channel?.Trim(), "pilot", StringComparison.OrdinalIgnoreCase) ? "pilot" : "stable";

    private static string NormalizeTtsVoiceProfile(string? profile)
    {
        if (string.Equals(profile?.Trim(), "AIDAR_CLEAN", StringComparison.OrdinalIgnoreCase)) return "AIDAR_CLEAN";
        if (string.Equals(profile?.Trim(), "CLEAN", StringComparison.OrdinalIgnoreCase)) return "CLEAN";
        if (string.Equals(profile?.Trim(), "JARVIS_RU", StringComparison.OrdinalIgnoreCase)) return "JARVIS_RU";
        if (string.Equals(profile?.Trim(), "JARVIS_EN", StringComparison.OrdinalIgnoreCase)) return "JARVIS_EN";
        return "MIFODIY_TECH";
    }

    private static string NormalizeTtsEngine(string? engine)
    {
        if (string.Equals(engine?.Trim(), "WINDOWS", StringComparison.OrdinalIgnoreCase)) return "WINDOWS";
        if (string.Equals(engine?.Trim(), "PIPER_JARVIS", StringComparison.OrdinalIgnoreCase)) return "PIPER_JARVIS";
        return "SILERO";
    }

    private static DesktopSettings CreateDefault() => new(DefaultApiUrl(), "admin", null, DefaultArchiveRoot());

    private static DesktopSettings MigrateApiUrl(DesktopSettings settings)
    {
        string configuredUrl = settings.ApiUrl?.TrimEnd('/') ?? string.Empty;
        var defaultUrl = DefaultApiUrl();
        var machineConfig = MachineServerConfig.Load();
        if (machineConfig?.Managed == true)
            configuredUrl = machineConfig.ServerOrigin.TrimEnd('/');
        // A damaged or partially-written settings file must never prevent the
        // shell from starting: ServerApiClient validates its base URI in the
        // constructor. Treat malformed values exactly like an unconfigured
        // origin and fall back to the managed/environment source.
        if (!IsHttpUrl(configuredUrl) || IsUnconfiguredUrl(configuredUrl))
        {
            configuredUrl = defaultUrl;
        }

        if (string.Equals(settings.ApiUrl, configuredUrl, StringComparison.Ordinal)) return settings;

        var migrated = settings with { ApiUrl = configuredUrl };
        try
        {
            var sessionCookie = migrated.UnprotectSessionCookie();
            // Do not overwrite a protected session that cannot be decrypted in
            // the current Windows user context.
            if (string.IsNullOrWhiteSpace(migrated.ProtectedSessionCookie) || !string.IsNullOrWhiteSpace(sessionCookie))
            {
                Save(migrated.ApiUrl, migrated.Username, sessionCookie, migrated.ArchiveRoot,
                    migrated.MicrophoneDeviceId, migrated.SystemAudioDeviceId, migrated.SessionExpiresAtUtc, migrated.RecordingProfile, migrated.OwnerUserId, migrated.AgentBootstrapConfirmed, migrated.VoiceAlwaysListening, migrated.VoiceQuietMode, migrated.VoiceSensitivity, migrated.AcousticProfile, migrated.VoiceName, migrated.VoiceRate, migrated.VoiceVolume, migrated.UpdateChannel, migrated.TtsEngine, migrated.TtsVoice, migrated.TtsSampleRate, migrated.TtsCpuThreads, migrated.TtsFallbackEnabled, migrated.WindowsFallbackVoice, migrated.VoiceProcessingGainDb, migrated.TtsVoiceProfile);
            }
        }
        catch
        {
            // The in-memory migration is still useful; persistence will retry
            // after the next successful login or settings save.
        }
        return migrated;
    }

    private static string? ReadHttpUrlEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString().TrimEnd('/')
            : null;
    }

    private static bool IsUnconfiguredUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == 0;

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string Protect(string value)
    {
        var bytes = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}

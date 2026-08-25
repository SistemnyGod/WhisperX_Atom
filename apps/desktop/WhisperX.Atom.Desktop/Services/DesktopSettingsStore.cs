using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

public sealed class DesktopSettingsStore : ISettingsStore
{
    public DesktopSettings Load() => DesktopSettings.Load();

    public void Save(DesktopSettings settings)
    {
        DesktopSettings.Save(
            settings.ApiUrl,
            settings.Username,
            settings.UnprotectSessionCookie(),
            settings.ArchiveRoot,
            settings.MicrophoneDeviceId,
            settings.SystemAudioDeviceId,
            settings.SessionExpiresAtUtc,
            settings.RecordingProfile,
            settings.OwnerUserId,
            settings.AgentBootstrapConfirmed,
            settings.VoiceAlwaysListening,
            settings.VoiceQuietMode,
            settings.VoiceSensitivity,
            settings.AcousticProfile,
            settings.VoiceName,
            settings.VoiceRate,
            settings.VoiceVolume,
            settings.UpdateChannel,
            settings.TtsEngine,
            settings.TtsVoice,
            settings.TtsSampleRate,
            settings.TtsCpuThreads,
            settings.TtsFallbackEnabled,
            settings.WindowsFallbackVoice,
            settings.VoiceProcessingGainDb,
            settings.TtsVoiceProfile);
    }
}

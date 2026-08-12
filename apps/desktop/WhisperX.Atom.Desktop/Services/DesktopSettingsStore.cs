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
            settings.OwnerUserId);
    }
}

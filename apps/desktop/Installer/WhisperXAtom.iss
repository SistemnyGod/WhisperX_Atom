; Build after running scripts/publish-desktop.ps1
#define AppName "WhisperX Atom"
#define AppVersion "0.1.0"

[Setup]
AppId={{B6C9F93C-20E5-4E31-9E86-4A7B119D1B01}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\WhisperX Atom
DefaultGroupName={#AppName}
OutputDir=..\..\..\artifacts\installer
OutputBaseFilename=WhisperXAtom-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName={#AppName}

[Files]
Source: "..\..\..\artifacts\desktop\Desktop\*"; DestDir: "{app}\Desktop"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\desktop\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\desktop\RecorderHost\*"; DestDir: "{app}\RecorderHost"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\desktop\VoiceHost\*"; DestDir: "{app}\VoiceHost"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall-Service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Configure-VoiceUser.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Configure-RecorderHostUser.ps1"; DestDir: "{app}"; Flags: ignoreversion


[Icons]
Name: "{group}\WhisperX Atom"; Filename: "{app}\Desktop\WhisperX.Atom.Desktop.exe"
Name: "{commondesktop}\WhisperX Atom"; Filename: "{app}\Desktop\WhisperX.Atom.Desktop.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Configure-VoiceUser.ps1"" -VoiceHostPath ""{app}\VoiceHost\WhisperX.Atom.Voice.Host.exe"" -SidFile ""{commonappdata}\WhisperXAtom\installer-user.sid"""; Flags: runasoriginaluser runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Install-Service.ps1"" -RecorderHostDirectory ""{app}\RecorderHost"" -AllowedUserSidFile ""{commonappdata}\WhisperXAtom\installer-user.sid"""; Flags: waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Configure-RecorderHostUser.ps1"" -RecorderHostPath ""{app}\RecorderHost\WhisperX.Atom.Recorder.Host.exe"""; Flags: runasoriginaluser runhidden waituntilterminated
Filename: "{app}\VoiceHost\WhisperX.Atom.Voice.Host.exe"; Parameters: "--doctor"; Flags: runasoriginaluser runhidden waituntilterminated
Filename: "{app}\VoiceHost\WhisperX.Atom.Voice.Host.exe"; Flags: runasoriginaluser nowait runhidden
Filename: "{app}\Desktop\WhisperX.Atom.Desktop.exe"; Description: "Launch WhisperX Atom"; Flags: runasoriginaluser nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Configure-VoiceUser.ps1"" -Remove -SidFile ""{commonappdata}\WhisperXAtom\installer-user.sid"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveWhisperXAtomVoiceUser"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-Service.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveWhisperXAtomService"

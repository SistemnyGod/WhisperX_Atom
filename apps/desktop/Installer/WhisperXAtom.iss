; Build after running scripts/publish-desktop.ps1
#define AppName "WhisperX Atom"
#define AppVersion "1.0.1"

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
Source: "Configure-RecorderHostUser.ps1"; DestDir: "{app}"; Flags: ignoreversion


[Icons]
Name: "{group}\WhisperX Atom"; Filename: "{app}\Desktop\WhisperX.Atom.Desktop.exe"
Name: "{commondesktop}\WhisperX Atom"; Filename: "{app}\Desktop\WhisperX.Atom.Desktop.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Install-Service.ps1"" -RecorderHostDirectory ""{app}\RecorderHost"" -AllowedUserSidFile ""{commonappdata}\WhisperXAtom\installer-user.sid"""; Flags: waituntilterminated
; The per-user config script is deliberately fire-and-forget. Inno Setup is
; elevated, while this script must run in the original user's DPAPI/profile
; scope; waiting for the runasoriginaluser hand-off can deadlock the UAC
; broker and leave the installer looking hung after all files were copied.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Configure-RecorderHostUser.ps1"" -RecorderHostPath ""{app}\RecorderHost\WhisperX.Atom.Recorder.Host.exe"""; Flags: runasoriginaluser runhidden nowait
; Do not auto-launch Desktop from the elevated Setup process. A Desktop
; process started here can inherit a high-integrity token after UAC and then
; start a high-integrity Recorder Host, which blocks the normal user Desktop
; from opening the Host pipe. The user starts the installed shortcut after
; Setup has exited, guaranteeing a matching medium-integrity user session.

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-Service.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveWhisperXAtomService"

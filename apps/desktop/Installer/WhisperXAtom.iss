; Build after running scripts/publish-desktop.ps1
#define AppName "WhisperX Atom"
#define AppVersion "1.0.1"
#ifndef ServerOrigin
#define ServerOrigin "http://192.168.2.194:8080"
#endif

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
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=WhisperX.Atom.*.exe
RestartApplications=no

[Files]
Source: "..\..\..\artifacts\desktop\Desktop\*"; DestDir: "{app}\Desktop"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\desktop\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\desktop\RecorderHost\*"; DestDir: "{app}\RecorderHost"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\desktop\VoiceHost\*"; DestDir: "{app}\VoiceHost"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\desktop\VoiceRefinerHost\*"; DestDir: "{app}\VoiceRefinerHost"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\desktop\TtsHost\*"; DestDir: "{app}\TtsHost"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall-Service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Configure-RecorderHostUser.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\..\artifacts\desktop\build-identity.json"; DestDir: "{app}\Desktop"; Flags: ignoreversion
; Extracted to {tmp} and executed by PrepareToInstall before any installed
; binaries can be replaced. This is intentionally not copied into {app}.
Source: "Preflight-Upgrade.ps1"; Flags: dontcopy


[Icons]
Name: "{group}\WhisperX Atom"; Filename: "{app}\Desktop\WhisperX.Atom.Desktop.exe"; IconFilename: "{app}\Desktop\Assets\AppIcon.ico"
Name: "{commondesktop}\WhisperX Atom"; Filename: "{app}\Desktop\WhisperX.Atom.Desktop.exe"; IconFilename: "{app}\Desktop\Assets\AppIcon.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ScriptPath, Params: String;
  ResultCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('Preflight-Upgrade.ps1');
  ScriptPath := ExpandConstant('{tmp}\Preflight-Upgrade.ps1');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" -InstalledRoot "' + ExpandConstant('{app}') + '"';
  // Always run the native 64-bit PowerShell on x64 Windows.  The installer
  // executable itself may be 32-bit even when ArchitecturesInstallIn64BitMode
  // is enabled; using {sys} in that process can resolve to SysWOW64 and the
  // preflight then cannot inspect the 64-bit Recorder Host (false
  // INSTALL_BLOCKED_UNINSPECTABLE_PROCESS).
  if not Exec(ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'INSTALL_PREFLIGHT_FAILED_TO_START'
  else if ResultCode <> 0 then
    Result := 'INSTALL_PREFLIGHT_REJECTED_' + IntToStr(ResultCode);
end;

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Install-Service.ps1"" -ServerOrigin ""{#ServerOrigin}"" -RecorderHostDirectory ""{app}\RecorderHost"" -AllowedUserSidFile ""{commonappdata}\WhisperXAtom\installer-user.sid"""; Flags: waituntilterminated
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

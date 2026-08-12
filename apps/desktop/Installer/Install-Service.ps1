param(
    [string]$ServiceDirectory = (Join-Path $PSScriptRoot "Service"),
    [string]$AllowedUserSid,
    [string]$AllowedUserSidFile
)
$ErrorActionPreference = "Stop"
$serviceName = "WhisperXAtomRecorder"
$displayName = "WhisperX Atom Recorder Service"
$serviceExe = Join-Path $ServiceDirectory "WhisperX.Atom.Recorder.Service.exe"
if (-not (Test-Path -LiteralPath $serviceExe)) { throw "Service binary not found: $serviceExe" }
if ([string]::IsNullOrWhiteSpace($AllowedUserSid) -and -not [string]::IsNullOrWhiteSpace($AllowedUserSidFile)) {
    if (-not (Test-Path -LiteralPath $AllowedUserSidFile -PathType Leaf)) { throw "Installer user SID file not found." }
    $AllowedUserSid = (Get-Content -LiteralPath $AllowedUserSidFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($AllowedUserSid)) { throw "Installer user SID is required." }
$ffmpeg = Join-Path $ServiceDirectory "ffmpeg.exe"
$ffprobe = Join-Path $ServiceDirectory "ffprobe.exe"
if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf) -or -not (Test-Path -LiteralPath $ffprobe -PathType Leaf)) {
    throw "Pinned FFmpeg payload is missing. Include ffmpeg.exe and ffprobe.exe in the Service directory and rerun the installer."
}
[Environment]::SetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH", $ffmpeg, "Machine")
[Environment]::SetEnvironmentVariable("ATOM_AGENT_FFPROBE_PATH", $ffprobe, "Machine")
try { [void][System.Security.Principal.SecurityIdentifier]::new($AllowedUserSid) } catch { throw "Invalid installer user SID: $AllowedUserSid" }
[Environment]::SetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID", $AllowedUserSid, "Machine")
$agentDataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "WhisperXAtom\Agent"
New-Item -ItemType Directory -Force -Path $agentDataRoot | Out-Null
Set-Content -LiteralPath (Join-Path $agentDataRoot "allowed-user.sid") -Value $AllowedUserSid -Encoding ascii -NoNewline
$agentConfigPath = Join-Path $agentDataRoot "agent-config.json"
$serverOrigin = if ([string]::IsNullOrWhiteSpace($env:WHISPERX_API_URL)) { "http://192.168.2.194:8080" } else { $env:WHISPERX_API_URL }
if (-not (Test-Path -LiteralPath $agentConfigPath -PathType Leaf)) {
    $agentConfig = [ordered]@{
        ServerUrl = $serverOrigin.TrimEnd('/')
        AgentId = ""
        Token = ""
        Encrypted = $false
        InstallationId = [Guid]::NewGuid()
        RecordingProfile = "ROOM"
    } | ConvertTo-Json
    $agentConfigPart = "$agentConfigPath.part"
    Set-Content -LiteralPath $agentConfigPart -Value $agentConfig -Encoding utf8
    Move-Item -LiteralPath $agentConfigPart -Destination $agentConfigPath -Force
}
$machineConfigPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "WhisperXAtom\client-config.json"
if (-not (Test-Path -LiteralPath $machineConfigPath -PathType Leaf)) {
    $machineConfig = [ordered]@{ schemaVersion = 1; serverOrigin = $serverOrigin.TrimEnd('/'); managed = $true } | ConvertTo-Json
    $machineConfigPart = "$machineConfigPath.part"
    Set-Content -LiteralPath $machineConfigPart -Value $machineConfig -Encoding utf8
    Move-Item -LiteralPath $machineConfigPart -Destination $machineConfigPath -Force
}
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.Status -ne "Stopped") { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}
New-Service -Name $serviceName -DisplayName $displayName -Description "Local-first WhisperX Atom recording service" -BinaryPathName "`"$serviceExe`"" -StartupType Automatic | Out-Null
sc.exe config $serviceName start= delayed-auto | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
Start-Service -Name $serviceName
Write-Host "Installed and started $displayName"

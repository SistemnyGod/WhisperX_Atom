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
# The user-session Recorder Host shares the canonical spool with the Legacy
# Service. Grant only the installing user's Modify access; do not make the
# durable audio root globally writable.
$userAcl = "*$($AllowedUserSid):(OI)(CI)M"
$aclOutput = @(& icacls.exe $agentDataRoot /grant $userAcl /T /C 2>&1)
$aclText = ($aclOutput -join "`n")
if ($LASTEXITCODE -ne 0 -or $aclText -match "Failed processing [1-9][0-9]* files") {
    throw "AGENT_DATA_ROOT_ACL_FAILED: $agentDataRoot`n$aclText"
}
try {
    $acl = Get-Acl -LiteralPath $agentDataRoot
    $hasUserModify = @($acl.Access | Where-Object {
        $_.IdentityReference.Value -eq $AllowedUserSid -and $_.FileSystemRights.ToString().IndexOf("Modify", [StringComparison]::OrdinalIgnoreCase) -ge 0
    }).Count -gt 0
    if (-not $hasUserModify) { throw "user SID Modify ACE was not found" }
}
catch { throw "AGENT_DATA_ROOT_ACL_FAILED: $agentDataRoot ($($_.Exception.Message))" }
Set-Content -LiteralPath (Join-Path $agentDataRoot "allowed-user.sid") -Value $AllowedUserSid -Encoding ascii -NoNewline
$agentConfigPath = Join-Path $agentDataRoot "agent-config.json"
$serverOrigin = if ([string]::IsNullOrWhiteSpace($env:WHISPERX_API_URL)) { "http://192.168.2.194:8080" } else { $env:WHISPERX_API_URL }
$machineConfigPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "WhisperXAtom\client-config.json"
$installationId = [Guid]::NewGuid()
foreach ($candidatePath in @($machineConfigPath, $agentConfigPath)) {
    if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
        try {
            $candidate = Get-Content -LiteralPath $candidatePath -Raw | ConvertFrom-Json
            $candidateValue = if ($candidate.installationId) { $candidate.installationId } else { $candidate.InstallationId }
            $parsed = [Guid]::Empty
            if ([Guid]::TryParse([string]$candidateValue, [ref]$parsed) -and $parsed -ne [Guid]::Empty) { $installationId = $parsed; break }
        } catch { }
    }
}
if (-not (Test-Path -LiteralPath $agentConfigPath -PathType Leaf)) {
    $agentConfig = [ordered]@{
        ServerUrl = $serverOrigin.TrimEnd('/')
        AgentId = ""
        Token = ""
        Encrypted = $false
        InstallationId = $installationId
        RecordingProfile = "ROOM"
    } | ConvertTo-Json
    $agentConfigPart = "$agentConfigPath.part"
    Set-Content -LiteralPath $agentConfigPart -Value $agentConfig -Encoding utf8
    Move-Item -LiteralPath $agentConfigPart -Destination $agentConfigPath -Force
}
if (-not (Test-Path -LiteralPath $machineConfigPath -PathType Leaf)) {
    $machineConfig = [ordered]@{
        schemaVersion = 2
        serverOrigin = $serverOrigin.TrimEnd('/')
        managed = $true
        installationId = $installationId
        audioConfiguration = [ordered]@{
            audioConfigurationVersion = 2
            captureEngine = "LEGACY_WASAPI"
            microphone = [ordered]@{ selectionMode = "DEFAULT"; deviceId = $null }
            systemAudio = [ordered]@{ selectionMode = "DEFAULT"; deviceId = $null }
            userReselectRequired = $false
        }
    } | ConvertTo-Json -Depth 8
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

param(
    [string]$ServiceDirectory = (Join-Path $PSScriptRoot "Service"),
    [string]$RecorderHostDirectory,
    [string]$AllowedUserSid,
    [string]$AllowedUserSidFile
)
$ErrorActionPreference = "Stop"
$serviceName = "WhisperXAtomRecorder"
$displayName = "WhisperX Atom Recorder Service"
$serviceExe = Join-Path $ServiceDirectory "WhisperX.Atom.Recorder.Service.exe"
$serviceCommandLine = "`"$serviceExe`" --runtime LEGACY_WASAPI"
$recorderHostExe = if ([string]::IsNullOrWhiteSpace($RecorderHostDirectory)) { $null } else { Join-Path $RecorderHostDirectory "WhisperX.Atom.Recorder.Host.exe" }

# ACL display names are localized and are not stable identifiers. Windows can
# return DESKTOP\user here even when the installer was given the corresponding
# S-1-5-* SID. Resolve every rule to a SID before comparing it. Likewise,
# FileSystemRights is a flags enum; Modify is commonly displayed as
# "Modify, Synchronize", so validate the required bits instead of parsing text.
function Test-ModifyAccessForSid {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Sid
    )

    $sidType = [System.Security.Principal.SecurityIdentifier]
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $modify = [System.Security.AccessControl.FileSystemRights]::Modify
    $acl = Get-Acl -LiteralPath $Path

    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne $allow) { continue }

        try {
            $ruleSid = $rule.IdentityReference.Translate($sidType).Value
        }
        catch {
            continue
        }

        if ($ruleSid -ne $Sid) { continue }

        $rights = [System.Security.AccessControl.FileSystemRights]$rule.FileSystemRights
        if (($rights -band $modify) -eq $modify) { return $true }
    }

    return $false
}

function Test-PinnedFfmpegPayload {
    param([Parameter(Mandatory)][string]$Directory)

    $manifestPath = Join-Path $Directory "ffmpeg-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "PINNED_FFMPEG_MANIFEST_MISSING: $manifestPath"
    }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
    catch { throw "PINNED_FFMPEG_MANIFEST_INVALID: $manifestPath" }
    if ($manifest.schemaVersion -ne 1 -or $null -eq $manifest.files) {
        throw "PINNED_FFMPEG_MANIFEST_INVALID: $manifestPath"
    }
    foreach ($entry in $manifest.files) {
        $payload = Join-Path $Directory ([string]$entry.file)
        if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) {
            throw "PINNED_FFMPEG_PAYLOAD_MISSING: $payload"
        }
        $actual = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "PINNED_FFMPEG_CHECKSUM_MISMATCH: $payload"
        }
    }
}

function Invoke-RecorderHostIpc {
    param(
        [Parameter(Mandatory)][string]$Command,
        [int]$TimeoutMilliseconds = 3000
    )

    $pipe = $null
    $reader = $null
    $writer = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'WhisperXAtomRecorderHost', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect($TimeoutMilliseconds)
        if (-not $pipe.IsConnected) { return $null }
        $reader = [System.IO.StreamReader]::new($pipe)
        $writer = [System.IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $request = [ordered]@{ command = $Command; protocolVersion = 6; payload = @{} } | ConvertTo-Json -Compress -Depth 8
        $writer.WriteLine($request)
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($TimeoutMilliseconds)) { return $null }
        $line = $task.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($line)) { return $null }
        return $line | ConvertFrom-Json
    }
    catch { return $null }
    finally {
        if ($null -ne $writer) { try { $writer.Dispose() } catch { } }
        if ($null -ne $reader) { try { $reader.Dispose() } catch { } }
        if ($null -ne $pipe) { try { $pipe.Dispose() } catch { } }
    }
}

function Prepare-RecorderHostUpdate {
    if ($null -eq $recorderHostExe) { return }

    $processes = @(Get-Process -Name 'WhisperX.Atom.Recorder.Host' -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) { return }

    $health = Invoke-RecorderHostIpc -Command 'HEALTH'
    if ($null -eq $health) {
        throw 'RECORDER_HOST_UPDATE_RESTART_REQUIRED: existing Host process is running but its IPC pipe is unresponsive.'
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$health.health.activeSessionId)) {
        throw 'RECORDER_HOST_UPDATE_BLOCKED_ACTIVE_RECORDING: stop the active recording before updating Recorder Host.'
    }

    $shutdown = Invoke-RecorderHostIpc -Command 'SHUTDOWN'
    if ($null -eq $shutdown -or $shutdown.ok -ne $true) {
        throw 'RECORDER_HOST_UPDATE_RESTART_REQUIRED: current Host did not accept a safe idle shutdown.'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $processes = @(Get-Process -Name 'WhisperX.Atom.Recorder.Host' -ErrorAction SilentlyContinue)
    } while ($processes.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($processes.Count -gt 0) {
        throw 'RECORDER_HOST_UPDATE_RESTART_REQUIRED: Host accepted shutdown but did not exit in time.'
    }
}

function Get-InstallationIdFromConfig {
    param([Parameter(Mandatory)][string[]]$Paths)

    foreach ($candidatePath in $Paths) {
        if ([string]::IsNullOrWhiteSpace($candidatePath) -or -not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
            continue
        }

        try {
            $candidate = Get-Content -LiteralPath $candidatePath -Raw | ConvertFrom-Json
            $candidateValue = if ($candidate.installationId) { $candidate.installationId } else { $candidate.InstallationId }
            $parsed = [Guid]::Empty
            if ([Guid]::TryParse([string]$candidateValue, [ref]$parsed) -and $parsed -ne [Guid]::Empty) {
                return $parsed
            }
        }
        catch {
            # A malformed optional config must not prevent installation. A new
            # identity is created only after every known config has been tried.
        }
    }

    return [Guid]::Empty
}

function Get-UserAgentConfigPathForSid {
    param([Parameter(Mandatory)][string]$Sid)

    # Install-Service is elevated, so $env:LOCALAPPDATA belongs to the
    # administrator rather than necessarily to the user who owns the Host.
    # Resolve the profile by the explicitly supplied SID instead.
    $profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$Sid"
    try {
        $profileImagePath = (Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath
        if ([string]::IsNullOrWhiteSpace([string]$profileImagePath)) { return $null }
        $profilePath = [Environment]::ExpandEnvironmentVariables([string]$profileImagePath)
        return Join-Path $profilePath "AppData\Local\WhisperXAtom\Agent\agent-config.json"
    }
    catch {
        return $null
    }
}

if (-not (Test-Path -LiteralPath $serviceExe)) { throw "Service binary not found: $serviceExe" }
if ($null -ne $recorderHostExe -and -not (Test-Path -LiteralPath $recorderHostExe -PathType Leaf)) {
    throw "RECORDER_HOST_BINARY_NOT_FOUND: $recorderHostExe"
}
if ([string]::IsNullOrWhiteSpace($AllowedUserSid) -and -not [string]::IsNullOrWhiteSpace($AllowedUserSidFile)) {
    if (-not (Test-Path -LiteralPath $AllowedUserSidFile -PathType Leaf)) { throw "Installer user SID file not found." }
    $AllowedUserSid = (Get-Content -LiteralPath $AllowedUserSidFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($AllowedUserSid)) { throw "Installer user SID is required." }
Prepare-RecorderHostUpdate
$ffmpeg = Join-Path $ServiceDirectory "ffmpeg.exe"
$ffprobe = Join-Path $ServiceDirectory "ffprobe.exe"
if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf) -or -not (Test-Path -LiteralPath $ffprobe -PathType Leaf)) {
    throw "Pinned FFmpeg payload is missing. Include ffmpeg.exe and ffprobe.exe in the Service directory and rerun the installer."
}
Test-PinnedFfmpegPayload -Directory $ServiceDirectory
[Environment]::SetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH", $ffmpeg, "Machine")
[Environment]::SetEnvironmentVariable("ATOM_AGENT_FFPROBE_PATH", $ffprobe, "Machine")
if ($null -ne $recorderHostExe) {
    Test-PinnedFfmpegPayload -Directory $RecorderHostDirectory
    [Environment]::SetEnvironmentVariable("WHISPERX_RECORDER_HOST_EXE", $recorderHostExe, "Machine")
}
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
if (-not (Test-ModifyAccessForSid -Path $agentDataRoot -Sid $AllowedUserSid)) {
    throw "AGENT_DATA_ROOT_ACL_FAILED: $agentDataRoot (user SID Modify ACE was not found)"
}
$agentDbPath = Join-Path $agentDataRoot "agent.db"
$agentDbAclReady = $true
if (Test-Path -LiteralPath $agentDbPath -PathType Leaf) {
    $agentDbAclReady = Test-ModifyAccessForSid -Path $agentDbPath -Sid $AllowedUserSid
    if (-not $agentDbAclReady) {
        throw "AGENT_DB_ACL_FAILED: $agentDbPath (user SID Modify ACE was not found)"
    }
}
Write-Host "AGENT_DATA_ROOT_ACL_READY=true"
Write-Host "AGENT_DB_ACL_READY=$($agentDbAclReady.ToString().ToLowerInvariant())"
Set-Content -LiteralPath (Join-Path $agentDataRoot "allowed-user.sid") -Value $AllowedUserSid -Encoding ascii -NoNewline
$agentConfigPath = Join-Path $agentDataRoot "agent-config.json"
$serverOrigin = if ([string]::IsNullOrWhiteSpace($env:WHISPERX_API_URL)) { "http://192.168.2.194:8080" } else { $env:WHISPERX_API_URL }
$machineConfigPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "WhisperXAtom\client-config.json"
$userAgentConfigPath = Get-UserAgentConfigPathForSid -Sid $AllowedUserSid
# A user-scoped Host configuration owns the runtime lease. Preserve its
# identity before consulting machine or legacy service configuration, otherwise
# an elevation can split a single installation into two competing leases.
$installationId = Get-InstallationIdFromConfig -Paths @($userAgentConfigPath, $machineConfigPath, $agentConfigPath)
if ($installationId -eq [Guid]::Empty) { $installationId = [Guid]::NewGuid() }
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
$existingMachine = if (Test-Path -LiteralPath $machineConfigPath -PathType Leaf) {
    try { Get-Content -LiteralPath $machineConfigPath -Raw | ConvertFrom-Json } catch { $null }
} else { $null }
$existingOrigin = if ($existingMachine -and $existingMachine.serverOrigin) { [string]$existingMachine.serverOrigin } else { $null }
if ($existingOrigin) {
    try {
        $existingUri = [Uri]$existingOrigin
        if ($existingUri.Scheme -in @("http", "https")) { $serverOrigin = $existingUri.ToString() }
    } catch { }
}
$existingAudio = if ($existingMachine) { $existingMachine.audioConfiguration } else { $null }
$microphone = if ($existingAudio -and $existingAudio.microphone) { $existingAudio.microphone } else { $null }
$machineConfig = [ordered]@{
    schemaVersion = 2
    serverOrigin = $serverOrigin.TrimEnd('/')
    managed = if ($existingMachine -and $null -ne $existingMachine.managed) { [bool]$existingMachine.managed } else { $true }
    installationId = $installationId
    audioConfiguration = [ordered]@{
        audioConfigurationVersion = 2
        captureEngine = "AUDIOGRAPH"
        microphone = [ordered]@{
            selectionMode = if ($microphone -and $microphone.selectionMode -in @("DEFAULT", "FIXED")) { [string]$microphone.selectionMode } else { "DEFAULT" }
            deviceId = if ($microphone) { $microphone.deviceId } else { $null }
        }
        systemAudio = [ordered]@{ selectionMode = "DEFAULT"; deviceId = $null }
        userReselectRequired = if ($existingAudio -and $null -ne $existingAudio.userReselectRequired) { [bool]$existingAudio.userReselectRequired } else { $false }
    }
} | ConvertTo-Json -Depth 8
$machineConfigPart = "$machineConfigPath.part"
Set-Content -LiteralPath $machineConfigPart -Value $machineConfig -Encoding utf8
Move-Item -LiteralPath $machineConfigPart -Destination $machineConfigPath -Force
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.Status -ne "Stopped") { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}
New-Service -Name $serviceName -DisplayName $displayName -Description "Legacy fallback for WhisperX Atom recording" -BinaryPathName $serviceCommandLine -StartupType Manual | Out-Null
sc.exe config $serviceName start= demand | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
Write-Host "Installed $displayName in Manual/Stopped fallback mode"
Write-Host "RECORDER_RUNTIME_DEFAULT=AUDIOGRAPH"

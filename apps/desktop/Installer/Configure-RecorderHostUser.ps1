param(
    [Parameter(Mandatory)]
    [string]$RecorderHostPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $RecorderHostPath -PathType Leaf)) {
    throw "RECORDER_HOST_BINARY_NOT_FOUND: $RecorderHostPath"
}

$directory = Join-Path (Join-Path $env:LOCALAPPDATA "WhisperXAtom") "Agent"
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$path = Join-Path $directory "agent-config.json"
$existing = if (Test-Path -LiteralPath $path -PathType Leaf) {
    try { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $null }
} else { $null }

# The current-user Host owns the DPAPI token and the canonical Agent identity.
# Only fall back to the machine installation id when no valid user identity
# exists; otherwise an elevated installer can silently split one installation
# into two runtime leases.
$machineConfigPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "WhisperXAtom\client-config.json"
$machineConfig = if (Test-Path -LiteralPath $machineConfigPath -PathType Leaf) {
    try { Get-Content -LiteralPath $machineConfigPath -Raw | ConvertFrom-Json } catch { $null }
} else { $null }
$userInstallationId = if ($existing -and $existing.InstallationId) { [string]$existing.InstallationId } elseif ($existing -and $existing.installationId) { [string]$existing.installationId } else { $null }
$machineInstallationId = if ($machineConfig -and $machineConfig.InstallationId) { [string]$machineConfig.InstallationId } elseif ($machineConfig -and $machineConfig.installationId) { [string]$machineConfig.installationId } else { $null }
$validUserInstallationId = [Guid]::Empty
$validMachineInstallationId = [Guid]::Empty
$userIsValid = [Guid]::TryParse($userInstallationId, [ref]$validUserInstallationId) -and $validUserInstallationId -ne [Guid]::Empty
$machineIsValid = [Guid]::TryParse($machineInstallationId, [ref]$validMachineInstallationId) -and $validMachineInstallationId -ne [Guid]::Empty
$installationId = if ($userIsValid) { $validUserInstallationId.ToString() } elseif ($machineIsValid) { $validMachineInstallationId.ToString() } else { [Guid]::NewGuid().ToString() }

$existingAudio = if ($existing) { $existing.AudioConfiguration } else { $null }
$existingDevice = if ($existingAudio -and $existingAudio.microphone) { [string]$existingAudio.microphone.deviceId } elseif ($existing) { [string]$existing.MicrophoneDeviceId } else { $null }
# Legacy NAudio endpoint IDs ({0.0...}) are not DeviceInformation.Id values.
# Never copy them into the AudioGraph Host config; the Doctor will resolve
# Windows DEFAULT and require confirmation before persisting a FIXED device.
$isAudioGraphDeviceId = -not [string]::IsNullOrWhiteSpace($existingDevice) -and $existingDevice.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase)
$microphoneDeviceId = if ($isAudioGraphDeviceId) { $existingDevice } else { $null }
$requiresReselect = -not [string]::IsNullOrWhiteSpace($existingDevice) -and -not $isAudioGraphDeviceId

# The Host configuration is per user. Keep the encrypted token exactly as it
# is; this installer never writes a plaintext token or changes DPAPI scope.
$configuration = [ordered]@{
    ServerUrl = if ($existing -and $existing.ServerUrl) { [string]$existing.ServerUrl } else { "" }
    AgentId = if ($existing -and $existing.AgentId) { [string]$existing.AgentId } else { "" }
    Token = if ($existing -and $existing.Token) { [string]$existing.Token } else { "" }
    Encrypted = if ($existing -and $null -ne $existing.Encrypted) { [bool]$existing.Encrypted } else { $true }
    InstallationId = $installationId
    ArchiveRoot = if ($existing -and $existing.ArchiveRoot) { [string]$existing.ArchiveRoot } else { $null }
    MicrophoneDeviceId = $microphoneDeviceId
    SystemAudioDeviceId = $null
    RecordingProfile = if ($existing -and $existing.RecordingProfile -in @("ROOM", "MIC_ONLY")) { [string]$existing.RecordingProfile } else { "ROOM" }
    AudioConfiguration = [ordered]@{
        audioConfigurationVersion = 2
        captureEngine = "AUDIOGRAPH"
        microphone = [ordered]@{ selectionMode = if ($microphoneDeviceId) { "FIXED" } else { "DEFAULT" }; deviceId = $microphoneDeviceId }
        systemAudio = [ordered]@{ selectionMode = "DEFAULT"; deviceId = $null }
        userReselectRequired = $requiresReselect
    }
} | ConvertTo-Json -Depth 8

$temporary = "$path.part"
Set-Content -LiteralPath $temporary -Value $configuration -Encoding utf8
Move-Item -LiteralPath $temporary -Destination $path -Force
[Environment]::SetEnvironmentVariable("WHISPERX_RECORDER_HOST_EXE", $RecorderHostPath, "User")
Write-Host "RECORDER_HOST_USER_CONFIG_READY=true"

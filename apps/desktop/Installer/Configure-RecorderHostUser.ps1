param(
    [Parameter(Mandatory)]
    [string]$RecorderHostPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $RecorderHostPath -PathType Leaf)) {
    throw "RECORDER_HOST_BINARY_NOT_FOUND: $RecorderHostPath"
}

$machineConfigPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "WhisperXAtom\client-config.json"
$machineConfig = if (Test-Path -LiteralPath $machineConfigPath -PathType Leaf) {
    try { Get-Content -LiteralPath $machineConfigPath -Raw | ConvertFrom-Json } catch { $null }
} else { $null }
$installationId = if ($null -ne $machineConfig -and $machineConfig.installationId) { [string]$machineConfig.installationId } else { [Guid]::NewGuid().ToString() }

$directory = Join-Path (Join-Path $env:LOCALAPPDATA "WhisperXAtom") "Agent"
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$path = Join-Path $directory "agent-config.json"
$existing = if (Test-Path -LiteralPath $path -PathType Leaf) {
    try { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $null }
} else { $null }

# The Host configuration is per user. Keep the encrypted token exactly as it
# is; this installer never writes a plaintext token or changes DPAPI scope.
$configuration = [ordered]@{
    ServerUrl = if ($existing -and $existing.ServerUrl) { [string]$existing.ServerUrl } else { "" }
    AgentId = if ($existing -and $existing.AgentId) { [string]$existing.AgentId } else { "" }
    Token = if ($existing -and $existing.Token) { [string]$existing.Token } else { "" }
    Encrypted = if ($existing -and $null -ne $existing.Encrypted) { [bool]$existing.Encrypted } else { $true }
    InstallationId = $installationId
    ArchiveRoot = if ($existing -and $existing.ArchiveRoot) { [string]$existing.ArchiveRoot } else { $null }
    MicrophoneDeviceId = if ($existing -and $existing.MicrophoneDeviceId) { [string]$existing.MicrophoneDeviceId } else { $null }
    SystemAudioDeviceId = $null
    RecordingProfile = if ($existing -and $existing.RecordingProfile -in @("ROOM", "MIC_ONLY")) { [string]$existing.RecordingProfile } else { "ROOM" }
    AudioConfiguration = [ordered]@{
        audioConfigurationVersion = 2
        captureEngine = "AUDIOGRAPH"
        microphone = [ordered]@{ selectionMode = if ($existing -and $existing.MicrophoneDeviceId) { "FIXED" } else { "DEFAULT" }; deviceId = if ($existing) { $existing.MicrophoneDeviceId } else { $null } }
        systemAudio = [ordered]@{ selectionMode = "DEFAULT"; deviceId = $null }
        userReselectRequired = $false
    }
} | ConvertTo-Json -Depth 8

$temporary = "$path.part"
Set-Content -LiteralPath $temporary -Value $configuration -Encoding utf8
Move-Item -LiteralPath $temporary -Destination $path -Force
[Environment]::SetEnvironmentVariable("WHISPERX_RECORDER_HOST_EXE", $RecorderHostPath, "User")
Write-Host "RECORDER_HOST_USER_CONFIG_READY=true"

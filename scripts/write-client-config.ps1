[CmdletBinding()]
param(
    [string]$ServerOrigin = "http://127.0.0.1:0",
    [switch]$Managed,
    [ValidateSet("AUDIOGRAPH", "LEGACY_WASAPI")]
    [string]$CaptureEngine = ""
)

$ErrorActionPreference = "Stop"
$uri = $null
if (-not [Uri]::TryCreate($ServerOrigin.TrimEnd('/'), [UriKind]::Absolute, [ref]$uri)) { throw "SERVER_ORIGIN_INVALID" }
if ($uri.Scheme -notin @("http", "https")) { throw "SERVER_ORIGIN_SCHEME_INVALID" }
$target = "C:\ProgramData\WhisperXAtom\client-config.json"
$directory = Split-Path -Parent $target
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$existing = if (Test-Path -LiteralPath $target -PathType Leaf) {
    try { Get-Content -LiteralPath $target -Raw | ConvertFrom-Json } catch { $null }
} else { $null }
$parsedInstallation = [Guid]::Empty
$installationId = if ($existing -and $existing.installationId -and [Guid]::TryParse([string]$existing.installationId, [ref]$parsedInstallation) -and $parsedInstallation -ne [Guid]::Empty) {
    [string]$existing.installationId
} else {
    [Guid]::NewGuid().ToString()
}
$audio = if ($existing -and $existing.audioConfiguration) { $existing.audioConfiguration } else { $null }
$engine = if (-not [string]::IsNullOrWhiteSpace($CaptureEngine)) { $CaptureEngine } elseif ($audio -and $audio.captureEngine -in @("AUDIOGRAPH", "LEGACY_WASAPI")) { [string]$audio.captureEngine } else { "AUDIOGRAPH" }
$microphone = if ($audio -and $audio.microphone) { $audio.microphone } else { $null }
$systemAudio = if ($audio -and $audio.systemAudio) { $audio.systemAudio } else { $null }
$configuration = [ordered]@{
    schemaVersion = 2
    serverOrigin = $uri.ToString().TrimEnd('/')
    managed = if ($Managed.IsPresent) { $true } elseif ($existing -and $null -ne $existing.managed) { [bool]$existing.managed } else { $false }
    installationId = $installationId
    audioConfiguration = [ordered]@{
        audioConfigurationVersion = 2
        captureEngine = $engine
        microphone = [ordered]@{
            selectionMode = if ($microphone -and $microphone.selectionMode -in @("DEFAULT", "FIXED")) { [string]$microphone.selectionMode } else { "DEFAULT" }
            deviceId = if ($microphone) { $microphone.deviceId } else { $null }
        }
        systemAudio = [ordered]@{
            selectionMode = if ($systemAudio -and $systemAudio.selectionMode -in @("DEFAULT", "FIXED")) { [string]$systemAudio.selectionMode } else { "DEFAULT" }
            deviceId = if ($systemAudio) { $systemAudio.deviceId } else { $null }
        }
        userReselectRequired = if ($audio -and $null -ne $audio.userReselectRequired) { [bool]$audio.userReselectRequired } else { $false }
    }
}
$json = $configuration | ConvertTo-Json -Depth 8
$part = "$target.part"
Set-Content -LiteralPath $part -Value $json -Encoding utf8
Move-Item -LiteralPath $part -Destination $target -Force
Write-Host "Machine runtime config v2 written: $($uri.ToString().TrimEnd('/')) ($engine)" -ForegroundColor Green

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AudioCaptureAbReport,
    [Parameter(Mandatory)][string]$SharedNativeReport,
    [Parameter(Mandatory)][string]$AudacityBaseline,
    [Parameter(Mandatory)][string]$BuildIdentity,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$OperatorConfirmed
)

$ErrorActionPreference = 'Stop'
function Read-Json([string]$path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
function Hash-File([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

$ab = Read-Json $AudioCaptureAbReport
$native = Read-Json $SharedNativeReport
$baseline = Read-Json $AudacityBaseline
$abCapture = $ab.response.audioCaptureAb
$nativeCapture = $native.capture
$abOk = $ab.status -eq 'PASSED' -and $null -ne $abCapture
$nativeOk = $native.status -in @('PASSED','READY_FOR_REVIEW') -and $null -ne $nativeCapture
$safe = [ordered]@{
    audioIncluded = $false
    transcriptIncluded = $false
    credentialsIncluded = $false
    tokensIncluded = $false
    pathsIncluded = $false
}
$report = [ordered]@{
    schemaVersion = 2
    scenario = 'audio-capture-parity'
    captureMode = 'LIVE'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    buildIdentity = $BuildIdentity
    status = if ($OperatorConfirmed -and $abOk -and $nativeOk) { 'PASSED' } else { 'READY_FOR_REVIEW' }
    parityComplete = [bool]($OperatorConfirmed -and $abOk -and $nativeOk)
    inputs = @('AUDACITY_BASELINE','AUDIOGRAPH','WASAPI_SHARED_NATIVE','WASAPI_RAW')
    baseline = [ordered]@{
        schemaVersion = $baseline.schemaVersion
        endpoint = [string]$baseline.endpoint
        format = $baseline.format
        deviceIdSha256 = [string]$baseline.deviceIdSha256
        sourceSha256 = Hash-File $AudacityBaseline
    }
    measurements = [ordered]@{
        audioGraph = [ordered]@{ sha256 = [string]$abCapture.audioGraphSha256; quality = $abCapture.audioGraphQuality }
        wasapiRaw = [ordered]@{ sha256 = [string]$abCapture.rawSha256; quality = $abCapture.rawQuality }
        wasapiSharedNative = [ordered]@{
            sha256 = [string]$nativeCapture.nativeSha256
            variant = [string]$nativeCapture.variant
            sampleRate = $nativeCapture.nativeSampleRate
            channels = $nativeCapture.nativeChannels
            sampleFormat = [string]$nativeCapture.nativeSampleFormat
        }
    }
    safety = $safe
}
$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output $OutputPath
if (-not $report.parityComplete) { exit 2 }

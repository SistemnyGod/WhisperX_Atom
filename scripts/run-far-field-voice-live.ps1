[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$VoiceHostPath,
    [Parameter(Mandatory = $true)][string]$VoiceManifestPath,
    [Parameter(Mandatory = $true)][string]$BuildIdentity,
    [string]$OutputPath,
    [string]$MicrophoneId,
    [ValidateRange(1, 500)][int]$RepetitionsPerCell = 20,
    [switch]$OperatorConfirmed
)

$ErrorActionPreference = "Stop"
if (-not $OperatorConfirmed) { throw "FAR_FIELD_OPERATOR_CONFIRMATION_REQUIRED" }
if (-not (Test-Path -LiteralPath $VoiceHostPath -PathType Leaf)) { throw "VOICE_HOST_MISSING" }
if (-not (Test-Path -LiteralPath $VoiceManifestPath -PathType Leaf)) { throw "VOICE_REFINER_MANIFEST_MISSING" }
$manifest = Get-Content -LiteralPath $VoiceManifestPath -Raw | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 2) { throw "VOICE_REFINER_MANIFEST_UPGRADE_REQUIRED" }
if ([string]$manifest.buildIdentity -ne $BuildIdentity) { throw "VOICE_REFINER_IDENTITY_MISMATCH" }
$voiceHostSha = (Get-FileHash -LiteralPath $VoiceHostPath -Algorithm SHA256).Hash.ToLowerInvariant()

$distances = @(0.5, 1, 2, 3)
$conditions = @("quiet", "office", "ventilation", "conversation", "tts_playback")
$records = [System.Collections.Generic.List[object]]::new()
$overallStatus = "PASSED"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-far-field-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    foreach ($distance in $distances) {
        foreach ($condition in $conditions) {
            Write-Host "Подготовьте микрофон на расстоянии ${distance} м, условие '$condition'. Нажмите Enter после проверки."
            [void](Read-Host)
            $stdoutPath = Join-Path $tempRoot ("cell-" + $distance.ToString().Replace('.', '_') + "-" + $condition + ".jsonl")
            $args = @("--mic-acceptance", $RepetitionsPerCell)
            if (-not [string]::IsNullOrWhiteSpace($MicrophoneId)) { $args += @("--microphone-id", $MicrophoneId) }
            $process = Start-Process -FilePath $VoiceHostPath -ArgumentList $args -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdoutPath
            $jsonLine = @(Get-Content -LiteralPath $stdoutPath -ErrorAction SilentlyContinue |
                Where-Object { $_ -match '^\s*\{' } |
                ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } |
                Where-Object { $_.mode -eq "mic-acceptance-dry-run" } |
                Select-Object -Last 1)
            $result = if ($jsonLine.Count -gt 0) { $jsonLine[0] } else { $null }
            $completed = $null -ne $result -and $result.completed -eq $true -and [int]$result.audioQueueDrops -eq 0 -and [int]$result.falseActivations -eq 0
            if (-not $completed -or $process.ExitCode -ne 0) { $overallStatus = "FAILED" }
            $records.Add([ordered]@{
                caseId = "far-field-$($distance.ToString().Replace('.', '_'))-$condition"
                distanceMeters = [double]$distance
                condition = $condition
                repetitions = $RepetitionsPerCell
                detections = if ($result) { [int]$result.detections } else { 0 }
                targetDetections = if ($result) { [int]$result.targetDetections } else { $RepetitionsPerCell }
                audioQueueDrops = if ($result) { [int]$result.audioQueueDrops } else { -1 }
                falseActivations = if ($result) { [int]$result.falseActivations } else { -1 }
                completed = [bool]$completed
                processExitCode = $process.ExitCode
            })
            Remove-Item -LiteralPath $stdoutPath -Force -ErrorAction SilentlyContinue
        }
    }
} finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    $OutputPath = Join-Path $repo "artifacts\acceptance\far-field-voice\live.json"
}
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$report = [ordered]@{
    schema = "far-field-voice-acceptance-v2"
    mode = "LIVE"
    status = $overallStatus
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    buildIdentity = $BuildIdentity
    voiceHostSha256 = $voiceHostSha
    operatorConfirmed = $true
    refiner = [ordered]@{
        modelRevision = [string]$manifest.model.revision
        modelSha256 = [string]$manifest.model.sha256
        whisperCppRevision = [string]$manifest.native.whisperCppRevision
        bridgeRevision = [string]$manifest.native.bridgeRevision
        nativeSha256 = [string]$manifest.native.sha256
        abiVersion = [int]$manifest.native.abiVersion
    }
    distancesMeters = $distances
    conditions = $conditions
    repetitionsPerCell = $RepetitionsPerCell
    cellCount = $records.Count
    records = @($records)
    privacy = [ordered]@{ audioStored = $false; transcriptStored = $false; rawPcmStored = $false; sourcePathsStored = $false }
    safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; textIncluded = $false }
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output ([ordered]@{ schema = $report.schema; mode = $report.mode; status = $report.status; cellCount = $report.cellCount; repetitionsPerCell = $report.repetitionsPerCell } | ConvertTo-Json -Compress)
if ($overallStatus -ne "PASSED") { exit 1 }

[CmdletBinding()]
param(
    [string]$ResultRoot,
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo

$transcriptionRoot = Join-Path $repo "artifacts\transcription-mvp"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repo "artifacts\release" }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($ResultRoot)) {
    $ResultRoot = @(Get-ChildItem -LiteralPath $transcriptionRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 |
        ForEach-Object { $_.FullName })
}

$doctorPath = Join-Path $transcriptionRoot "doctor.json"
$doctor = if (Test-Path -LiteralPath $doctorPath) {
    Get-Content -LiteralPath $doctorPath -Raw | ConvertFrom-Json
} else { $null }
$runtimeStatePath = Join-Path $repo "artifacts\runtime\state.json"
$runtimeState = if (Test-Path -LiteralPath $runtimeStatePath) {
    Get-Content -LiteralPath $runtimeStatePath -Raw | ConvertFrom-Json
} else { $null }
$runtimeReport = if ($runtimeState) { $runtimeState } elseif ($doctor -and ($doctor.PSObject.Properties.Name -contains "postgres")) { $doctor } else { $null }

function Get-JsonProperty($object, [string]$name) {
    if ($null -eq $object -or -not ($object.PSObject.Properties.Name -contains $name)) { return $null }
    return $object.PSObject.Properties[$name].Value
}

$resultFiles = if ($ResultRoot -and (Test-Path -LiteralPath $ResultRoot)) {
    @(Get-ChildItem -LiteralPath $ResultRoot -Recurse -File -Filter "run-*.json" | Sort-Object FullName)
} else { @() }
$results = @($resultFiles | ForEach-Object {
    try { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json }
    catch { $null }
} | Where-Object { $null -ne $_ })

$coreComponents = @("postgres", "nats", "api", "mediaWorker", "hostGpuWorker", "cuda", "whisperX")
$runtimeReady = $true
$runtimeReasons = [System.Collections.Generic.List[string]]::new()
if ($null -eq $runtimeReport) {
    $runtimeReady = $false
    $runtimeReasons.Add("DOCTOR_REPORT_MISSING")
} else {
    foreach ($component in $coreComponents) {
        $componentStatus = [string](Get-JsonProperty $runtimeReport $component)
        if ($componentStatus -ne "READY") {
            $runtimeReady = $false
            $runtimeReasons.Add("RUNTIME_$($component.ToUpperInvariant())_$componentStatus")
        }
    }
}

$chains = @($results | ForEach-Object {
    $transcriptStatus = [string](Get-JsonProperty $_ "transcriptStatus")
    $transcriptSegmentCount = [int](Get-JsonProperty $_ "transcriptSegmentCount")
    $summaryStatus = [string](Get-JsonProperty $_ "summaryStatus")
    $summaryId = [string](Get-JsonProperty $_ "summaryId")
    $transcriptReady = ($transcriptStatus -in @("READY", "PARTIAL_READY")) -and ($transcriptSegmentCount -gt 0)
    [pscustomobject]@{
        runId = [string](Get-JsonProperty $_ "runId")
        meetingId = [string](Get-JsonProperty $_ "meetingId")
        mediaAssetIds = @((Get-JsonProperty $_ "mediaAssetIds") | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        jobIds = @((Get-JsonProperty $_ "jobIds") | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        transcriptId = [string](Get-JsonProperty $_ "transcriptId")
        traceId = [string](Get-JsonProperty $_ "traceId")
        transcriptReady = $transcriptReady
        summaryReady = ($summaryStatus -eq "READY") -and (-not [string]::IsNullOrWhiteSpace($summaryId))
        localArchiveReady = [bool](Get-JsonProperty $_ "localArchiveReady")
        deliveryConfirmed = [bool](Get-JsonProperty $_ "deliveryConfirmed")
        mediaReady = [bool](Get-JsonProperty $_ "mediaReady")
    }
})

$recordingReady = @($chains | Where-Object { $_.localArchiveReady }).Count -gt 0
$deliveryReady = @($chains | Where-Object { $_.deliveryConfirmed }).Count -gt 0
$mediaReady = @($chains | Where-Object { $_.mediaReady }).Count -gt 0
$transcriptReady = @($chains | Where-Object { $_.transcriptReady }).Count -gt 0
$summaryReady = @($chains | Where-Object { $_.summaryReady }).Count -gt 0

$reasons = [System.Collections.Generic.List[string]]::new()
foreach ($reason in $runtimeReasons) { $reasons.Add($reason) }
if (-not $recordingReady) { $reasons.Add("LOCAL_ARCHIVE_EVIDENCE_MISSING") }
if (-not $deliveryReady) { $reasons.Add("DELIVERY_CONFIRMATION_MISSING") }
if (-not $mediaReady) { $reasons.Add("MEDIA_READY_EVIDENCE_MISSING") }
if (-not $transcriptReady) { $reasons.Add("TRANSCRIPT_NOT_READY") }
if (-not $summaryReady) { $reasons.Add("SUMMARY_NOT_READY") }

$uniqueMeetingIds = @($chains | ForEach-Object { $_.meetingId } | Where-Object { $_ } | Select-Object -Unique)
$allJobIds = @($chains | ForEach-Object { $_.jobIds } | Where-Object { $_ })
$uniqueJobIds = @($allJobIds | Select-Object -Unique)
$uniqueTranscriptIds = @($chains | ForEach-Object { $_.transcriptId } | Where-Object { $_ } | Select-Object -Unique)
$missingCorrelation = @($chains | Where-Object {
    [string]::IsNullOrWhiteSpace($_.runId) -or
    [string]::IsNullOrWhiteSpace($_.meetingId) -or
    $_.mediaAssetIds.Count -eq 0 -or
    $_.jobIds.Count -eq 0 -or
    [string]::IsNullOrWhiteSpace($_.transcriptId) -or
    [string]::IsNullOrWhiteSpace($_.traceId)
}).Count -gt 0
$duplicateJobIds = $uniqueJobIds.Count -ne $allJobIds.Count
$duplicateChains = $duplicateJobIds -or $uniqueMeetingIds.Count -ne $chains.Count -or $uniqueTranscriptIds.Count -ne $chains.Count
$uniqueChains = $chains.Count -eq 0 -or (-not $missingCorrelation -and -not $duplicateChains)
if ($missingCorrelation) { $reasons.Add("PIPELINE_CORRELATION_EVIDENCE_MISSING") }
if ($duplicateChains) { $reasons.Add("DUPLICATE_PIPELINE_IDENTIFIERS") }

$audit = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    resultRoot = if ($ResultRoot) { [string]$ResultRoot } else { $null }
    doctorReport = if ($doctorPath -and (Test-Path -LiteralPath $doctorPath)) { $doctorPath } else { $null }
    runtime = [ordered]@{ ready = $runtimeReady; reasons = @($runtimeReasons) }
    evidence = [ordered]@{
        recording = $recordingReady
        delivery = $deliveryReady
        media = $mediaReady
        transcript = $transcriptReady
        summary = $summaryReady
        uniqueChains = $uniqueChains
        chainCount = $chains.Count
    }
    chains = @($chains)
    blockers = @($reasons)
}
$auditPath = Join-Path $OutputRoot "mvp-release-audit.json"
$audit | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 -LiteralPath $auditPath

$gateStatus = if ($reasons.Count -eq 0) { "READY" } else { "BLOCKED_BY_CORE_PIPELINE" }
$gate = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    status = $gateStatus
    mvpV1Ready = $gateStatus -eq "READY"
    auditPath = $auditPath
    blockers = @($reasons)
    releaseHardeningAllowed = $gateStatus -eq "READY"
    note = if ($gateStatus -eq "READY") { "Core pipeline evidence is complete." } else { "Release hardening remains blocked until the complete live core pipeline is proven." }
}
$gatePath = Join-Path $OutputRoot "mvp-release-gate.json"
$gate | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 -LiteralPath $gatePath
$gate | ConvertTo-Json -Depth 12
if ($gateStatus -ne "READY") { exit 2 }

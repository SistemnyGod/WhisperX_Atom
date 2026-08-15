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
$coreEvidencePath = Join-Path $repo "artifacts\acceptance\core-e2e-v1.json"
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

$coreEvidence = if (Test-Path -LiteralPath $coreEvidencePath -PathType Leaf) {
    try { Get-Content -LiteralPath $coreEvidencePath -Raw | ConvertFrom-Json } catch { $null }
} else { $null }

# A core gate is one correlated chain. Do not merge unrelated historical
# transcription-mvp runs: that can make a stale ASR result appear to belong to
# the current local recording.
$results = if ($null -ne $coreEvidence) { @($coreEvidence) } else { @() }

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
        runId = [string](Get-JsonProperty $_ "pipelineCorrelationId")
        localSessionId = [string](Get-JsonProperty $_ "localSessionId")
        serverSessionId = [string](Get-JsonProperty $_ "serverSessionId")
        meetingId = [string](Get-JsonProperty $_ "meetingId")
        mediaAssetIds = @((Get-JsonProperty $_ "mediaAssetId") | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        jobIds = @((Get-JsonProperty $_ "asrJobId") | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        transcriptId = [string](Get-JsonProperty $_ "transcriptV1Id")
        traceId = [string](Get-JsonProperty $_ "traceId")
        transcriptReady = $transcriptReady
        summaryReady = ($summaryStatus -eq "READY") -and (-not [string]::IsNullOrWhiteSpace($summaryId))
        localArchiveReady = [bool](Get-JsonProperty $_ "flacReady")
        deliveryConfirmed = [bool](Get-JsonProperty $_ "deliveryConfirmed")
        mediaReady = [bool](Get-JsonProperty $_ "mediaReady")
        pipelineCorrelationId = [string](Get-JsonProperty $_ "pipelineCorrelationId")
    }
})

$recordingReady = @($chains | Where-Object { $_.localArchiveReady }).Count -gt 0
$deliveryReady = @($chains | Where-Object { $_.deliveryConfirmed }).Count -gt 0
$mediaReady = @($chains | Where-Object { $_.mediaReady }).Count -gt 0
$transcriptReady = @($chains | Where-Object { $_.transcriptReady }).Count -gt 0
$summaryReady = @($chains | Where-Object { $_.summaryReady }).Count -gt 0

$requiredAcceptanceScenarios = @(
    "e2e-5m",
    "server-offline-recovery",
    "recorder-crash-recovery",
    "worker-crash-recovery",
    "windows-reboot-recovery",
    "endurance-30m",
    "endurance-2h",
    "backup-restore",
    "rbac-isolation"
)
$acceptanceRoot = Join-Path $repo "artifacts\acceptance"
$acceptanceBlockers = [System.Collections.Generic.List[string]]::new()
foreach ($scenario in $requiredAcceptanceScenarios) {
    $scenarioRoot = Join-Path $acceptanceRoot $scenario
    $evidence = @(Get-ChildItem -LiteralPath $scenarioRoot -Recurse -File -Filter "*.json" -ErrorAction SilentlyContinue)
    $scenarioReady = $false
    foreach ($file in $evidence) {
        try {
            $json = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($scenario -eq "backup-restore") {
                $scenarioReady = ($json.backupVerified -eq $true -and $json.cleanRestore -eq $true)
            } else {
                $scenarioReady = ($json.status -in @("READY", "PASSED", "GREEN") -or $json.result -in @("READY", "PASSED", "GREEN") -or $json.passed -eq $true)
            }
            if ($scenarioReady) { break }
        } catch { }
    }
    if (-not $scenarioReady) { $acceptanceBlockers.Add("ACCEPTANCE_$($scenario.ToUpperInvariant().Replace('-', '_'))_MISSING") }
}
$acceptanceReady = $acceptanceBlockers.Count -eq 0

# Core release gate deliberately stops at the first useful text. Summary/Qwen,
# long endurance and recovery hardening remain part of the full gate below.
$coreAcceptanceScenarios = @("e2e-5m")
$coreAcceptanceBlockers = [System.Collections.Generic.List[string]]::new()
$coreAcceptanceBlockers.Add("CORE_E2E_V1_MISSING")
if ($null -ne $coreEvidence) {
    $coreAcceptanceBlockers.Remove("CORE_E2E_V1_MISSING")
    if ([string](Get-JsonProperty $coreEvidence "status") -notin @("READY", "PASSED", "GREEN")) { $coreAcceptanceBlockers.Add("CORE_E2E_V1_NOT_READY") }
    foreach ($field in @("localSessionId","serverSessionId","meetingId","mediaAssetId","asrJobId","transcriptV1Id","traceId","pipelineCorrelationId")) {
        if ([string]::IsNullOrWhiteSpace([string](Get-JsonProperty $coreEvidence $field))) { $coreAcceptanceBlockers.Add("CORE_E2E_V1_$($field.ToUpperInvariant())_MISSING") }
    }
    if ([string](Get-JsonProperty $coreEvidence "transcriptStatus") -notin @("PARTIAL_READY","READY")) { $coreAcceptanceBlockers.Add("CORE_E2E_V1_TRANSCRIPT_NOT_READY") }
}
foreach ($scenario in $coreAcceptanceScenarios) {
    $scenarioRoot = Join-Path $acceptanceRoot $scenario
    $evidence = @(Get-ChildItem -LiteralPath $scenarioRoot -Recurse -File -Filter "*.json" -ErrorAction SilentlyContinue)
    $scenarioReady = $false
    foreach ($file in $evidence) {
        try {
            $json = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $scenarioReady = ($json.status -in @("READY", "PASSED", "GREEN") -or $json.result -in @("READY", "PASSED", "GREEN") -or $json.passed -eq $true)
            if ($scenarioReady) { break }
        } catch { }
    }
    if (-not $scenarioReady) { $coreAcceptanceBlockers.Add("CORE_ACCEPTANCE_$($scenario.ToUpperInvariant().Replace('-', '_'))_MISSING") }
}

$reasons = [System.Collections.Generic.List[string]]::new()
foreach ($reason in $runtimeReasons) { $reasons.Add($reason) }
foreach ($reason in $acceptanceBlockers) { $reasons.Add($reason) }
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

$coreReasons = [System.Collections.Generic.List[string]]::new()
foreach ($reason in $runtimeReasons) { $coreReasons.Add($reason) }
foreach ($reason in $coreAcceptanceBlockers) { $coreReasons.Add($reason) }
if (-not $recordingReady) { $coreReasons.Add("LOCAL_ARCHIVE_EVIDENCE_MISSING") }
if (-not $deliveryReady) { $coreReasons.Add("DELIVERY_CONFIRMATION_MISSING") }
if (-not $mediaReady) { $coreReasons.Add("MEDIA_READY_EVIDENCE_MISSING") }
if (-not $transcriptReady) { $coreReasons.Add("TRANSCRIPT_NOT_READY") }
if (-not $uniqueChains) { $coreReasons.Add("PIPELINE_CORRELATION_INVALID") }
$coreStatus = if ($coreReasons.Count -eq 0) { "END_TO_END_CORE_READY" } else { "BLOCKED_BY_CORE_PIPELINE" }

$audit = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    resultRoot = if ($ResultRoot) { [string]$ResultRoot } else { $null }
    coreEvidence = if (Test-Path -LiteralPath $coreEvidencePath -PathType Leaf) { $coreEvidencePath } else { $null }
    doctorReport = if ($doctorPath -and (Test-Path -LiteralPath $doctorPath)) { $doctorPath } else { $null }
    runtime = [ordered]@{ ready = $runtimeReady; reasons = @($runtimeReasons) }
    evidence = [ordered]@{
        recording = $recordingReady
        delivery = $deliveryReady
        media = $mediaReady
        transcript = $transcriptReady
        summary = $summaryReady
        acceptance = $acceptanceReady
        coreAcceptanceScenarios = $coreAcceptanceScenarios
        coreAcceptance = $coreAcceptanceBlockers.Count -eq 0
        requiredAcceptanceScenarios = $requiredAcceptanceScenarios
        uniqueChains = $uniqueChains
        chainCount = $chains.Count
    }
    chains = @($chains)
    coreStatus = $coreStatus
    coreBlockers = @($coreReasons)
    blockers = @($reasons)
}
$auditPath = Join-Path $OutputRoot "mvp-release-audit.json"
$audit | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 -LiteralPath $auditPath

$gateStatus = if ($reasons.Count -eq 0) { "READY" } else { "BLOCKED_BY_CORE_PIPELINE" }
$gate = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    status = $gateStatus
    coreStatus = $coreStatus
    coreReady = $coreStatus -eq "END_TO_END_CORE_READY"
    coreBlockers = @($coreReasons)
    mvpV1Ready = $gateStatus -eq "READY"
    auditPath = $auditPath
    blockers = @($reasons)
    releaseHardeningAllowed = $gateStatus -eq "READY"
    releaseHardeningStatus = if ($gateStatus -eq "READY") { "FULL_RELEASE_READY" } elseif ($coreStatus -eq "END_TO_END_CORE_READY") { "CORE_READY_HARDENING_PENDING" } else { "BLOCKED_BY_CORE_PIPELINE" }
    note = if ($gateStatus -eq "READY") { "Core pipeline and full release hardening evidence are complete." } elseif ($coreStatus -eq "END_TO_END_CORE_READY") { "Transcript V1 core path is ready; Summary/Qwen and hardening scenarios remain pending." } else { "Core pipeline remains blocked until a live record-to-transcript path is proven." }
}
$gatePath = Join-Path $OutputRoot "mvp-release-gate.json"
$gate | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 -LiteralPath $gatePath
$gate | ConvertTo-Json -Depth 12
if ($gateStatus -ne "READY") { exit 2 }

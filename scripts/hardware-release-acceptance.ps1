[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Server','Client','Aggregate')][string]$NodeRole,
    [Parameter(Mandatory)][string]$Scenario,
    [string]$RunId = ([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')),
    [string]$OutputRoot = '',
    [string]$BundleRoot = '',
    [string]$ConfigRoot = 'C:\ProgramData\WhisperXAtom\Server',
    [switch]$AllowReboot,
    [switch]$PrepareReboot,
    [switch]$Resume,
    [switch]$AllowGpuPressure,
    [switch]$AllowTemporaryVhd,
    [switch]$AllowLongRun,
    [switch]$KeepAudio,
    [string]$TemporaryVhdPath,
    [string]$EvidenceRoot = '',
    [string]$PipelineEvidenceRoot = '',
    [ValidateRange(10,20)][int]$AudioAbPairs = 10,
    [string]$AudioAbRatingsPath = ''
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scenarioRegistryPath = Join-Path $PSScriptRoot 'acceptance-scenarios.json'
$scenarioRegistry = if (Test-Path -LiteralPath $scenarioRegistryPath) { Get-Content -LiteralPath $scenarioRegistryPath -Raw | ConvertFrom-Json } else { @() }
$scenarioDefinition = @($scenarioRegistry | Where-Object { [string]$_.name -ieq $Scenario -or @($_.aliases) -contains $Scenario } | Select-Object -First 1)
if ($scenarioDefinition.Count -eq 1) { $Scenario = [string]$scenarioDefinition[0].name }
$BundleRoot = if ($BundleRoot) { [IO.Path]::GetFullPath($BundleRoot) } else { $repo }
$root = if ($OutputRoot) {
    if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) } else { [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot)) }
} else { Join-Path $repo "artifacts\acceptance\$Scenario" }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$started = [DateTimeOffset]::UtcNow
$manifestPath = Join-Path ([IO.Path]::GetFullPath($BundleRoot)) 'release-manifest.json'
$identity = $env:WHISPERX_BUILD_IDENTITY
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    try { $identity = [string](Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).buildIdentity } catch { }
}

$checks = [System.Collections.Generic.List[object]]::new()
$metrics = [ordered]@{}
$safety = [ordered]@{
    audioIncluded = $false
    transcriptIncluded = $false
    credentialsIncluded = $false
    tokensIncluded = $false
    destructiveActions = @()
}

function Add-Check([string]$Name, [string]$Status, [string]$Detail = '') {
    $checks.Add([ordered]@{ name = $Name; status = $Status; detail = $Detail })
}
function Require-Switch([bool]$Condition, [string]$Name, [string]$Message) {
    if ($Condition) { Add-Check $Name 'READY' $Message; return $true }
    Add-Check $Name 'BLOCKED' $Message; return $false
}
function Write-Evidence([string]$Status) {
    $report = [ordered]@{
        schemaVersion = 1
        scenario = $Scenario
        nodeRole = $NodeRole
        runId = $RunId
        buildIdentity = $identity
        startedAtUtc = $started.ToString('O')
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = $Status
        checks = @($checks)
        metrics = $metrics
        safety = $safety
    }
    $path = Join-Path $root "$RunId.json"
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Output $path
}

if ([string]::IsNullOrWhiteSpace($identity) -or $identity -match 'dev|dirty') {
    Add-Check 'cleanIdentity' 'BLOCKED' 'Immutable clean release identity is required.'
} else { Add-Check 'cleanIdentity' 'READY' $identity }

if ($NodeRole -eq 'Server') {
    switch -Regex ($Scenario) {
        '^server-doctor-release$' {
            $doctor = Join-Path ([IO.Path]::GetFullPath($BundleRoot)) 'doctor-server-bundle.ps1'
            if (Test-Path -LiteralPath $doctor) {
                try {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $doctor -BundleRoot $BundleRoot -ConfigRoot $ConfigRoot -Mode Release | Out-Null
                    Add-Check 'serverDoctorRelease' 'READY' 'SERVER_DOCTOR_READY'
                } catch { Add-Check 'serverDoctorRelease' 'FAILED' $_.Exception.Message }
            } else { Add-Check 'serverDoctorRelease' 'BLOCKED' 'DOCTOR_SCRIPT_MISSING' }
        }
        '^no-console-start$' {
            $taskName = 'WhisperXAtom-HardwareAcceptance-Resume'
            if ($PrepareReboot) {
                if (-not $AllowReboot) { Add-Check 'rebootAuthorization' 'BLOCKED' 'Use -AllowReboot with -PrepareReboot.' }
                else {
                    $script = $PSCommandPath
                    $args = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -NodeRole Server -Scenario no-console-start -RunId $RunId -Resume -BundleRoot `"$BundleRoot`" -ConfigRoot `"$ConfigRoot`""
                    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $args
                    $trigger = New-ScheduledTaskTrigger -AtStartup
                    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -RunLevel Highest -Force | Out-Null
                    Add-Check 'resumeTask' 'READY' $taskName
                    $safety.destructiveActions += 'REBOOT_AFTER_OPERATOR_CONFIRMATION'
                }
            } elseif ($Resume) {
                $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
                if ($null -eq $task) { Add-Check 'resumeTask' 'FAILED' 'RESUME_TASK_MISSING' }
                else { Add-Check 'resumeTask' 'READY' 'Resumed after reboot'; Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
            } else { Add-Check 'resumeTask' 'BLOCKED' 'Run -PrepareReboot -AllowReboot, then invoke -Resume after the real reboot.' }
        }
        '^no-console-20x10s$' {
            if (-not $Resume) {
                Add-Check 'noConsoleResume' 'BLOCKED' 'Run this Server scenario with -Resume after the real reboot; no simulated startup is accepted.'
            } else {
                $doctor = Join-Path ([IO.Path]::GetFullPath($BundleRoot)) 'doctor-server-bundle.ps1'
                if (-not (Test-Path -LiteralPath $doctor)) { Add-Check 'serverDoctorQuick' 'BLOCKED' 'DOCTOR_SCRIPT_MISSING' }
                else {
                    try {
                        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $doctor -BundleRoot $BundleRoot -ConfigRoot $ConfigRoot -Mode Quick | Out-Null
                        if ($LASTEXITCODE -ne 0) { throw "SERVER_DOCTOR_EXIT_$LASTEXITCODE" }
                        Add-Check 'serverDoctorQuick' 'READY' 'Authenticated runtime readiness after reboot.'
                        $metrics.noConsole = 'AUTO_RECOVERED'
                    } catch { Add-Check 'serverDoctorQuick' 'FAILED' $_.Exception.Message }
                }
            }
        }
        '^cold-model-runtime$' { Require-Switch $true 'modelVolumesPreserved' 'Cold runtime must be executed by the operator without deleting model volumes.' | Out-Null; Add-Check 'operatorEvidence' 'BLOCKED' 'Attach real cold-model evidence after restart.'; $metrics.outcome = 'BLOCKED_OPERATOR_ACTION' }
        '^gpu-oom$' { Require-Switch $AllowGpuPressure 'gpuPressureAuthorization' 'GPU pressure is explicitly authorized.' | Out-Null; if ($AllowGpuPressure) { Add-Check 'operatorEvidence' 'BLOCKED' 'Run controlled GPU-OOM/recovery and attach evidence.'; $metrics.outcome = 'BLOCKED_OPERATOR_ACTION' } }
        default { Add-Check 'scenario' 'BLOCKED' "Unsupported Server scenario '$Scenario'." }
    }
}
elseif ($NodeRole -eq 'Client') {
    switch -Regex ($Scenario) {
        '^audio-quality$' {
            $script = Join-Path $repo 'scripts\acceptance-audiograph-local-recording.ps1'
            $seconds = 30
            if ($AllowLongRun -and $Scenario -match '8h') { $seconds = 28800 }
            if (Test-Path -LiteralPath $script) {
                try { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Seconds $seconds -AllowPendingArchive -OutputRoot $root | Out-Null; Add-Check 'audioQuality' 'READY' 'Runner completed; inspect child report.' }
                catch { Add-Check 'audioQuality' 'FAILED' $_.Exception.Message }
            } else { Add-Check 'audioQuality' 'BLOCKED' 'AUDIO_ACCEPTANCE_SCRIPT_MISSING' }
        }
        '^audio-quality-ab$' {
            $script = Join-Path $repo 'scripts\audio-quality-ab-gate.ps1'
            if (Test-Path -LiteralPath $script) {
                try {
                    $abArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$script,'-Pairs',$AudioAbPairs,'-OutputRoot',$root)
                    if ($AudioAbRatingsPath) { $abArgs += @('-RatingsPath',$AudioAbRatingsPath) }
                    & powershell.exe @abArgs
                    if ($LASTEXITCODE -ne 0) { throw "AUDIO_AB_GATE_EXIT_$LASTEXITCODE" }
                    Add-Check 'audioQualityAb' 'READY' 'AudioGraph/RAW pair gate completed.'
                }
                catch { Add-Check 'audioQualityAb' 'BLOCKED' "Real A/B pairs and blind operator ratings are required: $($_.Exception.Message)" }
            } else { Add-Check 'audioQualityAb' 'BLOCKED' 'AUDIO_AB_GATE_SCRIPT_MISSING' }
        }
        '^no-console-20x10s$' {
            if (-not $AllowLongRun) {
                Add-Check 'twentyRunAuthorization' 'BLOCKED' 'Use -AllowLongRun for the real 20x10s no-console gate.'
            } else {
                $script = Join-Path $repo 'scripts\acceptance-audiograph-local-recording.ps1'
                $childRoot = Join-Path $root 'runs'
                New-Item -ItemType Directory -Force -Path $childRoot | Out-Null
                $reports = [System.Collections.Generic.List[object]]::new()
                $duplicateIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                for ($run = 1; $run -le 20; $run++) {
                    try {
                        $runStarted = [DateTime]::UtcNow
                        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Seconds 10 -FinalizeTimeoutSeconds 180 -ServerDelivery -AllowPendingArchive -OutputRoot (Join-Path $childRoot ("run-{0:D2}" -f $run)) | Out-Null
                        if ($LASTEXITCODE -ne 0) { throw "CHILD_GATE_EXIT_$LASTEXITCODE" }
                        $candidate = Get-ChildItem -LiteralPath $childRoot -Recurse -File -Filter 'report-*.json' -ErrorAction SilentlyContinue |
                            Where-Object { $_.LastWriteTimeUtc -ge $runStarted } |
                            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
                        if ($null -eq $candidate) { throw 'CHILD_EVIDENCE_MISSING' }
                        $child = Get-Content -LiteralPath $candidate.FullName -Raw | ConvertFrom-Json
                        $reports.Add($child)
                        $jobId = [string]$child.result.processingJobId
                        if ([string]::IsNullOrWhiteSpace($jobId)) { throw 'SERVER_JOB_ID_MISSING' }
                        if (-not $duplicateIds.Add($jobId)) { throw "DUPLICATE_PROCESSING_JOB_ID: $jobId" }
                        if ([string]$child.status -ne 'PASSED' -or [string]$child.result.deliveryState -ne 'CONFIRMED') { throw "CHILD_GATE_NOT_GREEN: $($child.status) / $($child.result.deliveryState)" }
                    } catch { Add-Check ("run-{0:D2}" -f $run) 'FAILED' $_.Exception.Message }
                }
                $metrics.runsCompleted = $reports.Count
                $metrics.uniqueProcessingJobIds = $duplicateIds.Count
                if ($reports.Count -eq 20 -and $duplicateIds.Count -eq 20) {
                    if ([string]::IsNullOrWhiteSpace($PipelineEvidenceRoot)) {
                        Add-Check 'v1V2SummaryEvidence' 'BLOCKED' 'Attach real server evidence with V1/V2/Summary terminal states; delivery alone is insufficient.'
                    } else {
                        $pipelineFiles = @(Get-ChildItem -LiteralPath ([IO.Path]::GetFullPath($PipelineEvidenceRoot)) -Recurse -File -Filter '*.json' -ErrorAction SilentlyContinue)
                        $pipelineReports = @($pipelineFiles | ForEach-Object { try { Get-Content $_.FullName -Raw | ConvertFrom-Json } catch { } } | Where-Object { $_.runId -eq $RunId -and $_.buildIdentity -eq $identity })
                        $green = @($pipelineReports | Where-Object { $_.v1Status -in @('READY','PARTIAL_READY') -and $_.v2Status -in @('READY','PARTIAL_READY') -and $_.summaryStatus -in @('READY','NEEDS_REVIEW','PARTIAL_READY') -and [int]$_.duplicateCount -eq 0 }).Count
                        if ($green -eq 20) { Add-Check 'v1V2SummaryEvidence' 'READY' '20/20 terminal V1/V2/Summary evidence.'; $metrics.outcome = 'AUTO_RECOVERED' }
                        else { Add-Check 'v1V2SummaryEvidence' 'BLOCKED' "Expected 20 green server evidence records, got $green." }
                    }
                }
            }
        }
        '^audio-device-loss$|^system-audio-device-loss$' { Add-Check 'operatorDeviceLoss' 'BLOCKED' 'Disconnect and restore the physical device, then rerun with operator evidence.'; $metrics.outcome = 'BLOCKED_OPERATOR_ACTION' }
        '^low-disk-during-recording$' {
            if (-not $AllowTemporaryVhd) { Add-Check 'temporaryVhdAuthorization' 'BLOCKED' 'Use -AllowTemporaryVhd and a new run-scoped VHD path.'; $metrics.outcome = 'BLOCKED_OPERATOR_ACTION' }
            elseif ([string]::IsNullOrWhiteSpace($TemporaryVhdPath)) { Add-Check 'temporaryVhdPath' 'BLOCKED' 'TemporaryVhdPath is required; existing disks are never modified.'; $metrics.outcome = 'BLOCKED_OPERATOR_ACTION' }
            else { Add-Check 'temporaryVhdPath' 'BLOCKED' 'Operator must create/mount the isolated VHDX and attach evidence; no existing volume is touched.'; $metrics.outcome = 'BLOCKED_OPERATOR_ACTION' }
        }
        '^4h-recording$|^8h-recording$' { if ($AllowLongRun) { Add-Check 'longRunAuthorization' 'READY' 'Long-run gate explicitly authorized.'; Add-Check 'operatorEvidence' 'BLOCKED' 'Run the real installed-client endurance gate.'; $metrics.outcome = 'BLOCKED_OPERATOR_ACTION' } else { Add-Check 'longRunAuthorization' 'BLOCKED' 'Use -AllowLongRun.'; $metrics.outcome = 'BLOCKED_OPERATOR_ACTION' } }
        default { Add-Check 'scenario' 'BLOCKED' "Unsupported Client scenario '$Scenario'." }
    }
}
else {
    $evidenceRoot = if ($EvidenceRoot) { [IO.Path]::GetFullPath($EvidenceRoot) } else { Join-Path $repo 'artifacts\acceptance' }
    $files = @(Get-ChildItem -LiteralPath $evidenceRoot -Recurse -File -Filter '*.json' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notlike "*$RunId.json" })
    $reports = @()
    foreach ($file in $files) { try { $reports += @(Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json) } catch { } }
    $selected = @($reports | Where-Object { $_.runId -eq $RunId -and $_.buildIdentity -eq $identity })
    if ($selected.Count -lt 2) { Add-Check 'sameIdentityEvidence' 'BLOCKED' 'Need Server and Client evidence with the same runId and clean identity.' }
    elseif (@($selected | Where-Object status -notin @('PASSED','READY','GREEN')).Count -gt 0) { Add-Check 'sameIdentityEvidence' 'FAILED' 'One or more node evidence reports are not green.' }
    else { Add-Check 'sameIdentityEvidence' 'READY' "Accepted $($selected.Count) node reports." }
    $metrics.nodeEvidenceCount = $selected.Count
}

$failed = @($checks | Where-Object status -in @('FAILED','BLOCKED'))
$status = if ($failed.Count -eq 0) { 'PASSED' } elseif (@($checks | Where-Object status -eq 'FAILED').Count -gt 0) { 'FAILED' } else { 'BLOCKED' }
$out = Write-Evidence $status
if ($status -ne 'PASSED') { exit 2 }

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
    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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
        '^cold-model-runtime$' { Require-Switch $true 'modelVolumesPreserved' 'Cold runtime must be executed by the operator without deleting model volumes.' | Out-Null; Add-Check 'operatorEvidence' 'BLOCKED' 'Attach real cold-model evidence after restart.' }
        '^gpu-oom$' { Require-Switch $AllowGpuPressure 'gpuPressureAuthorization' 'GPU pressure is explicitly authorized.' | Out-Null; if ($AllowGpuPressure) { Add-Check 'operatorEvidence' 'BLOCKED' 'Run controlled GPU-OOM/recovery and attach evidence.' } }
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
        '^audio-device-loss$|^system-audio-device-loss$' { Add-Check 'operatorDeviceLoss' 'BLOCKED' 'Disconnect and restore the physical device, then rerun with operator evidence.' }
        '^low-disk-during-recording$' {
            if (-not $AllowTemporaryVhd) { Add-Check 'temporaryVhdAuthorization' 'BLOCKED' 'Use -AllowTemporaryVhd and a new run-scoped VHD path.' }
            elseif ([string]::IsNullOrWhiteSpace($TemporaryVhdPath)) { Add-Check 'temporaryVhdPath' 'BLOCKED' 'TemporaryVhdPath is required; existing disks are never modified.' }
            else { Add-Check 'temporaryVhdPath' 'BLOCKED' 'Operator must create/mount the isolated VHDX and attach evidence; no existing volume is touched.' }
        }
        '^4h-recording$|^8h-recording$' { if ($AllowLongRun) { Add-Check 'longRunAuthorization' 'READY' 'Long-run gate explicitly authorized.'; Add-Check 'operatorEvidence' 'BLOCKED' 'Run the real installed-client endurance gate.' } else { Add-Check 'longRunAuthorization' 'BLOCKED' 'Use -AllowLongRun.' } }
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

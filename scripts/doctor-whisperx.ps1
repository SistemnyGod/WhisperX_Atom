[CmdletBinding()]
param([switch]$SkipRegistry)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo
$runtimeRoot = Get-WhisperXRuntimeRoot -RepoPath $repo
$artifactRoot = Join-Path $repo "artifacts\transcription-mvp"
$runtimeManifestPath = Join-Path $repo "artifacts\release\runtime-manifest.json"
& (Join-Path $PSScriptRoot "write-runtime-manifest.ps1") -RepoPath $repo *> $null
$mode = if ($env:GPU_WORKER_MODE) { $env:GPU_WORKER_MODE.ToLowerInvariant() } else { "host" }

$transcriptDoctor = Join-Path $PSScriptRoot "doctor-transcription-mvp.ps1"
if ($SkipRegistry) { & $transcriptDoctor -SkipRegistry *> $null } else { & $transcriptDoctor *> $null }
$transcriptExit = $LASTEXITCODE
$transcriptReport = $null
if (Test-Path -LiteralPath (Join-Path $artifactRoot "doctor.json")) {
    $transcriptReport = Get-Content -LiteralPath (Join-Path $artifactRoot "doctor.json") -Raw | ConvertFrom-Json
}

$hostReport = $null
$hostExit = 0
$hostLiveStatus = $null
if ($mode -eq "host") {
    & (Join-Path $PSScriptRoot "doctor-host-gpu-worker.ps1") *> $null
    $hostExit = $LASTEXITCODE
    if (Test-Path -LiteralPath (Join-Path $artifactRoot "host-gpu-doctor.json")) {
        $hostReport = Get-Content -LiteralPath (Join-Path $artifactRoot "host-gpu-doctor.json") -Raw | ConvertFrom-Json
    }
    $hostLiveStatus = Get-WhisperXHostWorkerStatus -RepoPath $repo -PythonPath $env:WHISPERX_HOST_PYTHON
}

$readiness = if ($transcriptReport -and $transcriptReport.diagnostics.systemReadiness) { $transcriptReport.diagnostics.systemReadiness } else { $null }
$components = if ($readiness) { $readiness.components } else { $null }
$workers = if ($components) { $components.workers } else { $null }
$gpuWorker = if ($workers) { $workers.'gpu-worker' } else { $null }
$recorder = if ($transcriptReport) { $transcriptReport.checks.recorder } else { "OPTIONAL" }
$desktop = if (@(Get-Process -Name "WhisperX.Atom.Desktop" -ErrorAction SilentlyContinue).Count -gt 0) { "READY" } else { "OPTIONAL" }
$hfStatus = if ($components -and $components.hfDiarization.status) { [string]$components.hfDiarization.status } else { "DEGRADED" }
$dockerStatus = if ($transcriptReport -and $transcriptReport.checks.docker -eq "ready") { "READY" } else { "FAILED" }
$apiStatus = if ($transcriptReport -and $transcriptReport.checks.systemReadiness -eq "ready") { "READY" } else { "FAILED" }
$postgresStatus = if ($components -and $components.postgres.status -eq "READY") { "READY" } else { "FAILED" }
$natsStatus = if ($components -and $components.nats.status -eq "READY") { "READY" } else { "FAILED" }
$mediaStatus = if ($workers -and $workers.'media-worker'.status -eq "READY") { "READY" } else { "FAILED" }
$hostDetails = if ($hostReport) { $hostReport.details } else { $null }
$hostCuda = if ($hostDetails) { $hostDetails.cuda } else { $null }
$hostStatus = if ($mode -eq "host") {
    if ($hostExit -eq 0 -and $hostLiveStatus -and $hostLiveStatus.processAlive -and $hostLiveStatus.commandMatchesPython -and $hostLiveStatus.heartbeatReady) { "READY" } else { "FAILED" }
} else { "OPTIONAL" }
$cudaStatus = if ($hostCuda -and $hostCuda.cudaAvailable) { "READY" } elseif ($gpuWorker -and $gpuWorker.capabilities.cudaAvailable) { "READY" } else { "FAILED" }
$whisperStatus = if ($hostReport -and $hostReport.checks.python -eq "READY" -and $hostReport.checks.cuda -eq "READY" -and $hostLiveStatus -and $hostLiveStatus.heartbeatReady) { "READY" } elseif ($gpuWorker -and $gpuWorker.status -eq "READY") { "READY" } else { "FAILED" }
$mediaWorkerStatus = $mediaStatus
$failed = @($dockerStatus, $apiStatus, $postgresStatus, $natsStatus, $mediaWorkerStatus, $hostStatus, $cudaStatus, $whisperStatus) -contains "FAILED"
$degraded = $hfStatus -eq "DEGRADED" -or $recorder -eq "warning"
$overall = if ($failed) { "FAILED" } elseif ($degraded) { "DEGRADED" } else { "READY" }

$state = [ordered]@{
    overall = $overall
    docker = $dockerStatus
    postgres = $postgresStatus
    nats = $natsStatus
    api = $apiStatus
    mediaWorker = $mediaWorkerStatus
    hostGpuWorker = $hostStatus
    cuda = $cudaStatus
    whisperX = $whisperStatus
    hfDiarization = $hfStatus
    recorder = if ($recorder -eq "ready") { "READY" } else { "OPTIONAL" }
    desktop = $desktop
    qwen = "DISABLED"
    runtime = $mode
    hostGpuWorkerPid = if ($hostLiveStatus -and $hostLiveStatus.pid) { $hostLiveStatus.pid } elseif ($hostDetails -and $hostDetails.pid) { $hostDetails.pid } else { $null }
    lastHeartbeat = if ($gpuWorker) { $gpuWorker.lastSeenAt } else { $null }
    currentJobId = if ($gpuWorker) { $gpuWorker.currentJobId } else { $null }
    lastErrorCode = if ($gpuWorker) { $gpuWorker.lastErrorCode } else { $null }
    hostVersions = if ($hostCuda) { $hostCuda } else { $null }
    runtimeManifest = $runtimeManifestPath
}
Write-WhisperXRuntimeState -RepoPath $repo -State $state
$state | ConvertTo-Json -Depth 12
if ($failed) { exit 1 }

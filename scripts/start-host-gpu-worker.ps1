[CmdletBinding()]
param(
    [string]$PythonPath,
    [int]$StartupTimeoutSeconds = 240
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo
if ([string]::IsNullOrWhiteSpace($PythonPath)) { $PythonPath = $env:WHISPERX_HOST_PYTHON }
if ([string]::IsNullOrWhiteSpace($PythonPath) -or -not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
    throw "HOST_PYTHON_NOT_FOUND: set WHISPERX_HOST_PYTHON to the CUDA/WhisperX python.exe."
}

$runtimeRoot = Get-WhisperXRuntimeRoot -RepoPath $repo
$pidPath = Join-Path $runtimeRoot "host-gpu-worker.pid"
$stdoutPath = Join-Path $runtimeRoot "host-gpu-worker.log"
$stderrPath = Join-Path $runtimeRoot "host-gpu-worker.error.log"
$process = $null

function Wait-HostGpuWorkerReady([int]$ProcessId) {
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Seconds 2
        $running = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if (-not $running) {
            throw "WORKER_HEARTBEAT_LOST: host GPU Worker exited during startup."
        }
        $status = Get-WhisperXHostWorkerStatus -RepoPath $repo -PythonPath $PythonPath
        if ($status.heartbeatReady) {
            Write-WhisperXRuntimeState -RepoPath $repo -State ([ordered]@{
                overall = "READY"
                hostGpuWorker = "READY"
                hostGpuWorkerPid = $ProcessId
                runtime = "host"
                lastErrorCode = $null
            })
            Write-Host "Host GPU Worker is ready (PID $ProcessId)." -ForegroundColor Green
            return
        }
        if (-not $status.heartbeatLive) {
            throw "WORKER_HEARTBEAT_LOST: host GPU Worker stopped reporting liveness during startup."
        }
    } while ((Get-Date) -lt $deadline)
    throw "WORKER_HEARTBEAT_TIMEOUT: no fresh host GPU Worker heartbeat within $StartupTimeoutSeconds seconds."
}

try {
    $existing = Get-WhisperXHostWorkerStatus -RepoPath $repo -PythonPath $PythonPath
    if ($existing.processAlive) {
        if (-not $existing.commandMatchesPython) {
            throw "HOST_WORKER_PID_MISMATCH: refusing to control an unrelated process."
        }
        if ($existing.heartbeatReady) {
            Write-Host "Host GPU Worker is already ready (PID $($existing.pid))." -ForegroundColor Green
            return
        }
        if (-not $existing.heartbeatLive) { throw "HOST_WORKER_NOT_READY: existing process has no fresh heartbeat." }
        Wait-HostGpuWorkerReady -ProcessId ([int]$existing.pid)
        return
    }
    $candidates = @(Get-WhisperXHostWorkerCandidates -PythonPath $PythonPath)
    $candidatePids = @($candidates | ForEach-Object { $_.pid })
    $roots = @($candidates | Where-Object { $candidatePids -notcontains $_.parentPid })
    if ($roots.Count -gt 1) {
        throw "HOST_WORKER_DUPLICATE: found $($roots.Count) host GPU Worker process trees; refusing to start another."
    }
    if ($roots.Count -eq 1) {
        Set-Content -LiteralPath $pidPath -Value $roots[0].pid -Encoding ascii
        $adopted = Get-WhisperXHostWorkerStatus -RepoPath $repo -PythonPath $PythonPath
        if (-not ($adopted.processAlive -and $adopted.commandMatchesPython -and $adopted.heartbeatLive)) {
            throw "HOST_WORKER_NOT_READY: existing host Worker process has no fresh heartbeat."
        }
        Wait-HostGpuWorkerReady -ProcessId ([int]$adopted.pid)
        return
    }
    if (Test-Path -LiteralPath $pidPath) { Remove-Item -LiteralPath $pidPath -Force }

    & $PythonPath -c "import nats, psycopg, torch, whisperx; assert torch.cuda.is_available(); print(torch.cuda.get_device_name(0))" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "WHISPERX_IMPORT_FAILED: host Python cannot import CUDA/WhisperX dependencies." }

    Push-Location $repo
    try {
        & docker compose --env-file (Join-Path $repo ".env") -f compose.dev.yml --profile gpu stop gpu-worker | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "DOCKER_GPU_STOP_FAILED: unable to stop the container GPU Worker." }

        if ((Test-Path -LiteralPath $stdoutPath) -and (Get-Item -LiteralPath $stdoutPath).Length -gt 50MB) {
            Move-Item -LiteralPath $stdoutPath -Destination "$stdoutPath.previous" -Force
        }
        if ((Test-Path -LiteralPath $stderrPath) -and (Get-Item -LiteralPath $stderrPath).Length -gt 50MB) {
            Move-Item -LiteralPath $stderrPath -Destination "$stderrPath.previous" -Force
        }
        $env:WORKER_INSTANCE_ID = "gpu-worker-$env:COMPUTERNAME-host"
        $process = Start-Process -FilePath $PythonPath `
            -ArgumentList @("-m", "workers.ml_worker.worker") `
            -WorkingDirectory $repo `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -PassThru
        Set-Content -LiteralPath $pidPath -Value $process.Id -Encoding ascii
    }
    finally { Pop-Location }

    Wait-HostGpuWorkerReady -ProcessId $process.Id
}
catch {
    if ($process -and -not $process.HasExited) { Stop-WhisperXProcessTree -ProcessId $process.Id }
    if (Test-Path -LiteralPath $pidPath) { Remove-Item -LiteralPath $pidPath -Force }
    throw
}

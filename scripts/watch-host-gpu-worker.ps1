[CmdletBinding()]
param(
    [int]$IntervalSeconds = 10,
    [int]$StaleAfterSeconds = 75,
    [int]$MaxRestarts = 5,
    [int]$RestartWindowSeconds = 900,
    [int]$RestartDelaySeconds = 30,
    [switch]$Once
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo
$runtimeRoot = Get-WhisperXRuntimeRoot -RepoPath $repo
$watchdogPidPath = Join-Path $runtimeRoot "host-gpu-watchdog.pid"
$logPath = Join-Path $runtimeRoot "host-gpu-watchdog.log"
$pythonPath = $env:WHISPERX_HOST_PYTHON
$mutex = New-Object System.Threading.Mutex($false, "Global\WhisperXAtomHostGpuWatchdog")
if (-not $mutex.WaitOne(0)) { exit 0 }

function Write-WatchdogLog([string]$Message) {
    $line = "{0:u} {1}" -f (Get-Date), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
}

$restartTimes = [System.Collections.Generic.List[DateTime]]::new()
Set-Content -LiteralPath $watchdogPidPath -Value $PID -Encoding ascii
try {
    do {
        $now = Get-Date
        while ($restartTimes.Count -gt 0 -and ($now - $restartTimes[0]).TotalSeconds -gt $RestartWindowSeconds) {
            $restartTimes.RemoveAt(0)
        }
        $status = Get-WhisperXHostWorkerStatus -RepoPath $repo -PythonPath $pythonPath
        $starting = $status.heartbeatLive -and $status.heartbeat -and [string]$status.heartbeat.status -eq "STARTING"
        $healthy = $status.processAlive -and $status.commandMatchesPython -and ($status.heartbeatReady -or $starting)
        if ($healthy) {
            $runtimeState = if ($status.heartbeatReady) { "READY" } else { "STARTING" }
            Write-WhisperXRuntimeState -RepoPath $repo -State ([ordered]@{
                overall = $runtimeState
                hostGpuWorker = $runtimeState
                hostGpuWorkerPid = $status.pid
                hostGpuHeartbeat = $status.heartbeat
                hostGpuRestartCount = $restartTimes.Count
                runtime = "host"
                lastErrorCode = $null
            })
        } else {
            $reason = if (-not $status.processAlive) { "HOST_WORKER_NOT_RUNNING" } elseif (-not $status.commandMatchesPython) { "HOST_WORKER_PID_MISMATCH" } else { "WORKER_HEARTBEAT_STALE" }
            Write-WatchdogLog "detected $reason pid=$($status.pid) heartbeat=$($status.heartbeatReady)"
            if ($restartTimes.Count -ge $MaxRestarts) {
                Write-WhisperXRuntimeState -RepoPath $repo -State ([ordered]@{
                    overall = "DEGRADED"
                    hostGpuWorker = "DEGRADED"
                    hostGpuWorkerPid = $status.pid
                    hostGpuRestartCount = $restartTimes.Count
                    runtime = "host"
                    lastErrorCode = "WORKER_RESTART_LIMIT"
                })
                Write-WatchdogLog "restart limit reached; queue and archive were preserved"
                exit 2
            }
            if ($status.processAlive) { Stop-WhisperXProcessTree -ProcessId ([int]$status.pid) }
            $pidPath = Join-Path $runtimeRoot "host-gpu-worker.pid"
            Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
            if (-not $Once) { Start-Sleep -Seconds $RestartDelaySeconds }
            & (Join-Path $PSScriptRoot "start-host-gpu-worker.ps1") -PythonPath $pythonPath
            if ($LASTEXITCODE -ne 0) { throw "HOST_WORKER_RESTART_FAILED" }
            $restartTimes.Add((Get-Date))
            Write-WatchdogLog "restarted host GPU Worker count=$($restartTimes.Count)"
        }
        if ($Once) { break }
        Start-Sleep -Seconds $IntervalSeconds
    } while ($true)
}
finally {
    Remove-Item -LiteralPath $watchdogPidPath -Force -ErrorAction SilentlyContinue
    $mutex.ReleaseMutex() | Out-Null
    $mutex.Dispose()
}

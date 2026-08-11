[CmdletBinding()]
param([switch]$StopRecorder)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
Import-WhisperXDotEnv -RepoPath $repo
$runtimeRoot = Get-WhisperXRuntimeRoot -RepoPath $repo
$watchdogPidPath = Join-Path $runtimeRoot "host-gpu-watchdog.pid"
if (Test-Path -LiteralPath $watchdogPidPath) {
    try { Stop-WhisperXProcessTree -ProcessId ([int](Get-Content $watchdogPidPath -Raw)) } catch { }
    Remove-Item -LiteralPath $watchdogPidPath -Force -ErrorAction SilentlyContinue
}
& (Join-Path $PSScriptRoot "stop-host-gpu-worker.ps1")
& docker compose --env-file (Join-Path $repo ".env") -f compose.dev.yml --profile core --profile gpu stop
if ($LASTEXITCODE -ne 0) { throw "DOCKER_CORE_STOP_FAILED: transcript-only Compose could not stop." }
& docker compose --env-file (Join-Path $repo ".env") -f compose.dev.yml --profile llm --profile llm-diagnostic stop summary-worker llama-server | Out-Null
if ($StopRecorder) {
    try { Stop-Service -Name "WhisperXAtomRecorder" -ErrorAction Stop } catch { Write-Warning "RECORDER_STOP_FAILED: Recorder Service was not stopped." }
    $recorderHostPidPath = Join-Path $runtimeRoot "recorder-host.pid"
    if (Test-Path -LiteralPath $recorderHostPidPath) {
        try { Stop-WhisperXProcessTree -ProcessId ([int](Get-Content $recorderHostPidPath -Raw)) } catch { }
        Remove-Item -LiteralPath $recorderHostPidPath -Force -ErrorAction SilentlyContinue
    }
}
Write-WhisperXRuntimeState -RepoPath $repo -State ([ordered]@{
    overall = "STOPPED"
    runtime = if ($env:GPU_WORKER_MODE) { $env:GPU_WORKER_MODE } else { "host" }
    docker = "STOPPED"
    hostGpuWorker = "STOPPED"
    recorder = if ($StopRecorder) { "STOPPED" } else { "PRESERVED" }
    qwen = "DISABLED"
})
Write-Host "WhisperX runtime stopped. Volumes, queue, spool and archive were preserved." -ForegroundColor Green

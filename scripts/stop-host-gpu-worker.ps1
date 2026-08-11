[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
$runtimeRoot = Get-WhisperXRuntimeRoot -RepoPath $repo
$pidPath = Join-Path $runtimeRoot "host-gpu-worker.pid"
if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
    try {
        $workerPid = [int](Get-Content -LiteralPath $pidPath -Raw).Trim()
        Stop-WhisperXProcessTree -ProcessId $workerPid
    } finally {
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "Host GPU Worker stopped. Queue, spool and archive were preserved." -ForegroundColor Green

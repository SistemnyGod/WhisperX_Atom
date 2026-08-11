[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pidPath = Join-Path $repo "artifacts\runtime\host-gpu-worker.pid"
if (-not (Test-Path -LiteralPath $pidPath)) {
    Write-Host "Host GPU worker is not running."
    exit 0
}

$workerPid = [int](Get-Content -LiteralPath $pidPath -Raw)
$process = Get-Process -Id $workerPid -ErrorAction SilentlyContinue
if ($process) {
    & taskkill.exe /PID $workerPid /T /F | Out-Null
}
Remove-Item -LiteralPath $pidPath -Force
Write-Host "Host GPU worker stopped. Data and queue were preserved." -ForegroundColor Green

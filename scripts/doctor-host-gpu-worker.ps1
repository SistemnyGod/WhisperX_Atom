[CmdletBinding()]
param([string]$PythonPath = $env:WHISPERX_HOST_PYTHON)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$envFile = Join-Path $repo ".env"
$artifactRoot = Join-Path $repo "artifacts\transcription-mvp"
$pidPath = Join-Path $repo "artifacts\runtime\host-gpu-worker.pid"
$checks = [ordered]@{}
$details = [ordered]@{}

foreach ($line in Get-Content -LiteralPath $envFile -ErrorAction SilentlyContinue) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
        $key = $Matches[1]; $value = $Matches[2].Trim().Trim('"')
        if ([string]::IsNullOrWhiteSpace((Get-Item -Path "Env:$key" -ErrorAction SilentlyContinue).Value)) { Set-Item -Path "Env:$key" -Value $value }
    }
}
if ([string]::IsNullOrWhiteSpace($PythonPath)) { $PythonPath = $env:WHISPERX_HOST_PYTHON }

$checks.python = if ($PythonPath -and (Test-Path -LiteralPath $PythonPath)) { "READY" } else { "UNAVAILABLE" }
if ($checks.python -eq "READY") {
    $cudaJson = & $PythonPath -c "import json,torch,whisperx,nats,psycopg; print(json.dumps({'torch':torch.__version__,'cuda':torch.version.cuda,'cudaAvailable':torch.cuda.is_available(),'device':torch.cuda.get_device_name(0) if torch.cuda.is_available() else None}))"
    if ($LASTEXITCODE -eq 0) { $details.cuda = $cudaJson | ConvertFrom-Json; $checks.cuda = if ($details.cuda.cudaAvailable) { "READY" } else { "UNAVAILABLE" } }
    else { $checks.cuda = "UNAVAILABLE" }
} else { $checks.cuda = "UNAVAILABLE" }

$checks.process = "UNAVAILABLE"
if (Test-Path -LiteralPath $pidPath) {
    $workerPid = [int](Get-Content -LiteralPath $pidPath -Raw)
    if (Get-Process -Id $workerPid -ErrorAction SilentlyContinue) { $checks.process = "READY"; $details.pid = $workerPid }
}

$postgresPort = if ($env:POSTGRES_HOST_PORT) { [int]$env:POSTGRES_HOST_PORT } else { 55432 }
$natsPort = if ($env:NATS_HOST_PORT) { [int]$env:NATS_HOST_PORT } else { 4222 }
$checks.postgresPort = if (Test-NetConnection 127.0.0.1 -Port $postgresPort -InformationLevel Quiet -WarningAction SilentlyContinue) { "READY" } else { "UNAVAILABLE" }
$checks.natsPort = if (Test-NetConnection 127.0.0.1 -Port $natsPort -InformationLevel Quiet -WarningAction SilentlyContinue) { "READY" } else { "UNAVAILABLE" }

$ok = @($checks.Values | Where-Object { $_ -ne "READY" }).Count -eq 0
$report = [ordered]@{ generatedAtUtc=[DateTimeOffset]::UtcNow; runtime="host-gpu-worker"; checks=$checks; details=$details; ok=$ok }
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $artifactRoot "host-gpu-doctor.json")
$report | ConvertTo-Json -Depth 6
if (-not $ok) { exit 1 }

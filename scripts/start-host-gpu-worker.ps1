[CmdletBinding()]
param(
    [string]$PythonPath = $env:WHISPERX_HOST_PYTHON,
    [int]$StartupTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$envFile = Join-Path $repo ".env"
$runtimeRoot = Join-Path $repo "artifacts\runtime"
$pidPath = Join-Path $runtimeRoot "host-gpu-worker.pid"
$stdoutPath = Join-Path $runtimeRoot "host-gpu-worker.log"
$stderrPath = Join-Path $runtimeRoot "host-gpu-worker.error.log"

function Import-DotEnv([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
            $key = $Matches[1]
            $value = $Matches[2].Trim().Trim('"')
            if ([string]::IsNullOrWhiteSpace((Get-Item -Path "Env:$key" -ErrorAction SilentlyContinue).Value)) {
                Set-Item -Path "Env:$key" -Value $value
            }
        }
    }
}

Import-DotEnv $envFile
if ([string]::IsNullOrWhiteSpace($PythonPath)) { $PythonPath = $env:WHISPERX_HOST_PYTHON }
if ([string]::IsNullOrWhiteSpace($PythonPath) -or -not (Test-Path -LiteralPath $PythonPath)) {
    throw "Set WHISPERX_HOST_PYTHON to python.exe from the CUDA/WhisperX virtualenv."
}

New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
if (Test-Path -LiteralPath $pidPath) {
    $existingPid = [int](Get-Content -LiteralPath $pidPath -Raw)
    if (Get-Process -Id $existingPid -ErrorAction SilentlyContinue) {
        Write-Host "Host GPU worker is already running (PID $existingPid)." -ForegroundColor Green
        exit 0
    }
    Remove-Item -LiteralPath $pidPath -Force
}

& $PythonPath -c "import nats, psycopg, torch, whisperx; assert torch.cuda.is_available(); print(torch.cuda.get_device_name(0))" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Host Python is not ready for the CUDA/WhisperX worker." }

$postgresPort = if ($env:POSTGRES_HOST_PORT) { $env:POSTGRES_HOST_PORT } else { "55432" }
$natsPort = if ($env:NATS_HOST_PORT) { $env:NATS_HOST_PORT } else { "4222" }
$database = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { "whisperx_atom" }
$databaseUser = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { "whisperx" }
if ([string]::IsNullOrWhiteSpace($env:POSTGRES_PASSWORD)) { throw "POSTGRES_PASSWORD is missing from .env." }

Push-Location $repo
try {
    & docker compose --env-file $envFile -f compose.dev.yml --profile gpu stop gpu-worker | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to stop the container GPU worker." }

    $env:PYTHONPATH = $repo
    $env:NATS_URL = "nats://127.0.0.1:$natsPort"
    $env:DATABASE_URL = "host=127.0.0.1 port=$postgresPort dbname=$database user=$databaseUser password=$($env:POSTGRES_PASSWORD)"
    $env:WHISPERX_DATA_HOST = if ($env:WHISPERX_DATA_HOST) { $env:WHISPERX_DATA_HOST } else { "C:\WhisperXAtom\Data" }
    $env:GPU_WORKER_RUNTIME = "host"
    $env:DEVICE = "cuda"
    $env:REQUIRE_CUDA = "true"
    # WhisperX/pyannote still loads its trusted bundled VAD checkpoint through
    # the pre-PyTorch-2.6 API. Scope compatibility mode to this worker process.
    $env:TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD = "1"
    $env:LLM_HEALTH_HOST = "127.0.0.1"
    if (-not $env:LLM_HEALTH_PORT) { $env:LLM_HEALTH_PORT = "18080" }

    $process = Start-Process -FilePath $PythonPath `
        -ArgumentList @("-m", "workers.ml_worker.worker") `
        -WorkingDirectory $repo `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    Set-Content -LiteralPath $pidPath -Value $process.Id -Encoding ascii

    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Seconds 2
        if ($process.HasExited) {
            $detail = if (Test-Path -LiteralPath $stderrPath) { (Get-Content -LiteralPath $stderrPath -Tail 20) -join [Environment]::NewLine } else { "" }
            throw "Host GPU worker exited during startup.`n$detail"
        }
        & $PythonPath (Join-Path $PSScriptRoot "probe_host_gpu_worker.py") 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Host GPU worker is ready: PID $($process.Id), Windows CUDA/WhisperX." -ForegroundColor Green
            exit 0
        }
    } while ((Get-Date) -lt $deadline)
    throw "Host GPU worker started, but no heartbeat appeared within $StartupTimeoutSeconds seconds."
}
catch {
    if ($process -and -not $process.HasExited) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
    }
    if (Test-Path -LiteralPath $pidPath) { Remove-Item -LiteralPath $pidPath -Force }
    throw
}
finally {
    Pop-Location
}

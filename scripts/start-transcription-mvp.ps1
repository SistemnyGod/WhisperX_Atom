[CmdletBinding()]
param(
    [switch]$SkipDesktop,
    [switch]$SkipRecorder,
    [switch]$StartWatchdog,
    [switch]$Rebuild,
    [ValidateSet("host", "container")][string]$GpuMode = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
. (Join-Path $PSScriptRoot "Resolve-RecorderRuntime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo
if ([string]::IsNullOrWhiteSpace($GpuMode)) { $GpuMode = if ($env:GPU_WORKER_MODE) { $env:GPU_WORKER_MODE.ToLowerInvariant() } else { "host" } }
$env:GPU_WORKER_MODE = $GpuMode
$env:AUTO_SUMMARY_ENABLED = "false"
$env:DIARIZATION_MODE = "preferred"

foreach ($required in @("POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN")) {
    if ([string]::IsNullOrWhiteSpace((Get-Item -Path "Env:$required" -ErrorAction SilentlyContinue).Value)) {
        throw "ENV_REQUIRED: $required is missing from .env."
    }
}
if (-not (Get-Command docker.exe -ErrorAction SilentlyContinue)) { throw "DOCKER_UNAVAILABLE: docker.exe was not found." }
if (-not (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)) { throw "FFMPEG_UNAVAILABLE: ffmpeg.exe was not found in PATH." }

$dataRoot = if ($env:WHISPERX_DATA_HOST) { $env:WHISPERX_DATA_HOST } else { "C:\WhisperXAtom\Data" }
$inboxRoot = if ($env:WHISPERX_INBOX_HOST) { $env:WHISPERX_INBOX_HOST } else { "C:\WhisperXAtom\Inbox" }
$archiveRoot = if ($env:WHISPERX_ARCHIVE_HOST) { $env:WHISPERX_ARCHIVE_HOST } else { "C:\WhisperXAtom\Archive" }
foreach ($path in @($dataRoot, $inboxRoot, $archiveRoot)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }

Push-Location $repo
try {
    docker info | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "DOCKER_UNAVAILABLE: Docker Desktop is not ready." }
    & docker compose --env-file (Join-Path $repo ".env") -f compose.dev.yml --profile llm --profile llm-diagnostic stop summary-worker llama-server | Out-Null

    $compose = @("compose", "--env-file", (Join-Path $repo ".env"), "-f", "compose.dev.yml", "--profile", "core")
    if ($GpuMode -eq "container") { $compose += @("--profile", "gpu") }
    $compose += @("up", "-d", "--pull", "never")
    if ($Rebuild) { $compose += "--build" }
    & docker @compose
    if ($LASTEXITCODE -ne 0) { throw "DOCKER_CORE_START_FAILED: transcript-only Compose could not start." }

    if ($GpuMode -eq "host") {
        & (Join-Path $PSScriptRoot "start-host-gpu-worker.ps1") -PythonPath $env:WHISPERX_HOST_PYTHON
        if ($LASTEXITCODE -ne 0) { throw "HOST_WORKER_START_FAILED: host GPU Worker did not become ready." }
    }

    & (Join-Path $PSScriptRoot "doctor-transcription-mvp.ps1") -SkipRegistry
    if ($LASTEXITCODE -ne 0) { throw "API_NOT_READY: Transcript MVP readiness check failed." }

    if (-not $SkipRecorder) {
        $captureEngine = (Resolve-RecorderRuntime).CaptureEngine
        if ($captureEngine.Trim().ToUpperInvariant() -eq "AUDIOGRAPH") {
            $env:AUDIO_CAPTURE_ENGINE = "AUDIOGRAPH"
            & (Join-Path $PSScriptRoot "start-recorder-host.ps1")
            if ($LASTEXITCODE -ne 0) { throw "RECORDER_HOST_UNAVAILABLE: exit code $LASTEXITCODE" }
        }
        else {
            $env:AUDIO_CAPTURE_ENGINE = "LEGACY_WASAPI"
            & (Join-Path $PSScriptRoot "start-recorder-service.ps1")
            if ($LASTEXITCODE -ne 0) { throw "RECORDER_SERVICE_UNAVAILABLE: exit code $LASTEXITCODE" }
        }
    }
    if (-not $SkipDesktop) { & (Join-Path $PSScriptRoot "launch-desktop.ps1") }
    if ($StartWatchdog -and $GpuMode -eq "host") {
        $runtimeRoot = Get-WhisperXRuntimeRoot -RepoPath $repo
        $watchdogPidPath = Join-Path $runtimeRoot "host-gpu-watchdog.pid"
        $watchdogRunning = $false
        if (Test-Path -LiteralPath $watchdogPidPath) {
            try { $watchdogRunning = $null -ne (Get-Process -Id ([int](Get-Content $watchdogPidPath -Raw)) -ErrorAction SilentlyContinue) } catch { }
        }
        if (-not $watchdogRunning) {
            $watchdog = Join-Path $PSScriptRoot "watch-host-gpu-worker.ps1"
            $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$watchdog`""
            Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -WorkingDirectory $repo -WindowStyle Hidden | Out-Null
        }
    }
    Write-WhisperXRuntimeState -RepoPath $repo -State ([ordered]@{
        overall = "READY"
        runtime = $GpuMode
        docker = "READY"
        hostGpuWorker = if ($GpuMode -eq "host") { "READY" } else { "OPTIONAL" }
        recorder = if ($SkipRecorder) { "OPTIONAL" } else { "READY" }
        desktop = if ($SkipDesktop) { "OPTIONAL" } else { "READY" }
        watchdog = if ($StartWatchdog -and $GpuMode -eq "host") { "READY" } else { "OPTIONAL" }
        qwen = "DISABLED"
    })
    Write-Host "WhisperX runtime is ready. GPU mode: $GpuMode" -ForegroundColor Green
}
finally { Pop-Location }

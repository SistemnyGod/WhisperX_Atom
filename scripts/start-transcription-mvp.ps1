[CmdletBinding()]
param(
    [switch]$SkipDesktop,
    [switch]$SkipRecorder
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$envFile = Join-Path $repo ".env"
if (-not (Test-Path -LiteralPath $envFile)) { throw ".env не найден: $envFile" }

Push-Location $repo
try {
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
            $key = $Matches[1]
            $value = $Matches[2].Trim().Trim('"')
            if ([string]::IsNullOrWhiteSpace((Get-Item -Path "Env:$key" -ErrorAction SilentlyContinue).Value)) {
                Set-Item -Path "Env:$key" -Value $value
            }
        }
    }

    # Transcript MVP is intentionally transcript-only even if a developer's .env
    # was previously used for the Qwen/summary cycle.
    $env:AUTO_SUMMARY_ENABLED = "false"
    $env:DIARIZATION_MODE = "preferred"

    foreach ($required in @("POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN")) {
        if ([string]::IsNullOrWhiteSpace((Get-Item -Path "Env:$required" -ErrorAction SilentlyContinue).Value)) {
            throw "Не задан обязательный параметр $required"
        }
    }

    docker info | Out-Null
    docker compose version | Out-Null
    if (Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue) { nvidia-smi | Out-Null }
    if (-not (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)) { throw "FFmpeg не найден в PATH" }

    $dataRoot = if ($env:WHISPERX_DATA_HOST) { $env:WHISPERX_DATA_HOST } else { "C:\WhisperXAtom\Data" }
    $inboxRoot = if ($env:WHISPERX_INBOX_HOST) { $env:WHISPERX_INBOX_HOST } else { "C:\WhisperXAtom\Inbox" }
    $archiveRoot = if ($env:WHISPERX_ARCHIVE_HOST) { $env:WHISPERX_ARCHIVE_HOST } else { "C:\WhisperXAtom\Archive" }
    foreach ($path in @($dataRoot, $inboxRoot, $archiveRoot)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }

    # Keep this runtime transcript-only even when a previous development session
    # left Qwen or browser diagnostics running.
    & docker compose --env-file $envFile -f compose.dev.yml `
        --profile llm --profile llm-diagnostic `
        stop summary-worker llama-server | Out-Null

    $compose = @("compose", "--env-file", $envFile, "-f", "compose.dev.yml", "--profile", "core", "--profile", "gpu", "up", "-d", "--build")
    & docker @compose
    if ($LASTEXITCODE -ne 0) { throw "Не удалось запустить transcript-only Compose" }

    & (Join-Path $PSScriptRoot "doctor-transcription-mvp.ps1") -SkipRegistry
    if ($LASTEXITCODE -ne 0) { throw "Transcript MVP не прошёл диагностику" }

    if (-not $SkipRecorder) {
        $service = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne "Running") {
            try { Start-Service -Name "WhisperXAtomRecorder" } catch { Write-Warning "Recorder Service не запущен автоматически: $($_.Exception.Message)" }
        }
    }
    if (-not $SkipDesktop) {
        & (Join-Path $PSScriptRoot "launch-desktop.ps1")
    }
    Write-Host "Transcript MVP запущен: API, media/GPU pipeline и локальный Recorder готовы." -ForegroundColor Green
}
finally { Pop-Location }

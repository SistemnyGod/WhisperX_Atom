[CmdletBinding()]
param([switch]$StopRecorder)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repo
try {
    & docker compose --env-file (Join-Path $repo ".env") -f compose.dev.yml --profile core --profile gpu stop
    & docker compose --env-file (Join-Path $repo ".env") -f compose.dev.yml `
        --profile llm --profile llm-diagnostic `
        stop summary-worker llama-server | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не удалось остановить transcript-only Compose" }
    if ($StopRecorder) {
        try { Stop-Service -Name "WhisperXAtomRecorder" -ErrorAction Stop } catch { Write-Warning "Recorder Service не остановлен: $($_.Exception.Message)" }
    }
    Write-Host "Transcript MVP остановлен. Volumes, spool и архив сохранены." -ForegroundColor Green
}
finally { Pop-Location }

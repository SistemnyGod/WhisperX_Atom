[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AudioPath,
    [string]$InboxPath,
    [int]$Runs = 5,
    [int]$TimeoutSeconds = 3600
)

$ErrorActionPreference = "Stop"
$env:AUTO_SUMMARY_ENABLED = "false"
$env:DIARIZATION_MODE = "preferred"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runRoot = Join-Path $repo "artifacts\transcription-mvp\$(Get-Date -Format yyyyMMdd-HHmmss)"
New-Item -ItemType Directory -Force -Path (Join-Path $runRoot "logs") | Out-Null
if (-not $InboxPath -and -not (Test-Path -LiteralPath $AudioPath)) { throw "Audio не найден: $AudioPath" }

& (Join-Path $PSScriptRoot "doctor-transcription-mvp.ps1") | Set-Content -Encoding utf8 -LiteralPath (Join-Path $runRoot "doctor-output.json")
if ($LASTEXITCODE -ne 0) { throw "Doctor не прошёл" }

for ($i=1; $i -le $Runs; $i++) {
    $log = Join-Path $runRoot "logs\run-$i.log"
    $params = @{ TimeoutSeconds=$TimeoutSeconds; StartCore=$false; WithGpu=$true; WithLlm=$false; WaitForGpu=$true; RestartWorkers=$false }
    if ($InboxPath) { $params.InboxPath = $InboxPath } else { $params.AudioPath = $AudioPath }
    & (Join-Path $PSScriptRoot "e2e-core.ps1") @params *>&1 | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) { throw "E2E запуск $i завершился ошибкой" }
}
Write-Host "Transcript E2E пройден: $Runs запусков. Артефакты: $runRoot" -ForegroundColor Green

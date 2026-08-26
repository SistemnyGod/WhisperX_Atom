[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AudioPath,
    [string]$InboxPath,
    [int]$Runs = 5,
    [int]$TimeoutSeconds = 3600,
    [switch]$WithSummary
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runtimeModule = Join-Path $PSScriptRoot "WhisperX.Runtime.ps1"
. $runtimeModule
Set-WhisperXRuntimeEnvironment -RepoPath $repo
$env:AUTO_SUMMARY_ENABLED = "false"
if ($WithSummary) { $env:AUTO_SUMMARY_ENABLED = "true" }
$env:DIARIZATION_MODE = "preferred"
$runRoot = Join-Path $repo "artifacts\transcription-mvp\$(Get-Date -Format yyyyMMdd-HHmmss)"
New-Item -ItemType Directory -Force -Path (Join-Path $runRoot "logs") | Out-Null
if (-not $InboxPath -and -not (Test-Path -LiteralPath $AudioPath)) {
    throw "Audio file was not found: $AudioPath"
}

& (Join-Path $PSScriptRoot "doctor-transcription-mvp.ps1") -SkipRegistry -ExpectSummary:$WithSummary |
    Set-Content -Encoding utf8 -LiteralPath (Join-Path $runRoot "doctor-output.json")
if ($LASTEXITCODE -ne 0) { throw "Doctor check failed." }

for ($i=1; $i -le $Runs; $i++) {
    $log = Join-Path $runRoot "logs\run-$i.log"
    $params = @{
        TimeoutSeconds = $TimeoutSeconds
        StartCore = $false
        WithGpu = $true
        WithLlm = $WithSummary
        WaitForGpu = $true
        RestartWorkers = $false
        ResultPath = (Join-Path $runRoot "runs\run-$i.json")
    }
    if ($InboxPath) { $params.InboxPath = $InboxPath } else { $params.AudioPath = $AudioPath }
    & (Join-Path $PSScriptRoot "e2e-core.ps1") @params *>&1 | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) { throw "E2E run $i failed." }
}

Write-Host "Transcript E2E passed: $Runs run(s). Artifacts: $runRoot" -ForegroundColor Green

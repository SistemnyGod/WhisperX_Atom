[CmdletBinding()]
param(
    [string]$EnvFile = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectName = "whisperx-atom"
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repo ".env.lan" }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "ENV_REQUIRED: $EnvFile is missing." }
if (-not (Get-Command docker.exe -ErrorAction SilentlyContinue)) { throw "DOCKER_UNAVAILABLE: docker.exe was not found." }

Push-Location $repo
try {
    $compose = @("compose", "--project-name", $projectName, "--env-file", $EnvFile, "-f", "compose.dev.yml", "-f", "compose.lan.yml", "--profile", "core", "--profile", "gpu", "--profile", "llm", "--profile", "lan", "stop")
    & docker @compose
    if ($LASTEXITCODE -ne 0) { throw "LAN_STOP_FAILED" }
    Write-Host "WhisperX Atom LAN server stopped. Containers, volumes, queue and archive were preserved." -ForegroundColor Green
}
finally { Pop-Location }

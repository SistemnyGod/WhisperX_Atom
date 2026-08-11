[CmdletBinding()]
param([switch]$SkipRegistry)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repo ".env"
if (Test-Path -LiteralPath $envFile) {
  foreach ($line in Get-Content -LiteralPath $envFile) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
      $key = $Matches[1]
      $value = $Matches[2].Trim().Trim('"')
      if ([string]::IsNullOrEmpty((Get-Item -Path "Env:$key" -ErrorAction SilentlyContinue).Value)) {
        Set-Item -Path "Env:$key" -Value $value
      }
    }
  }
}
$failures = @()
function Check($name, [scriptblock]$action) { try { & $action; Write-Host "[OK] $name" -ForegroundColor Green } catch { $script:failures += $name; Write-Host "[FAIL] $name`: $($_.Exception.Message)" -ForegroundColor Red } }
Check "Docker Engine" { docker info | Out-Null }
Check "Docker Compose" { docker compose version | Out-Null }
Check "WSL2" { $status = wsl --status 2>&1; if ($LASTEXITCODE -ne 0) { throw "WSL2 is unavailable" } }
Check "NVIDIA driver" { nvidia-smi | Out-Null }
Check "NVIDIA Container Toolkit" { $runtimes = docker info --format '{{json .Runtimes}}'; if ($runtimes -notmatch 'nvidia') { throw 'nvidia runtime is not registered' } }
if (-not $SkipRegistry) { Check "Container registry" { docker manifest inspect nats:2.11-alpine | Out-Null } }
Check "HF_TOKEN" { if ([string]::IsNullOrWhiteSpace($env:HF_TOKEN)) { throw "HF_TOKEN is not set" } }
$root = if ($env:WHISPERX_DATA_HOST) { $env:WHISPERX_DATA_HOST } else { "C:\WhisperXAtom\Data" }
$inbox = if ($env:WHISPERX_INBOX_HOST) { $env:WHISPERX_INBOX_HOST } else { "C:\WhisperXAtom\Inbox" }
$archive = if ($env:WHISPERX_ARCHIVE_HOST) { $env:WHISPERX_ARCHIVE_HOST } else { "C:\WhisperXAtom\Archive" }
foreach ($path in @($root,$inbox,$archive)) { Check "Storage $path" { New-Item -ItemType Directory -Force -Path $path | Out-Null; $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($path).TrimEnd(':','\')); if ($drive.Free -lt 20GB) { throw "less than 20 GiB free" } } }
if ($failures.Count) { Write-Host "Preflight failed: $($failures -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host "Preflight passed. Run: docker compose --env-file .env -f compose.dev.yml --profile core --profile gpu --profile llm up -d --build" -ForegroundColor Green

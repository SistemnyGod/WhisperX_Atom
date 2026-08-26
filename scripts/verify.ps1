[CmdletBinding()]
param(
  [switch]$SkipDocker,
  [switch]$SkipWeb,
  [switch]$SkipGpu
)
$ErrorActionPreference = "Continue"
# Native test runners (notably unittest -v) may write normal diagnostics to stderr.
# Use exit codes for pass/fail and do not promote native stderr to a PowerShell error.
if ($null -ne (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
  $PSNativeCommandUseErrorActionPreference = $false
}
$repo = Split-Path -Parent $PSScriptRoot
function Invoke-Check([string]$Name, [scriptblock]$Action) {
  Write-Host "[RUN] $Name" -ForegroundColor Cyan
  & $Action
  if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
  Write-Host "[OK]  $Name" -ForegroundColor Green
}
Set-Location $repo
$savedPythonPath = $env:PYTHONPATH
$env:PYTHONPATH = if ([string]::IsNullOrWhiteSpace($savedPythonPath)) { $repo } else { "$repo;$savedPythonPath" }
Invoke-Check "API build" { dotnet build apps/server/WhisperX.Atom.Api/WhisperX.Atom.Api.csproj --nologo }
Invoke-Check "Python compile" { py -3.12 -m compileall -q whisperx_atom workers }
Invoke-Check "Python test runtime" {
  & py -3.12 -c "import pytest" 2>$null
  if ($LASTEXITCODE -ne 0) {
    throw "PYTHON_TEST_RUNTIME_MISSING: install requirements.test.lock.txt for Python 3.12"
  }
}
Invoke-Check "Python tests" { py -3.12 -m pytest -q }
Invoke-Check "PowerShell E2E syntax" { [scriptblock]::Create((Get-Content scripts/e2e-core.ps1 -Raw)) | Out-Null }
if (-not $SkipWeb) {
  Push-Location apps/web
  try {
    Invoke-Check "Web lock install" { npm ci --ignore-scripts }
    Invoke-Check "Web build" { npm run build }
  } finally { Pop-Location }
}
if (-not $SkipDocker) {
  Invoke-Check "Compose config" { docker compose -f compose.dev.yml --profile core config --quiet }
}
if (-not $SkipGpu) {
  Invoke-Check "GPU runtime" { docker run --rm --gpus all nvidia/cuda:12.8.1-base-ubuntu24.04 nvidia-smi }
}
Write-Host "All selected checks passed." -ForegroundColor Green

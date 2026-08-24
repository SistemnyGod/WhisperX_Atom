[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server"
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
$maintenanceScript = Join-Path $bundle "enter-server-maintenance.ps1"
if (-not (Test-Path -LiteralPath $maintenanceScript -PathType Leaf)) { throw "SERVER_MAINTENANCE_SCRIPT_MISSING" }
# Set the marker before stopping containers.  This closes the race where the
# Supervisor observes a missing required service and immediately recreates it
# while the operator is still performing a deliberate manual shutdown.
& (Get-Command powershell.exe).Source -NoProfile -ExecutionPolicy Bypass -File $maintenanceScript -ConfigRoot $config -Reason "MANUAL_SERVER_STOP"
if ($LASTEXITCODE -ne 0) { throw "SERVER_MAINTENANCE_ENABLE_FAILED" }
$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',(Join-Path $bundle 'compose.dev.yml'),'-f',(Join-Path $bundle 'compose.lan.yml'),'-f',(Join-Path $bundle 'compose.release.yml'))
$profiles = @('--profile','core','--profile','gpu','--profile','llm','--profile','lan')
& docker @compose @profiles stop
if ($LASTEXITCODE -ne 0) { throw "SERVER_STOP_FAILED" }
Write-Host "SERVER_RUNTIME_STOPPED=true"
Write-Host "SERVER_RUNTIME_RESTART_SUPPRESSED=true"

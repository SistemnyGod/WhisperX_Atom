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
$manifestPath = Join-Path $bundle "release-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "SERVER_RELEASE_MANIFEST_MISSING" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$releaseTag = [string]$manifest.releaseTag
$buildIdentity = [string]$manifest.buildIdentity
if ([string]::IsNullOrWhiteSpace($releaseTag) -or $releaseTag -match 'dev|dirty|latest') { throw "SERVER_RELEASE_TAG_INVALID" }
if ([string]::IsNullOrWhiteSpace($buildIdentity) -or $buildIdentity -match 'dev|dirty') { throw "SERVER_RELEASE_IDENTITY_INVALID" }
$env:WHISPERX_RELEASE_TAG = $releaseTag
$env:WHISPERX_RELEASE_VERSION = $buildIdentity
$env:WHISPERX_BUILD_IDENTITY = $buildIdentity
$env:WHISPERX_REVISION = [string]$manifest.commit
$env:APP_VERSION = $buildIdentity
$maintenanceScript = Join-Path $bundle "enter-server-maintenance.ps1"
if (-not (Test-Path -LiteralPath $maintenanceScript -PathType Leaf)) { throw "SERVER_MAINTENANCE_SCRIPT_MISSING" }
# Set the marker before stopping containers.  This closes the race where the
# Supervisor observes a missing required service and immediately recreates it
# while the operator is still performing a deliberate manual shutdown.
& (Get-Command powershell.exe).Source -NoProfile -ExecutionPolicy Bypass -File $maintenanceScript -ConfigRoot $config -Reason "MANUAL_SERVER_STOP"
if ($LASTEXITCODE -ne 0) { throw "SERVER_MAINTENANCE_ENABLE_FAILED" }
$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',(Join-Path $bundle 'compose.dev.yml'),'-f',(Join-Path $bundle 'compose.lan.yml'),'-f',(Join-Path $bundle 'compose.release.yml'))
$profiles = @('--profile','core','--profile','gpu','--profile','llm','--profile','memory','--profile','lan')
& docker @compose @profiles stop
if ($LASTEXITCODE -ne 0) { throw "SERVER_STOP_FAILED" }
Write-Host "SERVER_RUNTIME_STOPPED=true"
Write-Host "SERVER_RUNTIME_RESTART_SUPPRESSED=true"

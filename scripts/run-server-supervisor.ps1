[CmdletBinding()]
param(
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server"
)

$ErrorActionPreference = "Stop"
$config = [IO.Path]::GetFullPath($ConfigRoot)
$pointer = Join-Path $config "active-bundle.path"
if (-not (Test-Path -LiteralPath $pointer -PathType Leaf)) { throw "SERVER_ACTIVE_BUNDLE_POINTER_MISSING" }

$bundleValue = (Get-Content -LiteralPath $pointer -Raw -Encoding utf8).Trim()
if ([string]::IsNullOrWhiteSpace($bundleValue)) { throw "SERVER_ACTIVE_BUNDLE_POINTER_INVALID" }
$bundle = [IO.Path]::GetFullPath($bundleValue)
$manifestPath = Join-Path $bundle "release-manifest.json"
$supervisor = Join-Path $bundle "supervise-server-runtime.ps1"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "SERVER_ACTIVE_BUNDLE_MANIFEST_MISSING" }
if (-not (Test-Path -LiteralPath $supervisor -PathType Leaf)) { throw "SERVER_ACTIVE_BUNDLE_SUPERVISOR_MISSING" }

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
$identity = [string]$manifest.buildIdentity
if ([string]::IsNullOrWhiteSpace($identity) -or $identity -match '(?i)dev|dirty') { throw "SERVER_ACTIVE_BUNDLE_IDENTITY_INVALID" }

& (Get-Command powershell.exe -ErrorAction Stop).Source -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File $supervisor -BundleRoot $bundle -ConfigRoot $config
exit $LASTEXITCODE

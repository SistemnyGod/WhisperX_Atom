[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
$manifestPath = Join-Path $bundle "release-manifest.json"
$composeFiles = @(
    (Join-Path $bundle "compose.dev.yml"),
    (Join-Path $bundle "compose.lan.yml"),
    (Join-Path $bundle "compose.release.yml")
)
foreach ($required in @($envFile, $manifestPath) + $composeFiles) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "SERVER_RUNTIME_FILE_MISSING: $required" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$identity = [string]$manifest.buildIdentity
$tag = [string]$manifest.releaseTag
if ([string]::IsNullOrWhiteSpace($identity) -or $identity -match '(?i)dev|dirty' -or
    [string]::IsNullOrWhiteSpace($tag) -or $tag -match '(?i)dev|dirty|latest') {
    throw "SERVER_RELEASE_IDENTITY_INVALID"
}

$env:COMPOSE_PROJECT_NAME = 'whisperx-atom'
$env:WHISPERX_RELEASE_TAG = $tag
$env:WHISPERX_RELEASE_VERSION = $identity
$env:WHISPERX_BUILD_IDENTITY = $identity
$env:WHISPERX_REVISION = [string]$manifest.commit
$env:APP_VERSION = $identity

$mode = if ($Apply) { "--apply" } else { "--preview" }
$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile)
foreach ($composeFile in $composeFiles) { $compose += @('-f', $composeFile) }
$profiles = @('--profile','core','--profile','gpu','--profile','lan')
& docker @compose @profiles run --rm --no-deps gpu-worker python -m workers.ml_worker.recovery $mode
if ($LASTEXITCODE -ne 0) { throw "GPU_RUNTIME_RECOVERY_FAILED" }

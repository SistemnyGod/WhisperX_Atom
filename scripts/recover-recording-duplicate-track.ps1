[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [Parameter(Mandatory=$true)][string[]]$SessionId,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
$manifestPath = Join-Path $bundle "release-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "SERVER_RELEASE_MANIFEST_MISSING" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$tag = [string]$manifest.releaseTag
if ([string]::IsNullOrWhiteSpace($tag) -or $tag -match 'dev|dirty|latest') { throw "SERVER_RELEASE_TAG_INVALID" }
$env:WHISPERX_RELEASE_TAG = $tag
$env:WHISPERX_BUILD_IDENTITY = [string]$manifest.buildIdentity
$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',(Join-Path $bundle 'compose.dev.yml'),'-f',(Join-Path $bundle 'compose.lan.yml'),'-f',(Join-Path $bundle 'compose.release.yml'), '--profile','core','--profile','gpu','--profile','lan')
$mode = if ($Apply) { '--apply' } else { '--preview' }
$args = @('run','--rm','--no-deps','gpu-worker','python','-m','workers.ml_worker.recording_recovery',$mode)
foreach ($id in $SessionId) { $args += @('--session-id',$id) }
& docker @compose @args
if ($LASTEXITCODE -ne 0) { throw 'RECORDING_DUPLICATE_TRACK_RECOVERY_FAILED' }

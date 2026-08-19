[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server"
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
$manifestPath = Join-Path $bundle "release-manifest.json"
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "SERVER_RELEASE_MANIFEST_MISSING" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.buildIdentity -match 'dev|dirty') { throw "SERVER_RELEASE_IDENTITY_INVALID" }
$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',(Join-Path $bundle 'compose.dev.yml'),'-f',(Join-Path $bundle 'compose.lan.yml'),'-f',(Join-Path $bundle 'compose.release.yml'))
$profiles = @('--profile','core','--profile','gpu','--profile','llm','--profile','lan')
$configText = (& docker @compose @profiles config | Out-String)
if ($LASTEXITCODE -ne 0) { throw "SERVER_RELEASE_COMPOSE_INVALID" }
if ($configText -match '(?im)image:\s*[^\r\n]*:(dev|latest)\b') { throw "SERVER_RELEASE_COMPOSE_UNPINNED_IMAGE" }
docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw "DOCKER_ENGINE_UNAVAILABLE" }
$states = (& docker @compose @profiles ps --format '{{.Service}} {{.State}} {{.Health}}' | Out-String).Trim()
$health = [ordered]@{
    buildIdentity = [string]$manifest.buildIdentity
    compose = 'VALID'
    docker = 'READY'
    services = $states
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$health | ConvertTo-Json -Depth 8

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
foreach ($required in @($envFile, $manifestPath, (Join-Path $bundle "compose.dev.yml"), (Join-Path $bundle "compose.lan.yml"), (Join-Path $bundle "compose.release.yml"))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "SERVER_RUNTIME_FILE_MISSING: $required" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$identity = [string]$manifest.buildIdentity
$tag = [string]$manifest.releaseTag
if ([string]::IsNullOrWhiteSpace($identity) -or $identity -match '(?i)dev|dirty' -or [string]::IsNullOrWhiteSpace($tag) -or $tag -match '(?i)dev|dirty|latest') {
    throw "SERVER_RELEASE_IDENTITY_INVALID"
}

function Read-EnvValue([string]$name) {
    $line = Get-Content -LiteralPath $envFile -Encoding utf8 | Where-Object { $_ -match "^$name=" } | Select-Object -First 1
    if ($null -eq $line) { return $null }
    return ($line -replace "^$name=", '').Trim()
}

function Get-DockerImageMetadata([string]$image) {
    $raw = (& docker image inspect $image | Out-String)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) { throw "SERVER_IMAGE_INSPECT_FAILED: $image" }
    $record = @($raw | ConvertFrom-Json)[0]
    if ($null -eq $record) { throw "SERVER_IMAGE_INSPECT_EMPTY: $image" }
    return $record
}

$auto = Read-EnvValue 'AUTO_SUMMARY_ENABLED'
$assistant = Read-EnvValue 'ASSISTANT_ENABLED'
$llmEnabled = $auto -eq 'true' -or $assistant -ne 'false'
$env:COMPOSE_PROJECT_NAME = 'whisperx-atom'
$env:WHISPERX_RELEASE_TAG = $tag
$env:WHISPERX_RELEASE_VERSION = $identity
$env:WHISPERX_BUILD_IDENTITY = $identity
$env:WHISPERX_REVISION = [string]$manifest.commit
$env:APP_VERSION = $identity

docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'DOCKER_ENGINE_UNAVAILABLE' }
foreach ($property in $manifest.images.PSObject.Properties) {
    $image = [string]$property.Value.reference
    $metadata = Get-DockerImageMetadata $image
    $actualIdentity = [string]$metadata.Config.Labels.'io.whisperx.atom.build-identity'
    if ($actualIdentity -ne $identity) { throw "SERVER_IMAGE_IDENTITY_LABEL_MISMATCH: $image" }
}

$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',(Join-Path $bundle 'compose.dev.yml'),'-f',(Join-Path $bundle 'compose.lan.yml'),'-f',(Join-Path $bundle 'compose.release.yml'))
$profiles = @('--profile','core','--profile','gpu','--profile','lan')
if ($llmEnabled) { $profiles += @('--profile','llm') }
$configText = (& docker @compose @profiles config | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'SERVER_RUNTIME_COMPOSE_INVALID' }
if ($configText -match '(?im)image:\s*[^\r\n]*:(dev|latest)\b') { throw 'SERVER_RUNTIME_COMPOSE_UNPINNED_IMAGE' }

& docker @compose @profiles up -d --pull never
if ($LASTEXITCODE -ne 0) { throw 'SERVER_RUNTIME_START_FAILED' }
Write-Host "SERVER_RUNTIME_STARTED=true"
Write-Host "BUILD_IDENTITY=$identity"

[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [switch]$EnableQwen,
    [switch]$EnableAssistant,
    [switch]$SkipBackup
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
$manifestPath = Join-Path $bundle "release-manifest.json"
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "SERVER_RELEASE_MANIFEST_MISSING" }
if (-not (Test-Path -LiteralPath (Join-Path $bundle "compose.release.yml") -PathType Leaf)) { throw "SERVER_RELEASE_COMPOSE_MISSING" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.buildIdentity -match 'dev|dirty' -or $manifest.releaseTag -match 'dev|dirty') { throw "SERVER_RELEASE_IDENTITY_INVALID" }
$tag = [string]$manifest.releaseTag
$identity = [string]$manifest.buildIdentity

function Read-EnvValue([string]$name) {
    $line = Get-Content -LiteralPath $envFile -Encoding utf8 | Where-Object { $_ -match "^$name=" } | Select-Object -First 1
    if ($null -eq $line) { return $null }
    return ($line -replace "^$name=", '').Trim()
}
if ($EnableQwen) { $env:AUTO_SUMMARY_ENABLED = 'true' }
if ($EnableAssistant) { $env:ASSISTANT_ENABLED = 'true' }
$auto = Read-EnvValue 'AUTO_SUMMARY_ENABLED'
$assistant = Read-EnvValue 'ASSISTANT_ENABLED'
if ($auto -eq 'true') { $EnableQwen = $true }
if ($assistant -ne 'false') { $EnableAssistant = $true }
$env:WHISPERX_RELEASE_TAG = $tag
$env:WHISPERX_RELEASE_VERSION = $identity
$env:WHISPERX_BUILD_IDENTITY = $identity
$env:WHISPERX_REVISION = [string]$manifest.commit
$env:APP_VERSION = $identity

docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'DOCKER_ENGINE_UNAVAILABLE' }
$imagesTar = Join-Path $bundle 'docker-images.tar'
if (-not (Test-Path -LiteralPath $imagesTar -PathType Leaf)) { throw 'SERVER_IMAGES_TAR_MISSING' }
& docker load --input $imagesTar
if ($LASTEXITCODE -ne 0) { throw 'SERVER_IMAGES_LOAD_FAILED' }
foreach ($property in $manifest.images.PSObject.Properties) {
    $image = [string]$property.Value.reference
    $actualId = (docker image inspect $image --format '{{.Id}}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualId -ne [string]$property.Value.imageId) { throw "SERVER_IMAGE_ID_MISMATCH: $image" }
    $actualIdentity = (docker image inspect $image --format '{{index .Config.Labels "io.whisperx.atom.build-identity"}}' | Out-String).Trim()
    if ($actualIdentity -ne $identity) { throw "SERVER_IMAGE_IDENTITY_LABEL_MISMATCH: $image" }
}
foreach ($property in $manifest.infrastructureImages.PSObject.Properties) {
    $image = [string]$property.Value.reference
    $actualId = (docker image inspect $image --format '{{.Id}}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualId -ne [string]$property.Value.imageId) { throw "SERVER_INFRA_IMAGE_ID_MISMATCH: $image" }
}

New-Item -ItemType Directory -Force -Path (Join-Path $config 'backups') | Out-Null
if (-not $SkipBackup) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDir = Join-Path $config "backups\$stamp"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $env:COMPOSE_PROJECT_NAME = 'whisperx-atom'
    foreach ($name in @('POSTGRES_DB','POSTGRES_USER','POSTGRES_PASSWORD')) {
        $value = Read-EnvValue $name
        if ($value) { [Environment]::SetEnvironmentVariable($name, $value, 'Process') }
    }
    & (Get-Command powershell.exe).Source -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bundle 'backup.ps1') -OutputDirectory $backupDir -ComposeFile (Join-Path $bundle 'compose.dev.yml') -MigrationRoot (Join-Path $bundle 'migrations')
    if ($LASTEXITCODE -ne 0) { throw 'SERVER_POSTGRES_BACKUP_FAILED' }
}

$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',(Join-Path $bundle 'compose.dev.yml'),'-f',(Join-Path $bundle 'compose.lan.yml'),'-f',(Join-Path $bundle 'compose.release.yml'))
$profiles = @('--profile','core','--profile','gpu','--profile','lan')
if ($EnableQwen) { $profiles += @('--profile','llm') }
$configText = (& docker @compose @profiles config | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'SERVER_RELEASE_COMPOSE_INVALID' }
if ($configText -match '(?im)image:\s*[^\r\n]*:(dev|latest)\b') { throw 'SERVER_RELEASE_COMPOSE_UNPINNED_IMAGE' }
$statePath = Join-Path $config 'pre-update-state.json'
$previousStateJson = (& docker @compose @profiles ps --format json | Out-String)
$previousStateJson | Set-Content -LiteralPath $statePath -Encoding utf8
$previousServices = @()
foreach ($line in ($previousStateJson -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
    try { $previousServices += @($line | ConvertFrom-Json) } catch { }
}

# Run the new image's additive migrations as a one-shot job before replacing
# the API. No `down -v` or volume recreation is ever issued.
& docker @compose @profiles run --rm --no-deps -e WHISPERX_MIGRATION_ONLY=true api
if ($LASTEXITCODE -ne 0) { throw 'SERVER_MIGRATION_JOB_FAILED' }
try {
    & docker @compose @profiles up -d --pull never
    if ($LASTEXITCODE -ne 0) { throw 'SERVER_RELEASE_START_FAILED' }
    $rollbackPath = Join-Path $config 'rollback-compose.yml'
    if (Test-Path -LiteralPath $rollbackPath) { Remove-Item -LiteralPath $rollbackPath -Force }
} catch {
    # Keep the previous image references available for an automatic rollback
    # after a post-migration start failure.  This never removes volumes or
    # data; if Docker removed an old container, the explicit report remains for
    # operator recovery instead of guessing at a replacement image.
    $rollbackPath = Join-Path $config 'rollback-compose.yml'
    $rollbackLines = [System.Collections.Generic.List[string]]::new()
    $rollbackLines.Add('services:')
    foreach ($service in $previousServices) {
        $name = [string]$service.Service
        $image = [string]$service.Image
        if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($image)) { continue }
        $rollbackLines.Add("  ${name}:")
        $rollbackLines.Add("    image: $image")
        $rollbackLines.Add('    build: !reset null')
    }
    if ($rollbackLines.Count -gt 1) {
        $rollbackLines -join "`r`n" | Set-Content -LiteralPath $rollbackPath -Encoding utf8
        $rollbackCompose = $compose + @('-f',$rollbackPath)
        & docker @rollbackCompose @profiles up -d --pull never
        if ($LASTEXITCODE -eq 0) { Write-Warning 'SERVER_RELEASE_ROLLED_BACK=true' }
        else { Write-Warning 'SERVER_RELEASE_ROLLBACK_FAILED=true' }
    } else {
        Write-Warning 'SERVER_RELEASE_ROLLBACK_UNAVAILABLE=true'
    }
    throw
}
try {
    & (Get-Command powershell.exe).Source -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bundle 'prune-stale-worker-heartbeats.ps1') -BundleRoot $bundle -ConfigRoot $config
    if ($LASTEXITCODE -ne 0) { Write-Warning 'HEARTBEAT_PRUNE_FAILED' }
} catch {
    Write-Warning "HEARTBEAT_PRUNE_SKIPPED: $($_.Exception.Message)"
}
Write-Host "SERVER_RUNTIME_STARTED=true"
Write-Host "BUILD_IDENTITY=$identity"

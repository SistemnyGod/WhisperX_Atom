[CmdletBinding()]
param(
    [string]$OutputRoot = "artifacts/server-bundle",
    [string]$ArchivePath = "artifacts/WhisperXAtom-Server.zip",
    [string]$EnvFile = ".env.lan",
    [switch]$IncludeLlm,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repo

function Invoke-Git([string[]]$Arguments) {
    $value = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GIT_FAILED: $($Arguments -join ' ')" }
    return ($value | Out-String).Trim()
}

# Generated and ignored runtime directories can be inaccessible to the Windows
# shell account used for release work.  Do not turn that into a false "dirty"
# result, but fail closed if Git itself cannot determine the worktree status.
$status = @(cmd.exe /d /s /c "git -C `"$repo`" status --porcelain --untracked-files=normal 2>NUL")
if ($LASTEXITCODE -ne 0) {
    throw "GIT_STATUS_FAILED: cannot determine release cleanliness"
}
if ($status.Count -gt 0) {
    throw "RELEASE_REQUIRES_CLEAN_COMMIT: $($status.Count) changed paths"
}
$commit = Invoke-Git @('rev-parse','HEAD')
$short = Invoke-Git @('rev-parse','--short=12','HEAD')
$identity = "1.0.1+$commit"
$tag = "1.0.1-$short"
if ($identity -match 'dirty|dev' -or $tag -match 'dirty|dev') { throw "RELEASE_IDENTITY_INVALID: $identity" }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "ENV_FILE_NOT_FOUND: $EnvFile" }
$autoSummarySetting = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match '^AUTO_SUMMARY_ENABLED=' } | Select-Object -First 1
if ($autoSummarySetting -and ($autoSummarySetting -replace '^AUTO_SUMMARY_ENABLED=','').Trim() -eq 'true') { $IncludeLlm = $true }
$assistantSetting = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match '^ASSISTANT_ENABLED=' } | Select-Object -First 1
if ($assistantSetting -and ($assistantSetting -replace '^ASSISTANT_ENABLED=','').Trim() -eq 'true') { $IncludeLlm = $true }
$memorySetting = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match '^MEETING_MEMORY_ENABLED=' } | Select-Object -First 1
$includeMemory = -not ($memorySetting -and ($memorySetting -replace '^MEETING_MEMORY_ENABLED=','').Trim().ToLowerInvariant() -eq 'false')
$embeddingSnapshotManifest = $null
if ($IncludeLlm) {
    $embeddingRequired = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match '^ASSISTANT_EMBEDDING_REQUIRE_VERIFIED=' } | Select-Object -First 1
    $embeddingModelHash = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match '^ASSISTANT_EMBEDDING_ONNX_SHA256=' } | Select-Object -First 1
    $embeddingTokenizerHash = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match '^ASSISTANT_EMBEDDING_TOKENIZER_SHA256=' } | Select-Object -First 1
    $embeddingRevisionLine = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match '^ASSISTANT_EMBEDDING_MODEL_REVISION=' } | Select-Object -First 1
    $modelsHostLine = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match '^WHISPERX_MODELS_HOST=' } | Select-Object -First 1
    $requiredValue = if ($embeddingRequired) { ($embeddingRequired -replace '^ASSISTANT_EMBEDDING_REQUIRE_VERIFIED=','').Trim() } else { '' }
    $modelHashValue = if ($embeddingModelHash) { ($embeddingModelHash -replace '^ASSISTANT_EMBEDDING_ONNX_SHA256=','').Trim() } else { '' }
    $tokenizerHashValue = if ($embeddingTokenizerHash) { ($embeddingTokenizerHash -replace '^ASSISTANT_EMBEDDING_TOKENIZER_SHA256=','').Trim() } else { '' }
    $embeddingRevision = if ($embeddingRevisionLine) { ($embeddingRevisionLine -replace '^ASSISTANT_EMBEDDING_MODEL_REVISION=','').Trim() } else { '' }
    $modelsHost = if ($modelsHostLine) { ($modelsHostLine -replace '^WHISPERX_MODELS_HOST=','').Trim() } else { 'C:\WhisperXAtom\Models' }
    if ($requiredValue -ne 'true' -or [string]::IsNullOrWhiteSpace($modelHashValue) -or [string]::IsNullOrWhiteSpace($tokenizerHashValue)) {
        throw 'RELEASE_EMBEDDING_SNAPSHOT_NOT_PINNED: Assistant release requires verified ONNX/tokenizer SHA256 values'
    }
    if ([string]::IsNullOrWhiteSpace($embeddingRevision)) { throw 'RELEASE_EMBEDDING_REVISION_MISSING' }
    $embeddingOnnxHostPath = Join-Path $modelsHost 'embeddings\paraphrase-multilingual-MiniLM-L12-v2.onnx'
    $embeddingTokenizerHostPath = Join-Path $modelsHost 'embeddings\tokenizer.json'
    if (-not (Test-Path -LiteralPath $embeddingOnnxHostPath -PathType Leaf) -or -not (Test-Path -LiteralPath $embeddingTokenizerHostPath -PathType Leaf)) {
        throw "RELEASE_EMBEDDING_SNAPSHOT_FILES_MISSING: $embeddingOnnxHostPath / $embeddingTokenizerHostPath"
    }
    $actualOnnxHash = (Get-FileHash -LiteralPath $embeddingOnnxHostPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualTokenizerHash = (Get-FileHash -LiteralPath $embeddingTokenizerHostPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualOnnxHash -ne $modelHashValue.ToLowerInvariant() -or $actualTokenizerHash -ne $tokenizerHashValue.ToLowerInvariant()) {
        throw 'RELEASE_EMBEDDING_SNAPSHOT_SHA256_MISMATCH'
    }
    $embeddingSnapshotManifest = [ordered]@{
        model = 'sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2'
        revision = $embeddingRevision
        onnxSha256 = $actualOnnxHash
        tokenizerSha256 = $actualTokenizerHash
        device = 'CPU'
    }
}

docker version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "DOCKER_ENGINE_UNAVAILABLE" }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "DOCKER_COMPOSE_UNAVAILABLE" }

$env:WHISPERX_RELEASE_TAG = $tag
$env:WHISPERX_RELEASE_VERSION = $identity
$env:WHISPERX_BUILD_IDENTITY = $identity
$env:WHISPERX_REVISION = $commit
$env:APP_VERSION = $identity
$compose = @('--project-name','whisperx-atom','--env-file',(Join-Path $repo $EnvFile),'-f',(Join-Path $repo 'compose.dev.yml'),'-f',(Join-Path $repo 'compose.lan.yml'))
$profiles = @('--profile','core','--profile','gpu','--profile','lan')
if ($IncludeLlm) { $profiles += @('--profile','llm') }
if ($includeMemory) { $profiles += @('--profile','memory') }
$appServices = @('api','outbox-relay','import-worker','media-worker','gpu-worker')
if ($IncludeLlm) { $appServices += 'summary-worker' }
if ($includeMemory) { $appServices += 'memory-worker' }

if (-not $SkipBuild) {
    # A release tag is immutable for a commit.  Reuse a pre-existing image only
    # under that exact tag; its OCI labels are verified below before it can enter
    # the bundle.  This makes an interrupted build resumable without overwriting
    # a potentially mismatched image.
    $servicesToBuild = @()
    foreach ($service in $appServices) {
        $image = "whisperx-atom-$($service):$tag"
        # `docker image inspect` uses stderr for the expected "not found"
        # result.  Invoke through cmd so PowerShell's Stop preference does not
        # turn that probe into a terminating NativeCommandError.
        & cmd.exe /d /s /c "docker image inspect `"$image`" >NUL 2>NUL"
        if ($LASTEXITCODE -ne 0) { $servicesToBuild += $service }
    }
    if ($servicesToBuild.Count -gt 0) {
        & docker compose @compose @profiles build --pull=false @servicesToBuild
        if ($LASTEXITCODE -ne 0) { throw "RELEASE_IMAGE_BUILD_FAILED" }
    } else {
        Write-Host "RELEASE_IMAGES_ALREADY_BUILT=$tag"
    }
}

$imageRecords = [ordered]@{}
function Get-DockerImageMetadata([string]$image) {
    # Do not use a Go-template map lookup here.  Windows PowerShell's native
    # argument marshalling removes the inner quotes from `index .Config.Labels
    # "..."`, which makes the release packager fail after a successful build.
    # Docker's JSON result is stable and retains dotted OCI label names.
    $raw = (& docker image inspect $image | Out-String)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
        throw "RELEASE_IMAGE_INSPECT_FAILED: $image"
    }
    $record = @($raw | ConvertFrom-Json)[0]
    if ($null -eq $record) { throw "RELEASE_IMAGE_INSPECT_EMPTY: $image" }
    return $record
}
foreach ($service in $appServices) {
    $image = "whisperx-atom-$($service):$tag"
    $inspect = Get-DockerImageMetadata $image
    $labels = $inspect.Config.Labels
    $actualIdentity = [string]$labels.'io.whisperx.atom.build-identity'
    $actualRevision = [string]$labels.'org.opencontainers.image.revision'
    $actualVersion = [string]$labels.'org.opencontainers.image.version'
    if ([string]::IsNullOrWhiteSpace($actualIdentity) -or [string]::IsNullOrWhiteSpace($actualRevision) -or [string]::IsNullOrWhiteSpace($actualVersion)) { throw "RELEASE_IMAGE_LABELS_MISSING: $image" }
    if ($actualIdentity -ne $identity -or $actualRevision -ne $commit -or $actualVersion -ne $identity) { throw "RELEASE_IMAGE_LABELS_MISMATCH: $image" }
    $imageRecords[$service] = [ordered]@{ reference = $image; imageId = [string]$inspect.Id; buildIdentity = $actualIdentity; revision = $actualRevision }
}
$configuredImages = @(& docker compose @compose @profiles config --images | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
if ($LASTEXITCODE -ne 0 -or $configuredImages.Count -eq 0) { throw "RELEASE_COMPOSE_IMAGE_LIST_FAILED" }
$infrastructure = [ordered]@{}
foreach ($image in $configuredImages) {
    $inspect = Get-DockerImageMetadata $image
    if ($image -match ':(dev|latest)(@|$)') { throw "RELEASE_INFRA_IMAGE_UNPINNED: $image" }
    $infrastructure[$image] = [ordered]@{ reference = $image; imageId = [string]$inspect.Id }
}

$finalRoot = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
$stage = "$finalRoot.staging.$tag.$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'compose.dev.yml') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'compose.lan.yml') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'compose.release.yml') -Destination $stage
foreach ($example in @('.env.example','.env.lan.example')) {
    if (Test-Path -LiteralPath (Join-Path $repo $example)) { Copy-Item -LiteralPath (Join-Path $repo $example) -Destination $stage }
}
$caddySource = Join-Path $repo 'infrastructure\caddy'
if (Test-Path -LiteralPath $caddySource -PathType Container) {
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'infrastructure\caddy') | Out-Null
    Get-ChildItem -LiteralPath $caddySource -File | Where-Object { $_.Name -in @('Caddyfile','Caddyfile.lan') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage "infrastructure\caddy\$($_.Name)")
    }
}
$runtimeScripts = @('start-server-bundle.ps1','start-runtime.ps1','supervise-server-runtime.ps1','enter-server-maintenance.ps1','exit-server-maintenance.ps1','stop-server-bundle.ps1','doctor-server-bundle.ps1','hardware-release-acceptance.ps1','acceptance-scenarios.json','install-server-startup-task.ps1','ensure-supervisor-health-token.ps1','prune-stale-worker-heartbeats.ps1','recover-gpu-runtime.ps1','recover-recording-duplicate-track.ps1','backup.ps1','restore.ps1','e2e-backup-restore.ps1')
foreach ($script in $runtimeScripts) {
    Copy-Item -LiteralPath (Join-Path $repo "scripts\$script") -Destination $stage
}
if (Test-Path -LiteralPath (Join-Path $repo 'migrations')) {
    Copy-Item -LiteralPath (Join-Path $repo 'migrations') -Destination (Join-Path $stage 'migrations') -Recurse
} elseif (Test-Path -LiteralPath (Join-Path $repo 'apps\server\WhisperX.Atom.Api\Migrations')) {
    Copy-Item -LiteralPath (Join-Path $repo 'apps\server\WhisperX.Atom.Api\Migrations') -Destination (Join-Path $stage 'migrations') -Recurse
}
if (Test-Path -LiteralPath (Join-Path $repo 'artifacts\model-manifests')) { Copy-Item -LiteralPath (Join-Path $repo 'artifacts\model-manifests') -Destination (Join-Path $stage 'model-manifests') -Recurse }

$migrationSource = if (Test-Path -LiteralPath (Join-Path $repo 'migrations')) { Join-Path $repo 'migrations' } else { Join-Path $repo 'apps\server\WhisperX.Atom.Api\Migrations' }
$migrationManifest = @()
if (Test-Path -LiteralPath $migrationSource -PathType Container) {
    $migrationManifest = @(Get-ChildItem -LiteralPath $migrationSource -Filter '*.sql' -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}

$allImages = [System.Collections.Generic.List[string]]::new()
foreach ($reference in @($imageRecords.Values | ForEach-Object { $_.reference }) + @($infrastructure.Values | ForEach-Object { $_.reference })) {
    # `docker compose config --images` includes application images too.  Save
    # each reference once: duplicate entries needlessly double the export time
    # and can make a GPU release bundle prohibitively large.
    if (-not $allImages.Contains([string]$reference)) { $allImages.Add([string]$reference) }
}
& docker save $allImages -o (Join-Path $stage 'docker-images.tar')
if ($LASTEXITCODE -ne 0) { throw "RELEASE_DOCKER_SAVE_FAILED" }
$dockerTarHash = (Get-FileHash -LiteralPath (Join-Path $stage 'docker-images.tar') -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    schemaVersion = 1
    product = 'WhisperX Atom'
    version = '1.0.1'
    commit = $commit
    buildIdentity = $identity
    releaseTag = $tag
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    images = $imageRecords
    infrastructureImages = $infrastructure
    migrations = $migrationManifest
    embeddingSnapshot = $embeddingSnapshotManifest
    runtimeScripts = @($runtimeScripts | ForEach-Object {
        $path = Join-Path $stage $_
        [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    dockerImages = [ordered]@{ path = 'docker-images.tar'; sha256 = $dockerTarHash }
    compose = @('compose.dev.yml','compose.lan.yml','compose.release.yml')
    volumesPolicy = 'preserve'
    secretsPolicy = 'external-env-only'
}
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8
([ordered]@{ algorithm = 'SHA256'; path = 'docker-images.tar'; hash = $dockerTarHash } | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $stage 'docker-images.sha256.json') -Encoding utf8
# Bundle staging is an explicit allowlist of compose files, scripts,
# migrations and image/model manifests. Reject human documents defensively so
# local secret containers (for example "Токен.docx") can never enter a release.
$forbiddenReleaseFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force |
    Where-Object { $_.Extension -in @('.doc','.docx','.xls','.xlsx','.ppt','.pptx') -or $_.Name -ieq 'Токен.docx' })
if ($forbiddenReleaseFiles.Count -gt 0) {
    throw ('RELEASE_FORBIDDEN_DOCUMENT: ' + (($forbiddenReleaseFiles | ForEach-Object FullName) -join ';'))
}
if (Test-Path -LiteralPath $finalRoot) { Remove-Item -LiteralPath $finalRoot -Recurse -Force }
Move-Item -LiteralPath $stage -Destination $finalRoot
$archive = [IO.Path]::GetFullPath((Join-Path $repo $ArchivePath))
$archiveParent = Split-Path -Parent $archive
New-Item -ItemType Directory -Force -Path $archiveParent | Out-Null
# The image tar includes CUDA layers and regularly exceeds the 2 GB limit of
# Compress-Archive on Windows PowerShell.  bsdtar writes a Zip64 archive while
# preserving the bundle as a portable directory layout.
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
& tar.exe -a -c -f $archive -C $finalRoot .
if ($LASTEXITCODE -ne 0) { throw "RELEASE_SERVER_ARCHIVE_CREATE_FAILED" }
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "RELEASE_SERVER_ARCHIVE_MISSING" }
Write-Host "SERVER_BUNDLE_READY=$finalRoot"
Write-Host "SERVER_BUNDLE_ARCHIVE=$archive"
Write-Host "BUILD_IDENTITY=$identity"

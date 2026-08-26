[CmdletBinding()]
param([string]$RepoPath, [string]$OutputPath, [string]$ModelManifestPath)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoPath)) { $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $RepoPath "artifacts\release\runtime-manifest.json" }
if ([string]::IsNullOrWhiteSpace($ModelManifestPath)) { $ModelManifestPath = Join-Path $RepoPath "artifacts\release\model-manifest.json" }
if (Test-Path -LiteralPath (Join-Path $RepoPath ".env") -PathType Leaf) {
    . (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
    Import-WhisperXDotEnv -RepoPath $RepoPath
}

function Invoke-Safe([scriptblock]$Action) {
    try { $value = & $Action 2>$null; if ($value) { return (($value | Out-String).Trim() -split "`r?`n")[0] } } catch { }
    return $null
}
function Get-PackageVersion([string]$Python, [string]$Package) {
    if ([string]::IsNullOrWhiteSpace($Python)) { return $null }
    $code = "import importlib.metadata as m; print(m.version('$Package'))"
    return Invoke-Safe { & $Python -c $code }
}
function Get-HfSnapshot([string]$Repository, [string]$Revision) {
    if ([string]::IsNullOrWhiteSpace($Repository)) { return $null }
    $hfHome = if ($env:HF_HOME) { $env:HF_HOME } else { Join-Path $env:USERPROFILE ".cache\huggingface" }
    $cacheName = "models--" + ($Repository -replace "/", "--")
    $root = Join-Path (Join-Path $hfHome "hub") $cacheName
    $ref = if ([string]::IsNullOrWhiteSpace($Revision)) { Join-Path $root "refs\main" } else { Join-Path $root ("refs\" + $Revision) }
    if (-not (Test-Path -LiteralPath $ref -PathType Leaf)) {
        if (-not [string]::IsNullOrWhiteSpace($Revision) -and (Test-Path -LiteralPath (Join-Path $root "refs\main") -PathType Leaf)) { $ref = Join-Path $root "refs\main" }
        else { return $null }
    }
    $resolved = (Get-Content -LiteralPath $ref -Raw).Trim()
    $snapshot = Join-Path $root ("snapshots\" + $resolved)
    if (-not (Test-Path -LiteralPath $snapshot -PathType Container)) { return $null }
    $inventory = @(Get-ChildItem -LiteralPath $snapshot -File -Recurse | ForEach-Object {
        ($_.FullName.Substring($snapshot.Length).TrimStart('\','/').Replace('\','/') + "|" + $_.Length)
    } | Sort-Object)
    $inventoryBytes = [Text.Encoding]::UTF8.GetBytes(($inventory -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $inventoryHash = ([BitConverter]::ToString($sha.ComputeHash($inventoryBytes))).Replace("-", "").ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [ordered]@{ repository = $Repository; revision = $resolved; snapshotPath = $snapshot; fileInventoryHash = $inventoryHash; fileCount = $inventory.Count }
}

$python = if ($env:WHISPERX_HOST_PYTHON -and (Test-Path -LiteralPath $env:WHISPERX_HOST_PYTHON)) { $env:WHISPERX_HOST_PYTHON } else {
    $command = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($command) { $command.Source } else { $null }
}
$pythonVersion = if ($python) { Invoke-Safe { & $python --version } } else { $null }
$torchVersion = Get-PackageVersion $python "torch"
$whisperVersion = Get-PackageVersion $python "whisperx"
$fasterVersion = Get-PackageVersion $python "faster-whisper"
$ctranslateVersion = Get-PackageVersion $python "ctranslate2"
$pyannoteVersion = Get-PackageVersion $python "pyannote.audio"
$onnxVersion = Get-PackageVersion $python "onnxruntime"
$torchRuntime = if ($python) { Invoke-Safe { & $python -c "import torch; print({'cuda': torch.version.cuda, 'cudaAvailable': torch.cuda.is_available()})" } } else { $null }
$ffmpeg = Invoke-Safe { & (Get-Command ffmpeg.exe -ErrorAction Stop).Source -version }
$ffprobe = Invoke-Safe { & (Get-Command ffprobe.exe -ErrorAction Stop).Source -version }
$dotnet = Invoke-Safe { & dotnet --version }
$modelRoot = if ([string]::IsNullOrWhiteSpace($env:LLM_MODEL_ROOT)) { "/models" } else { $env:LLM_MODEL_ROOT }
$modelDir = if ([string]::IsNullOrWhiteSpace($env:LLM_MODEL_DIR)) { "qwen3-8b" } else { $env:LLM_MODEL_DIR.Trim('/','\') }
$modelFile = if ([string]::IsNullOrWhiteSpace($env:LLM_MODEL_FILE)) { "Qwen3-8B-Q5_K_M.gguf" } else { $env:LLM_MODEL_FILE }
$modelPath = if (-not [string]::IsNullOrWhiteSpace($env:LLM_MODEL_PATH)) {
    $env:LLM_MODEL_PATH
} else {
    Join-Path (Join-Path $modelRoot $modelDir) $modelFile
}
$modelManifestPath = if (-not [string]::IsNullOrWhiteSpace($env:LLM_MODEL_MANIFEST)) {
    $env:LLM_MODEL_MANIFEST
} else {
    "$modelPath.manifest.json"
}
$modelHash = $env:LLM_MODEL_SHA256
$asrRepository = if ($env:WHISPERX_MODEL_REPOSITORY) { $env:WHISPERX_MODEL_REPOSITORY } else { "Systran/faster-whisper-large-v3" }
$asrSnapshot = Get-HfSnapshot $asrRepository $env:WHISPERX_MODEL_REVISION
$diarizationSnapshot = Get-HfSnapshot $env:DIARIZATION_MODEL $env:DIARIZATION_MODEL_REVISION
$asrRevision = if ($asrSnapshot) { $asrSnapshot.revision } else { $env:WHISPERX_MODEL_REVISION }
$diarizationRevision = if ($diarizationSnapshot) { $diarizationSnapshot.revision } else { $env:DIARIZATION_MODEL_REVISION }
$asrInventoryHash = if ($asrSnapshot) { $asrSnapshot.fileInventoryHash } else { $null }
$diarizationInventoryHash = if ($diarizationSnapshot) { $diarizationSnapshot.fileInventoryHash } else { $null }
# Deep hashing is intentionally opt-in: release install/download verifies it,
# while normal startup only records the pinned expected checksum.
if ($env:WHISPERX_RUNTIME_MANIFEST_DEEP -eq "true" -and $modelPath -and (Test-Path -LiteralPath $modelPath -PathType Leaf)) { $modelHash = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() }
$gitCommit = Invoke-Safe { git -C $RepoPath rev-parse HEAD }
$runtimeProfile = if ($env:WHISPERX_RUNTIME_PROFILE) { $env:WHISPERX_RUNTIME_PROFILE } else { "development" }
$releaseVersion = if ($env:WHISPERX_RELEASE_VERSION) { $env:WHISPERX_RELEASE_VERSION } else { "dev" }
$placeholderPattern = '^(|replace-with|changeme|change-me|password|latest|generate-|replace-with-)'
$requiredProduction = [ordered]@{
    releaseVersion = $releaseVersion
    gitCommit = $gitCommit
    python = $pythonVersion
    dotnet = $dotnet
    whisperX = $whisperVersion
    fasterWhisper = $fasterVersion
    pytorch = $torchVersion
    ctranslate2 = $ctranslateVersion
    pyannote = $pyannoteVersion
    onnxRuntime = $onnxVersion
    cuda = $torchRuntime
    ffmpeg = $ffmpeg
    ffprobe = $ffprobe
    whisperXModelRevision = $asrRevision
    whisperXModelIdentifier = $asrRepository
    whisperXModelInventoryHash = $asrInventoryHash
    diarizationModelRevision = $diarizationRevision
    diarizationModelIdentifier = $env:DIARIZATION_MODEL
    diarizationModelInventoryHash = $diarizationInventoryHash
    llmModelRevision = $env:LLM_MODEL_REVISION
    llmModelSha256 = $modelHash
}
if ($runtimeProfile -in @("production", "release")) {
    foreach ($entry in $requiredProduction.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$entry.Value) -or ([string]$entry.Value -match $placeholderPattern)) {
            throw "RUNTIME_MANIFEST_INCOMPLETE: $($entry.Key)"
        }
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    releaseVersion = $releaseVersion
    gitCommit = $gitCommit
    runtimeProfile = $runtimeProfile
    runtime = [ordered]@{
        python = $pythonVersion
        dotnet = $dotnet
        windowsAppSdk = "2.3.1"
        whisperX = $whisperVersion
        fasterWhisper = $fasterVersion
        pytorch = $torchVersion
        cuda = if ($torchRuntime) { $torchRuntime } else { $null }
        ctranslate2 = $ctranslateVersion
        pyannote = $pyannoteVersion
        onnxRuntime = $onnxVersion
        ffmpeg = $ffmpeg
        ffprobe = $ffprobe
        nats = if ($env:NATS_IMAGE) { $env:NATS_IMAGE } else { "nats:2.11.6-alpine3.21" }
        postgres = if ($env:POSTGRES_IMAGE) { $env:POSTGRES_IMAGE } else { "postgres:17.5-alpine3.21" }
        qwen = $modelPath
        llamaCpp = $env:LLAMA_IMAGE
        vosk = $env:VOSK_MODEL_PATH
    }
    models = @([ordered]@{
        name = if ($env:WHISPERX_MODEL) { $env:WHISPERX_MODEL } else { "large-v3" }
        revision = $asrRevision
        quantization = $env:COMPUTE_TYPE
        identifier = $asrRepository
        sha256 = $env:WHISPERX_MODEL_SHA256
        fileInventoryHash = $asrInventoryHash
    }, [ordered]@{
        name = "diarization"
        revision = $diarizationRevision
        identifier = $env:DIARIZATION_MODEL
        sha256 = $env:DIARIZATION_MODEL_SHA256
        fileInventoryHash = $diarizationInventoryHash
    }, [ordered]@{
        name = $modelFile
        revision = $env:LLM_MODEL_REVISION
        quantization = $env:LLM_QUANTIZATION
        identifier = $modelPath
        sha256 = $modelHash
        path = $modelPath
        manifestPath = $modelManifestPath
    })
    productionDownloads = "DISABLED"
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$modelManifest = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = $manifest.generatedAtUtc
    releaseVersion = $releaseVersion
    gitCommit = $gitCommit
    models = @($manifest.models)
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ModelManifestPath) | Out-Null
$modelManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ModelManifestPath -Encoding utf8
$manifest | ConvertTo-Json -Depth 12

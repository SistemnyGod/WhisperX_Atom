[CmdletBinding()]
param([string]$RepoPath, [string]$OutputPath)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoPath)) { $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $RepoPath "artifacts\release\runtime-manifest.json" }
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
$modelPath = $env:LLM_MODEL_FILE
$modelHash = $env:LLM_MODEL_SHA256
# Deep hashing is intentionally opt-in: release install/download verifies it,
# while normal startup only records the pinned expected checksum.
if ($env:WHISPERX_RUNTIME_MANIFEST_DEEP -eq "true" -and $modelPath -and (Test-Path -LiteralPath $modelPath -PathType Leaf)) { $modelHash = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() }
$gitCommit = Invoke-Safe { git -C $RepoPath rev-parse HEAD }

$manifest = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    releaseVersion = $env:WHISPERX_RELEASE_VERSION
    gitCommit = $gitCommit
    runtimeProfile = if ($env:WHISPERX_RUNTIME_PROFILE) { $env:WHISPERX_RUNTIME_PROFILE } else { "development" }
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
        nats = $env:NATS_IMAGE
        postgres = $env:POSTGRES_IMAGE
        qwen = $env:LLM_MODEL_FILE
        llamaCpp = $env:LLAMA_IMAGE
        vosk = $env:VOSK_MODEL_PATH
    }
    models = @([ordered]@{
        name = if ($env:WHISPERX_MODEL) { $env:WHISPERX_MODEL } else { "large-v3" }
        revision = $env:WHISPERX_MODEL_REVISION
        quantization = $env:COMPUTE_TYPE
        identifier = $env:WHISPERX_MODEL
        sha256 = $null
    }, [ordered]@{
        name = "diarization"
        revision = $env:DIARIZATION_MODEL_REVISION
        identifier = $env:DIARIZATION_MODEL
        sha256 = $env:DIARIZATION_MODEL_SHA256
    }, [ordered]@{
        name = $env:LLM_MODEL_FILE
        revision = $env:LLM_MODEL_REVISION
        quantization = $env:LLM_QUANTIZATION
        identifier = $env:LLM_MODEL_FILE
        sha256 = $modelHash
    })
    productionDownloads = "DISABLED"
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$manifest | ConvertTo-Json -Depth 12

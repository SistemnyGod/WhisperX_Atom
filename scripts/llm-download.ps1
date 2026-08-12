[CmdletBinding()]
param(
  [string]$Repository = "Qwen/Qwen3-8B-GGUF",
  [string]$Revision = "7c41481f57cb95916b40956ab2f0b139b296d974",
  [string]$FileName = "Qwen3-8B-Q5_K_M.gguf",
  [string]$Sha256 = "068BAE163FAA96AD48032DAF4E071A6A28FE67D8DCC95367609C2FF165E52738",
  [string]$ModelsRoot = $(if ($env:WHISPERX_MODELS_HOST) { $env:WHISPERX_MODELS_HOST } else { "C:\WhisperXAtom\Models" })
)

$ErrorActionPreference = "Stop"
$target = Join-Path $ModelsRoot "qwen3-8b"
New-Item -ItemType Directory -Force -Path $target | Out-Null
$model = Join-Path $target $FileName
$manifest = "$model.manifest.json"

function Write-ModelManifest {
  param([string]$Path, [string]$Hash, [long]$Size)
  $document = [ordered]@{
    schemaVersion = 1
    repository = $Repository
    revision = $Revision
    filename = $FileName
    sha256 = $Hash.ToUpperInvariant()
    size = $Size
    verifiedAtUtc = [DateTime]::UtcNow.ToString("o")
  }
  $temporaryManifest = "$Path.part"
  $json = $document | ConvertTo-Json -Depth 4
  [System.IO.File]::WriteAllText($temporaryManifest, $json, [System.Text.UTF8Encoding]::new($false))
  Move-Item -LiteralPath $temporaryManifest -Destination $Path -Force
}

if (Test-Path -LiteralPath $model -PathType Leaf) {
  $existingSha256 = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash
  if ($existingSha256 -ne $Sha256) { throw "Production model checksum mismatch; refusing to replace existing file: $model" }
  Write-ModelManifest -Path $manifest -Hash $existingSha256 -Size (Get-Item -LiteralPath $model).Length
  Write-Host "Pinned model already verified: $model"
  exit 0
}

$hfCommand = Get-Command hf -ErrorAction SilentlyContinue
$hfPath = if ($hfCommand) { $hfCommand.Source } else { Join-Path $env:APPDATA "Python\Python312\Scripts\hf.exe" }
if (-not (Test-Path -LiteralPath $hfPath)) {
  throw "hf CLI is not installed. Run: py -3.12 -m pip install --user --upgrade huggingface_hub"
}

& $hfPath download $Repository $FileName --revision $Revision --local-dir $target
if ($LASTEXITCODE -ne 0) { throw "Model download failed" }

if (-not (Test-Path -LiteralPath $model)) { throw "Downloaded model is missing: $model" }
$actualSha256 = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash
if ($actualSha256 -ne $Sha256) { throw "Model checksum mismatch: expected $Sha256, got $actualSha256" }
Write-ModelManifest -Path $manifest -Hash $actualSha256 -Size (Get-Item -LiteralPath $model).Length
Write-Host ("Model ready: {0} ({1:N2} GiB)" -f $model, ((Get-Item -LiteralPath $model).Length / 1GB))

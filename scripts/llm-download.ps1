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

$hfCommand = Get-Command hf -ErrorAction SilentlyContinue
$hfPath = if ($hfCommand) { $hfCommand.Source } else { Join-Path $env:APPDATA "Python\Python312\Scripts\hf.exe" }
if (-not (Test-Path -LiteralPath $hfPath)) {
  throw "hf CLI is not installed. Run: py -3.12 -m pip install --user --upgrade huggingface_hub"
}

& $hfPath download $Repository $FileName --revision $Revision --local-dir $target
if ($LASTEXITCODE -ne 0) { throw "Model download failed" }

$model = Join-Path $target $FileName
if (-not (Test-Path -LiteralPath $model)) { throw "Downloaded model is missing: $model" }
$actualSha256 = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash
if ($actualSha256 -ne $Sha256) { throw "Model checksum mismatch: expected $Sha256, got $actualSha256" }
Write-Host ("Model ready: {0} ({1:N2} GiB)" -f $model, ((Get-Item -LiteralPath $model).Length / 1GB))
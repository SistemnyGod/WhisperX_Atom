[CmdletBinding()]
param(
    [string]$ModelPath = (Join-Path $PSScriptRoot '..\artifacts\tts-host\Models\silero-v5_5_ru\v5_5_ru.pt'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\artifacts\tts-host\Models\silero-v5_5_ru\model-manifest.json')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) { throw 'TTS_MODEL_MISSING' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [string]::IsNullOrWhiteSpace([string]$manifest.sha256) -and $hash -ne ([string]$manifest.sha256).ToLowerInvariant()) { throw 'TTS_MODEL_HASH_MISMATCH' }
if ($manifest.modelId -ne 'silero-v5_5_ru' -or $manifest.speakers -notcontains 'aidar' -or $manifest.sampleRates -notcontains 48000) { throw 'TTS_MODEL_MANIFEST_INVALID' }
Write-Output (ConvertTo-Json @{ ok=$true; modelId=$manifest.modelId; sha256=$hash; cpuOnly=$true; speakers=@($manifest.speakers) } -Compress)

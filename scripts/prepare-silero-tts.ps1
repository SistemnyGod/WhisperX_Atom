[CmdletBinding()]
param(
    [string]$ModelPath = '',
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\apps\tts-host\model-manifest.json'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\tts-host\Models\silero-v5_5_ru'),
    [string]$ExpectedSha256 = '',
    [switch]$DownloadOfficial
)
$ErrorActionPreference = 'Stop'
$officialUrl = 'https://models.silero.ai/models/tts/ru/v5_5_ru.pt'
if ($DownloadOfficial) {
    if ($ModelPath -ne 'v5_5_ru.pt' -and -not [string]::IsNullOrWhiteSpace($ModelPath)) { throw 'TTS_OFFICIAL_URL_ONLY' }
    $downloadRoot = Join-Path ([IO.Path]::GetTempPath()) ('silero-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null
    $ModelPath = Join-Path $downloadRoot 'v5_5_ru.pt'
    Invoke-WebRequest -Uri $officialUrl -OutFile $ModelPath -UseBasicParsing
}
if ([string]::IsNullOrWhiteSpace($ModelPath)) { throw 'TTS_MODEL_PATH_REQUIRED' }
$source = (Resolve-Path -LiteralPath $ModelPath).Path
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "TTS_MODEL_MISSING" }
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = $ExpectedSha256
if ([string]::IsNullOrWhiteSpace($expected) -and (Test-Path $ManifestPath)) {
    $expected = ((Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json).sha256)
}
if (-not [string]::IsNullOrWhiteSpace($expected) -and $hash -ne $expected.ToLowerInvariant()) { throw "TTS_MODEL_HASH_MISMATCH" }
if ([string]::IsNullOrWhiteSpace($expected)) { throw 'TTS_MODEL_HASH_REQUIRED' }
$destination = Join-Path $OutputRoot 'v5_5_ru.pt'
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$part = "$destination.part"
Copy-Item -LiteralPath $source -Destination $part -Force
Move-Item -LiteralPath $part -Destination $destination -Force
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$manifest.sha256 = $hash
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'model-manifest.json') -Encoding utf8
Write-Output (ConvertTo-Json @{ modelId='silero-v5_5_ru'; path=$destination; sha256=$hash; sizeBytes=(Get-Item $destination).Length } -Compress)

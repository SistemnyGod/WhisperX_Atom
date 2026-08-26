[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [string]$OutputRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'vendor\tts-wheelhouse')
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceRoot)
$out = [IO.Path]::GetFullPath($OutputRoot)
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "TTS_WHEELHOUSE_SOURCE_MISSING: $source" }
$wheels = @(Get-ChildItem -LiteralPath $source -File -Filter '*.whl')
if ($wheels.Count -eq 0) { throw 'TTS_WHEELHOUSE_NO_WHEELS' }
if (-not (@($wheels | Where-Object Name -match '^torch-2\.8\.0(?:\+cpu|\.post).*\.whl$').Count)) { throw 'TTS_TORCH_CPU_WHEEL_MISSING' }
New-Item -ItemType Directory -Force -Path $out | Out-Null
foreach ($wheel in $wheels) { Copy-Item -LiteralPath $wheel.FullName -Destination (Join-Path $out $wheel.Name) -Force }
$entries = @($wheels | Sort-Object Name | ForEach-Object {
    [ordered]@{ name = $_.Name; sizeBytes = [int64]$_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ schemaVersion=1; python='3.12'; torch='2.8.0+cpu'; files=$entries; generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'wheelhouse-manifest.json') -Encoding utf8
Write-Output (Join-Path $out 'wheelhouse-manifest.json')

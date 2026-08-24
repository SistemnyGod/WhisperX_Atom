[CmdletBinding()]
param(
    [string]$AssetRoot = "",
    [string]$ManifestPath = "",
    [switch]$AllowUnavailable
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($AssetRoot)) { $AssetRoot = Join-Path $PSScriptRoot "..\apps\voice-host\Models\Voice\whisper-shadow" }
$root = [IO.Path]::GetFullPath($AssetRoot)
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $root "voice-refiner.manifest.json" }
$manifestPath = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    if ($AllowUnavailable) { Write-Host "VOICE_REFINER_ASSETS=UNAVAILABLE"; exit 0 }
    throw "VOICE_REFINER_MANIFEST_MISSING: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or [string]$manifest.provider -ne "whisper.cpp-native") { throw "VOICE_REFINER_MANIFEST_INVALID" }
foreach ($entry in @($manifest.files)) {
    $relative = [string]$entry.path
    $expected = ([string]$entry.sha256).ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($relative) -or $expected -notmatch '^[0-9a-f]{64}$') { throw "VOICE_REFINER_MANIFEST_ENTRY_INVALID" }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "VOICE_REFINER_ASSET_PATH_ESCAPE" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "VOICE_REFINER_ASSET_MISSING: $relative" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "VOICE_REFINER_ASSET_HASH_MISMATCH: $relative" }
}
if (@($manifest.files).Count -lt 2) { throw "VOICE_REFINER_ASSET_SET_INCOMPLETE" }
Write-Host "VOICE_REFINER_ASSETS=VERIFIED"
Write-Host "VOICE_REFINER_MODEL=$([string]$manifest.model)"

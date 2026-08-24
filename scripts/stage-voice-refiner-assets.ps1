[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModelPath,
    [Parameter(Mandatory = $true)][string]$NativeLibraryPath,
    [string]$OutputRoot = "",
    [string]$ModelName = "ggml-small.bin",
    [string]$NativeName = "whisperx-refiner.dll"
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $PSScriptRoot "..\apps\voice-host\Models\Voice\whisper-shadow" }
$root = [IO.Path]::GetFullPath($OutputRoot)
$model = [IO.Path]::GetFullPath($ModelPath)
$native = [IO.Path]::GetFullPath($NativeLibraryPath)
foreach ($path in @($model, $native)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "VOICE_REFINER_INPUT_MISSING: $path" }
}
if ([IO.Path]::GetExtension($modelName) -ne ".bin" -or [IO.Path]::GetExtension($NativeName) -notin @(".dll", ".so", ".dylib")) { throw "VOICE_REFINER_OUTPUT_NAME_INVALID" }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$modelTarget = Join-Path $root $ModelName
$nativeTarget = Join-Path $root $NativeName
Copy-Item -LiteralPath $model -Destination $modelTarget -Force
Copy-Item -LiteralPath $native -Destination $nativeTarget -Force
$manifest = [ordered]@{
    schemaVersion = 1
    provider = "whisper.cpp-native"
    model = $ModelName
    files = @(
        [ordered]@{ path = $ModelName; sha256 = (Get-FileHash -LiteralPath $modelTarget -Algorithm SHA256).Hash.ToLowerInvariant() },
        [ordered]@{ path = $NativeName; sha256 = (Get-FileHash -LiteralPath $nativeTarget -Algorithm SHA256).Hash.ToLowerInvariant() }
    )
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root "voice-refiner.manifest.json") -Encoding utf8
Write-Host "VOICE_REFINER_ASSETS_STAGED=true"
Write-Host "VOICE_REFINER_ROOT=$root"

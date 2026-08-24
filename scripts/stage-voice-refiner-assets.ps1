[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModelPath,
    [Parameter(Mandatory = $true)][string]$NativeLibraryPath,
    [Parameter(Mandatory = $true)][string]$ModelSource,
    [Parameter(Mandatory = $true)][string]$ModelRevision,
    [Parameter(Mandatory = $true)][string]$WhisperCppRevision,
    [Parameter(Mandatory = $true)][string]$BridgeRevision,
    [Parameter(Mandatory = $true)][string]$BuildIdentity,
    [string]$OutputRoot = "",
    [string]$ModelName = "ggml-small.bin",
    [string]$NativeName = "whisperx-refiner.dll",
    [int]$NativeAbiVersion = 1
)

$ErrorActionPreference = "Stop"
$expectedModelRevision = "c521a4b02f422512d734391fdf08bb08c0862f68"
$expectedModelSha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"
$expectedWhisperCppRevision = "f049fff95a089aa9969deb009cdd4892b3e74916"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $PSScriptRoot "..\apps\voice-host\Models\Voice\whisper-shadow" }
$root = [IO.Path]::GetFullPath($OutputRoot)
$model = [IO.Path]::GetFullPath($ModelPath)
$native = [IO.Path]::GetFullPath($NativeLibraryPath)
foreach ($path in @($model, $native)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "VOICE_REFINER_INPUT_MISSING: $path" }
}
if ([IO.Path]::GetExtension($ModelName) -ne ".bin" -or [IO.Path]::GetExtension($NativeName) -notin @(".dll", ".so", ".dylib")) { throw "VOICE_REFINER_OUTPUT_NAME_INVALID" }
foreach ($value in @($ModelRevision, $WhisperCppRevision, $BridgeRevision)) {
    if ($value -notmatch '^[0-9a-fA-F]{40}$') { throw "VOICE_REFINER_PROVENANCE_INVALID" }
}
if ([string]::IsNullOrWhiteSpace($ModelSource) -or [string]::IsNullOrWhiteSpace($BuildIdentity) -or $NativeAbiVersion -ne 1) { throw "VOICE_REFINER_MANIFEST_METADATA_INVALID" }
if ($ModelRevision -ne $expectedModelRevision -or $WhisperCppRevision -ne $expectedWhisperCppRevision -or $ModelSource -notmatch 'ggerganov/whisper\.cpp') { throw "VOICE_REFINER_PINNED_SOURCE_MISMATCH" }
$identityMatch = [regex]::Match($BuildIdentity, '\+([0-9a-fA-F]{40})(?:$|-)')
if ($identityMatch.Success -and $BridgeRevision -ne $identityMatch.Groups[1].Value) { throw "VOICE_REFINER_BRIDGE_IDENTITY_MISMATCH" }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$modelTarget = Join-Path $root $ModelName
$nativeTarget = Join-Path $root $NativeName
Copy-Item -LiteralPath $model -Destination $modelTarget -Force
Copy-Item -LiteralPath $native -Destination $nativeTarget -Force
$stagedModelHash = (Get-FileHash -LiteralPath $modelTarget -Algorithm SHA256).Hash.ToLowerInvariant()
if ($stagedModelHash -ne $expectedModelSha256) { throw "VOICE_REFINER_MODEL_SHA256_MISMATCH" }
$manifest = [ordered]@{
    schemaVersion = 2
    provider = "whisper.cpp-native"
    model = [ordered]@{
        name = "whisper.cpp multilingual small"
        source = $ModelSource
        revision = $ModelRevision
        file = $ModelName
        sizeBytes = (Get-Item -LiteralPath $modelTarget).Length
        sha256 = $stagedModelHash
    }
    native = [ordered]@{
        file = $NativeName
        whisperCppRevision = $WhisperCppRevision
        bridgeRevision = $BridgeRevision
        abiVersion = $NativeAbiVersion
        sizeBytes = (Get-Item -LiteralPath $nativeTarget).Length
        sha256 = (Get-FileHash -LiteralPath $nativeTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    buildIdentity = $BuildIdentity
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root "voice-refiner.manifest.json") -Encoding utf8
Write-Host "VOICE_REFINER_ASSETS_STAGED=true"
Write-Host "VOICE_REFINER_ROOT=$root"

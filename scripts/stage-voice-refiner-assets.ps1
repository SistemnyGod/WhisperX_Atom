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
    [string]$WhisperCppSource = "",
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
if ([string]::IsNullOrWhiteSpace($WhisperCppSource)) { $WhisperCppSource = $env:WHISPER_CPP_SOURCE_DIR }
if ([string]::IsNullOrWhiteSpace($WhisperCppSource)) { throw "VOICE_REFINER_WHISPER_CPP_SOURCE_REQUIRED" }
$sourceHead = (& git -C ([IO.Path]::GetFullPath($WhisperCppSource)) rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceHead -ne $expectedWhisperCppRevision) { throw "VOICE_REFINER_WHISPER_CPP_CHECKOUT_MISMATCH: expected=$expectedWhisperCppRevision actual=$sourceHead" }
$identityMatch = [regex]::Match($BuildIdentity, '\+([0-9a-fA-F]{40})(?:$|-)')
if ($identityMatch.Success -and $BridgeRevision -ne $identityMatch.Groups[1].Value) { throw "VOICE_REFINER_BRIDGE_IDENTITY_MISMATCH" }
$abiProbeSource = @'
using System;
using System.Runtime.InteropServices;
public static class WhisperXVoiceRefinerAbiProbe {
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32", SetLastError = true)] private static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AbiDelegate();
    public static int Read(string path) {
        var module = LoadLibrary(path);
        if (module == IntPtr.Zero) throw new InvalidOperationException("VOICE_REFINER_NATIVE_LOAD_FAILED");
        try {
            var symbol = GetProcAddress(module, "whisperx_refiner_abi_version");
            if (symbol == IntPtr.Zero) throw new InvalidOperationException("VOICE_REFINER_NATIVE_ABI_MISSING");
            return Marshal.GetDelegateForFunctionPointer<AbiDelegate>(symbol)();
        } finally { FreeLibrary(module); }
    }
}
'@
if (-not ('WhisperXVoiceRefinerAbiProbe' -as [type])) {
    Add-Type -TypeDefinition $abiProbeSource -ErrorAction Stop
}
$actualAbi = [WhisperXVoiceRefinerAbiProbe]::Read($native)
if ($actualAbi -ne $NativeAbiVersion) { throw "VOICE_REFINER_NATIVE_ABI_MISMATCH: expected=$NativeAbiVersion actual=$actualAbi" }
$staging = "$root.staging.$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$modelTarget = Join-Path $staging $ModelName
$nativeTarget = Join-Path $staging $NativeName
try {
    Copy-Item -LiteralPath $model -Destination $modelTarget -Force
    Copy-Item -LiteralPath $native -Destination $nativeTarget -Force
    $stagedModelHash = (Get-FileHash -LiteralPath $modelTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($stagedModelHash -ne $expectedModelSha256) { throw "VOICE_REFINER_MODEL_SHA256_MISMATCH" }
    $stagedNativeHash = (Get-FileHash -LiteralPath $nativeTarget -Algorithm SHA256).Hash.ToLowerInvariant()
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
        sha256 = $stagedNativeHash
    }
    buildIdentity = $BuildIdentity
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $staging "voice-refiner.manifest.json") -Encoding utf8
    $previous = $null
    if (Test-Path -LiteralPath $root) {
        $previous = "$root.previous.$([Guid]::NewGuid().ToString('N'))"
        Move-Item -LiteralPath $root -Destination $previous
    }
    try { Move-Item -LiteralPath $staging -Destination $root -ErrorAction Stop }
    catch {
        if ($previous -and (Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $root)) { Move-Item -LiteralPath $previous -Destination $root }
        throw
    }
    if ($previous -and (Test-Path -LiteralPath $previous)) { Remove-Item -LiteralPath $previous -Recurse -Force }
} catch { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }; throw }
Write-Host "VOICE_REFINER_ASSETS_STAGED=true"
Write-Host "VOICE_REFINER_ROOT=$root"

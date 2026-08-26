[CmdletBinding()]
param(
    [string]$AssetRoot = "",
    [string]$ManifestPath = "",
    [string]$ExpectedBuildIdentity = "",
    [switch]$AllowUnavailable
)

$ErrorActionPreference = "Stop"
$expectedModelRevision = "c521a4b02f422512d734391fdf08bb08c0862f68"
$expectedModelSha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"
$expectedWhisperCppRevision = "f049fff95a089aa9969deb009cdd4892b3e74916"
if ([string]::IsNullOrWhiteSpace($AssetRoot)) { $AssetRoot = Join-Path $PSScriptRoot "..\apps\voice-host\Models\Voice\whisper-shadow" }
$root = [IO.Path]::GetFullPath($AssetRoot)
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $root "voice-refiner.manifest.json" }
$manifestPath = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    if ($AllowUnavailable) { Write-Host "VOICE_REFINER_ASSETS=UNAVAILABLE"; exit 0 }
    throw "VOICE_REFINER_MANIFEST_MISSING: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or [string]$manifest.provider -ne "whisper.cpp-native") { throw "VOICE_REFINER_MANIFEST_UPGRADE_REQUIRED" }
if ([string]::IsNullOrWhiteSpace([string]$manifest.buildIdentity)) { throw "VOICE_REFINER_MANIFEST_IDENTITY_MISSING" }
if (-not [string]::IsNullOrWhiteSpace($ExpectedBuildIdentity) -and [string]$manifest.buildIdentity -ne $ExpectedBuildIdentity) { throw "VOICE_REFINER_BUILD_IDENTITY_MISMATCH" }
if ($manifest.native.abiVersion -ne 1) { throw "VOICE_REFINER_NATIVE_ABI_MISMATCH" }
foreach ($revision in @([string]$manifest.model.revision, [string]$manifest.native.whisperCppRevision, [string]$manifest.native.bridgeRevision)) {
    if ($revision -notmatch '^[0-9a-fA-F]{40}$') { throw "VOICE_REFINER_PROVENANCE_INVALID" }
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.model.source)) { throw "VOICE_REFINER_MODEL_SOURCE_MISSING" }
if ([string]$manifest.model.revision -ne $expectedModelRevision -or [string]$manifest.model.source -notmatch 'ggerganov/whisper\.cpp') { throw "VOICE_REFINER_PINNED_SOURCE_MISMATCH" }
if ([string]$manifest.native.whisperCppRevision -ne $expectedWhisperCppRevision) { throw "VOICE_REFINER_WHISPER_CPP_REVISION_MISMATCH" }
$identityMatch = [regex]::Match([string]$manifest.buildIdentity, '\+([0-9a-fA-F]{40})(?:$|-)')
if ($identityMatch.Success -and [string]$manifest.native.bridgeRevision -ne $identityMatch.Groups[1].Value) { throw "VOICE_REFINER_BRIDGE_IDENTITY_MISMATCH" }
$entries = @(
    @{ relative = [string]$manifest.model.file; expected = ([string]$manifest.model.sha256).ToLowerInvariant(); declaredSize = [int64]$manifest.model.sizeBytes },
    @{ relative = [string]$manifest.native.file; expected = ([string]$manifest.native.sha256).ToLowerInvariant(); declaredSize = [int64]$manifest.native.sizeBytes }
)
foreach ($entry in $entries) {
    if ([string]::IsNullOrWhiteSpace($entry.relative) -or $entry.expected -notmatch '^[0-9a-f]{64}$') { throw "VOICE_REFINER_MANIFEST_ENTRY_INVALID" }
    $path = [IO.Path]::GetFullPath((Join-Path $root $entry.relative))
    if (-not $path.StartsWith($root + [String][IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "VOICE_REFINER_ASSET_PATH_ESCAPE" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "VOICE_REFINER_ASSET_MISSING: $($entry.relative)" }
    $item = Get-Item -LiteralPath $path
    if ($entry.declaredSize -le 0 -or $item.Length -ne $entry.declaredSize) { throw "VOICE_REFINER_ASSET_SIZE_MISMATCH: $($entry.relative)" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.expected) { throw "VOICE_REFINER_ASSET_HASH_MISMATCH: $($entry.relative)" }
}
if ($entries[0].expected -ne $expectedModelSha256) { throw "VOICE_REFINER_MODEL_SHA256_MISMATCH" }
$nativePath = [IO.Path]::GetFullPath((Join-Path $root ([string]$manifest.native.file)))
$abiProbeSource = @'
using System;
using System.Runtime.InteropServices;
public static class WhisperXVoiceRefinerAbiProbeVerify {
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
if (-not ('WhisperXVoiceRefinerAbiProbeVerify' -as [type])) {
    Add-Type -TypeDefinition $abiProbeSource -ErrorAction Stop
}
$actualAbi = [WhisperXVoiceRefinerAbiProbeVerify]::Read($nativePath)
if ($actualAbi -ne [int]$manifest.native.abiVersion) { throw "VOICE_REFINER_NATIVE_ABI_MISMATCH: expected=$([int]$manifest.native.abiVersion) actual=$actualAbi" }
Write-Host "VOICE_REFINER_ASSETS=VERIFIED"
Write-Host "VOICE_REFINER_BUILD_IDENTITY=$([string]$manifest.buildIdentity)"
Write-Host "VOICE_REFINER_MODEL=$([string]$manifest.model.file)"
Write-Host "VOICE_REFINER_NATIVE_ABI=$([int]$manifest.native.abiVersion)"

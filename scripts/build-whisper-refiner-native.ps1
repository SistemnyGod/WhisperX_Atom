[CmdletBinding()]
param(
    [string]$BuildRoot = "",
    [string]$Configuration = "Release",
    [string]$Generator = "Visual Studio 17 2022",
    [string]$Architecture = "x64",
    [string]$WhisperCppSource = ""
)

$ErrorActionPreference = "Stop"
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\apps\voice-host\native\whisper-refiner"))
if ([string]::IsNullOrWhiteSpace($BuildRoot)) { $BuildRoot = Join-Path $PSScriptRoot "..\artifacts\voice-refiner-native" }
$build = [IO.Path]::GetFullPath($BuildRoot)
New-Item -ItemType Directory -Force -Path $build | Out-Null
$revision = "f049fff95a089aa9969deb009cdd4892b3e74916"
if ([string]::IsNullOrWhiteSpace($WhisperCppSource)) { $WhisperCppSource = $env:WHISPER_CPP_SOURCE_DIR }
if ([string]::IsNullOrWhiteSpace($WhisperCppSource)) { throw "VOICE_REFINER_WHISPER_CPP_SOURCE_REQUIRED" }
$whisperSourceFull = [IO.Path]::GetFullPath($WhisperCppSource)
$actualRevision = (& git -C $whisperSourceFull rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $revision) {
    throw "VOICE_REFINER_WHISPER_CPP_CHECKOUT_MISMATCH: expected=$revision actual=$actualRevision"
}
function Invoke-Native([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "VOICE_REFINER_NATIVE_BUILD_FAILED: $File ($LASTEXITCODE)" }
}
$configure = @("-S", $source, "-B", $build, "-G", $Generator, "-A", $Architecture, "-DWHISPER_CPP_REVISION=$revision", "-DWHISPER_BUILD_TESTS=OFF", "-DWHISPER_BUILD_EXAMPLES=OFF", "-DWHISPER_BUILD_SERVER=OFF", "-DGGML_CUDA=OFF", "-DGGML_NATIVE=OFF")
$configure += "-DWHISPER_CPP_SOURCE_DIR=$whisperSourceFull"
Invoke-Native "cmake" $configure
Invoke-Native "cmake" @("--build", $build, "--config", $Configuration, "--target", "whisperx-refiner", "--parallel")
$native = Get-ChildItem -LiteralPath $build -Filter "whisperx-refiner.dll" -Recurse -File | Select-Object -First 1
if ($null -eq $native) { throw "VOICE_REFINER_NATIVE_BUILD_OUTPUT_MISSING" }
Write-Host "VOICE_REFINER_NATIVE=$($native.FullName)"
Write-Host "VOICE_REFINER_WHISPER_CPP_REVISION=$revision"

[CmdletBinding()]
param(
    [string]$BuildRoot = "",
    [string]$Configuration = "Release",
    [string]$Generator = "Visual Studio 17 2022",
    [string]$Architecture = "x64",
    [string]$WhisperCppSource = ""
)

$ErrorActionPreference = "Stop"
# Some orchestrated Windows shells expose both Path and PATH. MSBuild copies
# the process environment into a case-insensitive Hashtable and then fails
# with MSB6001 before invoking CL.exe. Canonicalize only this build process;
# the machine/user environment is never modified.
$pathValue = $env:Path
if ($env:OS -eq 'Windows_NT') {
    # Windows PowerShell exposes the duplicate keys through a case-insensitive
    # provider, so counting them is not reliable. Remove the orchestrator's
    # uppercase alias explicitly and write one canonical key.
    [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
    [Environment]::SetEnvironmentVariable('Path', $pathValue, 'Process')
}
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
$buildArguments = @("--build", $build, "--config", $Configuration, "--target", "whisperx-refiner")
if ($env:OS -eq 'Windows_NT') {
    # A single MSBuild node is deterministic and avoids propagating duplicate
    # environment aliases through worker nodes in orchestrated shells.
    $buildArguments += @("--parallel", "1")
} else {
    $buildArguments += "--parallel"
}
Invoke-Native "cmake" $buildArguments
$native = Get-ChildItem -LiteralPath $build -Filter "whisperx-refiner.dll" -Recurse -File | Select-Object -First 1
if ($null -eq $native) { throw "VOICE_REFINER_NATIVE_BUILD_OUTPUT_MISSING" }
Write-Host "VOICE_REFINER_NATIVE=$($native.FullName)"
Write-Host "VOICE_REFINER_WHISPER_CPP_REVISION=$revision"

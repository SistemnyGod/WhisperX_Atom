param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\artifacts\desktop"),
    [switch]$NoRestore,
    [switch]$AllowDirty,
    [switch]$RequireVoiceRefinerAssets,
    [switch]$DevelopmentNoVoiceRefinerAssets,
    [string]$TtsWheelhouse = ''
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# LAN/client release builds must not inherit a stale desktop proxy (for
# example 127.0.0.1:9). NuGet restore and self-contained publish use the same
# process environment; clear proxy variables before invoking dotnet so a
# disconnected local proxy cannot turn a valid offline cache into a false
# release failure. This does not change the runtime API proxy policy.
foreach ($proxyVariable in @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','http_proxy','https_proxy','all_proxy')) {
    Remove-Item "Env:$proxyVariable" -ErrorAction SilentlyContinue
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$gitCommit = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
if ([string]::IsNullOrWhiteSpace($gitCommit) -or $gitCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Unable to resolve a full release commit; refusing to publish an unidentified runtime."
}
# A clean installed runtime must be reproducible. Include normal untracked
# files in the gate: a new source file (or a legacy Python entry point) must
# never be silently omitted from the commit that produced the binaries.
#
# The workspace can contain ignored test-owned directories that are no longer
# readable by the interactive user. `git` writes warnings about those paths to
# stderr; in PowerShell 7 they can be promoted to terminating native-command
# errors even though `git status` itself succeeds. Run the status command via
# cmd with stderr explicitly discarded, while still checking its actual exit
# code so a genuine Git failure remains fail-closed.
$dirtyFiles = @(cmd.exe /d /s /c "git -C `"$repoRoot`" status --porcelain --untracked-files=normal 2>NUL")
if ($LASTEXITCODE -ne 0) { throw "GIT_STATUS_FAILED: cannot determine release cleanliness." }
$dirtyAllowed = $AllowDirty -or ($env:WHISPERX_ALLOW_DIRTY_RELEASE -in @("1", "true", "yes"))
if ($dirtyFiles.Count -gt 0 -and -not $dirtyAllowed) {
    throw "Working tree is dirty; commit the release or set WHISPERX_ALLOW_DIRTY_RELEASE only for an explicit development package."
}
$dirtySuffix = if ($dirtyFiles.Count -gt 0) { "-dirty" } else { "" }
$buildIdentity = "1.0.1+$gitCommit$dirtySuffix"
Write-Host "Publishing build identity $buildIdentity"
if ($DevelopmentNoVoiceRefinerAssets) {
    if (-not [string]::Equals($env:VOICE_ASR_REFINER_MODE, 'OFF', [StringComparison]::OrdinalIgnoreCase)) {
        throw "VOICE_REFINER_DEVELOPMENT_MODE_REQUIRES_OFF"
    }
} else {
    & (Join-Path $PSScriptRoot 'verify-voice-refiner-assets.ps1') -ExpectedBuildIdentity $buildIdentity
    if ($LASTEXITCODE -ne 0) { throw "VOICE_REFINER_ASSET_GATE_FAILED" }
}
$finalOutput = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { [System.IO.Path]::GetFullPath($OutputRoot) } else { [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot)) }
if ([string]::IsNullOrWhiteSpace($finalOutput) -or $finalOutput -eq $repoRoot -or $finalOutput.Length -lt ($repoRoot.Length + 8)) {
    throw "Refusing unsafe output path: $finalOutput"
}
$output = "$finalOutput.staging.$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $output | Out-Null

$desktopProject = Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\WhisperX.Atom.Desktop.csproj"
$serviceProject = Join-Path $repoRoot "apps\recorder-agent\WhisperX.Atom.Recorder.Service.csproj"
$recorderHostProject = Join-Path $repoRoot "apps\recorder-host\WhisperX.Atom.Recorder.Host.csproj"
$voiceHostProject = Join-Path $repoRoot "apps\voice-host\WhisperX.Atom.Voice.Host\WhisperX.Atom.Voice.Host.csproj"
$voiceRefinerHostProject = Join-Path $repoRoot "apps\voice-host\WhisperX.Atom.Voice.Refiner.Host\WhisperX.Atom.Voice.Refiner.Host.csproj"
$updaterProject = Join-Path $repoRoot "apps\desktop\Updater\WhisperX.Atom.Updater.csproj"
$desktopOut = Join-Path $output "Desktop"
$serviceOut = Join-Path $output "Service"
$recorderHostOut = Join-Path $output "RecorderHost"
$voiceHostOut = Join-Path $output "VoiceHost"
$voiceRefinerHostOut = Join-Path $output "VoiceRefinerHost"
$updaterOut = Join-Path $output "Updater"
$publishRestoreArgs = if ($NoRestore) { @("--no-restore") } else { @() }

# WinUI 3 is published as an unpackaged self-contained directory. Keeping the
# runtime files beside the exe avoids single-file extraction into a temp folder
# and keeps the Inno Setup payload transparent to endpoint protection.
$identityArg = "-p:WhisperXBuildIdentity=$buildIdentity"
$restoreProperties = @("-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=true")
$desktopPublishArgs = @($desktopProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:WindowsPackageType=None", "-p:WindowsAppSDKSelfContained=true", "-p:PublishSingleFile=false") + $restoreProperties + @($identityArg, "-o", $desktopOut) + $publishRestoreArgs
$servicePublishArgs = @($serviceProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true") + $restoreProperties + @($identityArg, "-o", $serviceOut) + $publishRestoreArgs
$recorderHostPublishArgs = @($recorderHostProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true") + $restoreProperties + @($identityArg, "-o", $recorderHostOut) + $publishRestoreArgs
$voiceHostPublishArgs = @($voiceHostProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true") + $restoreProperties + @($identityArg, "-o", $voiceHostOut) + $publishRestoreArgs
$voiceRefinerHostPublishArgs = @($voiceRefinerHostProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true") + $restoreProperties + @($identityArg, "-o", $voiceRefinerHostOut) + $publishRestoreArgs
$updaterPublishArgs = @($updaterProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true") + $restoreProperties + @($identityArg, "-o", $updaterOut) + $publishRestoreArgs
function Invoke-Publish([string[]]$Arguments) {
    $proxyNames = @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','http_proxy','https_proxy','all_proxy')
    $saved = @{}
    foreach ($name in $proxyNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process'); [Environment]::SetEnvironmentVariable($name, '', 'Process') }
    try {
        & dotnet publish @Arguments
        $exitCode = [int]$LASTEXITCODE
        if ($exitCode -ne 0) { throw "dotnet publish failed with exit code $exitCode" }
    } finally {
        foreach ($name in $proxyNames) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}
Invoke-Publish $desktopPublishArgs
Invoke-Publish $servicePublishArgs
Invoke-Publish $recorderHostPublishArgs
Invoke-Publish $voiceHostPublishArgs
Invoke-Publish $voiceRefinerHostPublishArgs
Invoke-Publish $updaterPublishArgs
Copy-Item -LiteralPath (Join-Path $updaterOut "WhisperX.Atom.Updater.exe") -Destination (Join-Path $desktopOut "WhisperX.Atom.Updater.exe") -Force

# Silero is a build-time dependency. It is staged outside Git and frozen into
# an onedir host; a release must fail closed when the model/runtime is absent.
$ttsPublisher = Join-Path $repoRoot "scripts\publish-tts-host.ps1"
# Run in the current PowerShell host.  Spawning a nested Windows PowerShell
# loses the caller's module/session environment on some Server Nodes, which
# can make built-in hash verification unavailable halfway through packaging.
# Named parameters also keep the wheelhouse from being mistaken for OutputRoot.
if ($TtsWheelhouse) {
    & $ttsPublisher -OutputRoot (Join-Path $output 'TtsHost') -AllowGeneratedStagingDirty -WheelhouseRoot $TtsWheelhouse
} else {
    & $ttsPublisher -OutputRoot (Join-Path $output 'TtsHost') -AllowGeneratedStagingDirty
}

# The supported Windows runtime is .NET Desktop + AudioGraph Host + Voice
# Host.  The legacy Python app.py is kept in the repository for compatibility,
# but must never leak into an installed payload or become a second production
# entry point.
$ttsVendorRoot = [IO.Path]::GetFullPath((Join-Path $output 'TtsHost\_internal\torch'))
$ttsApplicationSources = @('tts_host.py', 'protocol.py', 'silero_runtime.py', 'text_normalizer.py')
$forbiddenPayload = @(Get-ChildItem -LiteralPath $output -Recurse -File -ErrorAction Stop | Where-Object {
    $fullPath = [IO.Path]::GetFullPath($_.FullName)
    $isFrozenTorchVendor = $fullPath.StartsWith($ttsVendorRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    $_.Name -ieq 'app.py' -or
        $_.Name -match '(?i)^python(?:\.exe)?$' -or
        $_.Name -iin $ttsApplicationSources -or
        ($_.Extension -iin @('.py', '.pyc', '.pyo') -and -not $isFrozenTorchVendor)
})
if ($forbiddenPayload.Count -gt 0) {
    throw "PRODUCTION_PAYLOAD_CONTAINS_LEGACY_PYTHON: $($forbiddenPayload.FullName -join ', ')"
}

Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Install-Service.ps1") $output
Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Uninstall-Service.ps1") $output
Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Configure-RecorderHostUser.ps1") $output
Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Preflight-Upgrade.ps1") $output

# FFmpeg is an explicit installer input. Do not silently pick an arbitrary
# executable from the build host PATH; release packaging must be reproducible.
$ffmpegSource = if (-not [string]::IsNullOrWhiteSpace($env:WHISPERX_FFMPEG_DIR)) { $env:WHISPERX_FFMPEG_DIR } else { Join-Path $repoRoot "vendor\ffmpeg\win-x64" }
$ffmpegSource = [IO.Path]::GetFullPath($ffmpegSource)
foreach ($tool in @("ffmpeg.exe", "ffprobe.exe")) {
    $source = Join-Path $ffmpegSource $tool
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Pinned FFmpeg payload is missing: $source. Stage the verified binaries under vendor\ffmpeg\win-x64 or set WHISPERX_FFMPEG_DIR."
    }
    foreach ($target in @($serviceOut, $recorderHostOut)) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $target $tool) -Force
    }
}
$ffmpegManifest = Join-Path $ffmpegSource "ffmpeg-manifest.json"
if (-not (Test-Path -LiteralPath $ffmpegManifest -PathType Leaf)) { throw "Pinned FFmpeg manifest is missing: $ffmpegManifest" }
$manifest = Get-Content -LiteralPath $ffmpegManifest -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$manifest.version)) { throw "Pinned FFmpeg manifest is invalid." }
foreach ($target in @($serviceOut, $recorderHostOut)) {
    foreach ($entry in $manifest.files) {
        $payload = Join-Path $target ([string]$entry.file)
        if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) { throw "Pinned FFmpeg manifest file is missing: $($entry.file)" }
        $actual = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Pinned FFmpeg checksum mismatch: $($entry.file)" }
    }
    Copy-Item -LiteralPath $ffmpegManifest -Destination (Join-Path $target "ffmpeg-manifest.json") -Force
}
[ordered]@{
    schemaVersion = 1
    product = "WhisperX Atom"
    version = "1.0.1"
    buildIdentity = $buildIdentity
    commit = $gitCommit
    dirty = $dirtyFiles.Count -gt 0
    runtimeEntrypoint = "WhisperX.Atom.Desktop.exe"
    supportedWindowsRuntime = @("Desktop", "AudioGraphRecorderHost", "VoiceHost", "VoiceRefinerHost", "SileroTtsHost")
    legacyService = [ordered]@{ path = "Service\\WhisperX.Atom.Recorder.Service.exe"; supported = $false; mode = "manual-fallback-only" }
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    components = @(
        @{ name = "Desktop"; path = (Join-Path $desktopOut "WhisperX.Atom.Desktop.exe") },
        @{ name = "RecorderService"; path = (Join-Path $serviceOut "WhisperX.Atom.Recorder.Service.exe") },
        @{ name = "RecorderHost"; path = (Join-Path $recorderHostOut "WhisperX.Atom.Recorder.Host.exe") },
        @{ name = "VoiceHost"; path = (Join-Path $voiceHostOut "WhisperX.Atom.Voice.Host.exe") },
        @{ name = "VoiceRefinerHost"; path = (Join-Path $voiceRefinerHostOut "WhisperX.Atom.Voice.Refiner.Host.exe") },
        @{ name = "Updater"; path = (Join-Path $desktopOut "WhisperX.Atom.Updater.exe") },
        @{ name = "TtsHost"; path = (Join-Path $output "TtsHost\TtsHost.exe") }
    ) | ForEach-Object {
        if (-not (Test-Path -LiteralPath $_.path -PathType Leaf)) { throw "RELEASE_COMPONENT_MISSING: $($_.name)" }
        $actualIdentity = [string](Get-Item -LiteralPath $_.path).VersionInfo.ProductVersion
        if ($actualIdentity -ne $buildIdentity) { throw "RELEASE_COMPONENT_IDENTITY_MISMATCH: $($_.name)=$actualIdentity expected=$buildIdentity" }
        [ordered]@{ name = $_.name; path = $_.path.Substring($output.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output "build-identity.json") -Encoding utf8
try {
    $previous = $null
    if (Test-Path -LiteralPath $finalOutput) {
        $previous = "$finalOutput.previous.$([Guid]::NewGuid().ToString('N'))"
        Move-Item -LiteralPath $finalOutput -Destination $previous
    }
    Move-Item -LiteralPath $output -Destination $finalOutput
    if ($previous -and (Test-Path -LiteralPath $previous)) { Remove-Item -LiteralPath $previous -Recurse -Force }
} catch {
    if (-not (Test-Path -LiteralPath $finalOutput) -and $previous -and (Test-Path -LiteralPath $previous)) {
        Move-Item -LiteralPath $previous -Destination $finalOutput
    }
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    throw
}
Write-Host "Desktop package published to $finalOutput"
Write-Host "Next: compile apps\desktop\Installer\WhisperXAtom.iss with Inno Setup."

param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\artifacts\desktop"),
    [switch]$NoRestore,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$gitCommit = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
if ([string]::IsNullOrWhiteSpace($gitCommit)) { throw "Unable to resolve release commit; refusing to publish an unidentified runtime." }
# A large local test cache can contain ACL-protected temporary directories.
# Tracked changes are sufficient to gate this development publish (the
# publish itself is already marked dirty); avoid turning Git's warning stream
# into a PowerShell terminating error before the actual build starts.
$dirtyFiles = @(& git -C $repoRoot status --porcelain --untracked-files=no 2>$null)
$dirtyAllowed = $AllowDirty -or ($env:WHISPERX_ALLOW_DIRTY_RELEASE -in @("1", "true", "yes"))
if ($dirtyFiles.Count -gt 0 -and -not $dirtyAllowed) {
    throw "Working tree is dirty; commit the release or set WHISPERX_ALLOW_DIRTY_RELEASE only for an explicit development package."
}
$dirtySuffix = if ($dirtyFiles.Count -gt 0) { "-dirty" } else { "" }
$buildIdentity = "1.0.1+$gitCommit$dirtySuffix"
Write-Host "Publishing build identity $buildIdentity"
$output = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { [System.IO.Path]::GetFullPath($OutputRoot) } else { [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot)) }
if ([string]::IsNullOrWhiteSpace($output) -or $output -eq $repoRoot -or $output.Length -lt ($repoRoot.Length + 8)) {
    throw "Refusing unsafe output path: $output"
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

$desktopProject = Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\WhisperX.Atom.Desktop.csproj"
$serviceProject = Join-Path $repoRoot "apps\recorder-agent\WhisperX.Atom.Recorder.Service.csproj"
$recorderHostProject = Join-Path $repoRoot "apps\recorder-host\WhisperX.Atom.Recorder.Host.csproj"
$voiceHostProject = Join-Path $repoRoot "apps\voice-host\WhisperX.Atom.Voice.Host\WhisperX.Atom.Voice.Host.csproj"
$desktopOut = Join-Path $output "Desktop"
$serviceOut = Join-Path $output "Service"
$recorderHostOut = Join-Path $output "RecorderHost"
$voiceHostOut = Join-Path $output "VoiceHost"
$publishRestoreArgs = if ($NoRestore) { @("--no-restore") } else { @() }

# WinUI 3 is published as an unpackaged self-contained directory. Keeping the
# runtime files beside the exe avoids single-file extraction into a temp folder
# and keeps the Inno Setup payload transparent to endpoint protection.
$identityArg = "-p:WhisperXBuildIdentity=$buildIdentity"
$desktopPublishArgs = @($desktopProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:WindowsPackageType=None", "-p:WindowsAppSDKSelfContained=true", "-p:PublishSingleFile=false", "-p:NuGetAudit=false", $identityArg, "-o", $desktopOut) + $publishRestoreArgs
$servicePublishArgs = @($serviceProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:NuGetAudit=false", $identityArg, "-o", $serviceOut) + $publishRestoreArgs
$recorderHostPublishArgs = @($recorderHostProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:NuGetAudit=false", $identityArg, "-o", $recorderHostOut) + $publishRestoreArgs
$voiceHostPublishArgs = @($voiceHostProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:NuGetAudit=false", $identityArg, "-o", $voiceHostOut) + $publishRestoreArgs
function Invoke-Publish([string[]]$Arguments) {
    & dotnet publish @Arguments
    $exitCode = [int]$LASTEXITCODE
    if ($exitCode -ne 0) { throw "dotnet publish failed with exit code $exitCode" }
}
Invoke-Publish $desktopPublishArgs
Invoke-Publish $servicePublishArgs
Invoke-Publish $recorderHostPublishArgs
Invoke-Publish $voiceHostPublishArgs

Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Install-Service.ps1") $output
Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Uninstall-Service.ps1") $output
Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Configure-RecorderHostUser.ps1") $output

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
Write-Host "Desktop package published to $output"
Write-Host "Next: compile apps\desktop\Installer\WhisperXAtom.iss with Inno Setup."

param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\artifacts\desktop"),
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
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
$desktopOut = Join-Path $output "Desktop"
$serviceOut = Join-Path $output "Service"
$recorderHostOut = Join-Path $output "RecorderHost"
$publishRestoreArgs = if ($NoRestore) { @("--no-restore") } else { @() }

# WinUI 3 is published as an unpackaged self-contained directory. Keeping the
# runtime files beside the exe avoids single-file extraction into a temp folder
# and keeps the Inno Setup payload transparent to endpoint protection.
$desktopPublishArgs = @($desktopProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:WindowsPackageType=None", "-p:WindowsAppSDKSelfContained=true", "-p:PublishSingleFile=false", "-p:NuGetAudit=false", "-o", $desktopOut) + $publishRestoreArgs
$servicePublishArgs = @($serviceProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:NuGetAudit=false", "-o", $serviceOut) + $publishRestoreArgs
$recorderHostPublishArgs = @($recorderHostProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:NuGetAudit=false", "-o", $recorderHostOut) + $publishRestoreArgs
function Invoke-Publish([string[]]$Arguments) {
    & dotnet publish @Arguments
    $exitCode = [int]$LASTEXITCODE
    if ($exitCode -ne 0) { throw "dotnet publish failed with exit code $exitCode" }
}
Invoke-Publish $desktopPublishArgs
Invoke-Publish $servicePublishArgs
Invoke-Publish $recorderHostPublishArgs

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

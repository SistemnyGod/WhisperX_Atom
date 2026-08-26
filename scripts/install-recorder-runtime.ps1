[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$target = "C:\ProgramData\WhisperXAtom\AgentUpdate"
$publishRoot = Join-Path $repo "artifacts\recorder-current"
$project = Join-Path $repo "apps\recorder-agent\WhisperX.Atom.Recorder.Service.csproj"
$sourceExe = Join-Path $publishRoot "WhisperX.Atom.Recorder.Service.exe"
$installer = Join-Path $repo "apps\desktop\Installer\Install-Service.ps1"
$ffmpegSourceRoot = if (-not [string]::IsNullOrWhiteSpace($env:WHISPERX_FFMPEG_DIR)) { $env:WHISPERX_FFMPEG_DIR } else { Join-Path $repo "vendor\ffmpeg\win-x64" }
$ffmpegSourceRoot = [IO.Path]::GetFullPath($ffmpegSourceRoot)

$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    # Service registration and ProgramData deployment require elevation. Keep
    # the normal command one-step: Windows shows the standard UAC prompt and
    # this process returns the elevated install exit code to the caller.
    $elevatedArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", ('"{0}"' -f $PSCommandPath)
    )
    if ($NoBuild) { $elevatedArguments += "-NoBuild" }
    try {
        $elevated = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $elevatedArguments -Wait -PassThru
        exit $elevated.ExitCode
    }
    catch {
        throw "ELEVATION_REQUIRED: UAC elevation was cancelled or unavailable."
    }
}

if ($NoBuild -and (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    $publishedAt = (Get-Item -LiteralPath $sourceExe).LastWriteTimeUtc
    $newestSource = Get-ChildItem -LiteralPath (Join-Path $repo "apps\recorder-agent") -Recurse -File |
        Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" -and $_.Extension -in @(".cs", ".csproj", ".props", ".targets") } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -ne $newestSource -and $newestSource.LastWriteTimeUtc -gt $publishedAt) {
        throw "RECORDER_PUBLISH_STALE: $($newestSource.FullName) is newer than $sourceExe. Rerun without -NoBuild."
    }
}

if (-not $NoBuild -or -not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -o $publishRoot --no-restore
    if ($LASTEXITCODE -ne 0) { throw "RECORDER_PUBLISH_FAILED" }
}
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "RECORDER_BINARY_NOT_FOUND: $sourceExe" }

# Keep the standalone service installer aligned with the release publisher.
# The service must use the pinned payload beside its executable, never a
# machine PATH executable.
$ffmpegManifestPath = Join-Path $ffmpegSourceRoot "ffmpeg-manifest.json"
if (-not (Test-Path -LiteralPath $ffmpegManifestPath -PathType Leaf)) {
    throw "FFMPEG_MANIFEST_NOT_FOUND: $ffmpegManifestPath"
}
$ffmpegManifest = Get-Content -LiteralPath $ffmpegManifestPath -Raw | ConvertFrom-Json
if ($ffmpegManifest.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$ffmpegManifest.version)) {
    throw "FFMPEG_MANIFEST_INVALID: $ffmpegManifestPath"
}
foreach ($entry in $ffmpegManifest.files) {
    $name = [string]$entry.file
    if ($name -notin @("ffmpeg.exe", "ffprobe.exe")) { throw "FFMPEG_MANIFEST_FILE_UNEXPECTED: $name" }
    $source = Join-Path $ffmpegSourceRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "FFMPEG_BINARY_MISSING: $source" }
    $actualHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "FFMPEG_CHECKSUM_MISMATCH: $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $publishRoot $name) -Force
}
Copy-Item -LiteralPath $ffmpegManifestPath -Destination (Join-Path $publishRoot "ffmpeg-manifest.json") -Force

# Install-Service.ps1 recreates the service, but the executable must be
# replaceable before it is invoked. Stop the existing process first and wait
# for Windows to release the image file; otherwise Copy-Item fails with a
# sharing violation while the old Recorder is still running.
$existingService = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction SilentlyContinue
if ($null -ne $existingService -and $existingService.Status -ne "Stopped") {
    Stop-Service -Name "WhisperXAtomRecorder" -Force -ErrorAction Stop
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $existingService = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction SilentlyContinue
    } while ($null -ne $existingService -and $existingService.Status -ne "Stopped" -and [DateTime]::UtcNow -lt $deadline)
    if ($null -ne $existingService -and $existingService.Status -ne "Stopped") {
        throw "RECORDER_SERVICE_STOP_FAILED: service did not stop within 20 seconds."
    }
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem -LiteralPath $publishRoot -File | Where-Object { $_.Extension -in @(".exe", ".pdb", ".dll") } |
    Copy-Item -Destination $target -Force
# Copy the pinned tools directly into the installation target as well. This
# explicit step keeps an existing AgentUpdate directory from retaining a
# stale service payload when the publish folder was prepared by another run.
foreach ($name in @("ffmpeg.exe", "ffprobe.exe")) {
    Copy-Item -LiteralPath (Join-Path $ffmpegSourceRoot $name) -Destination (Join-Path $target $name) -Force
}
Copy-Item -LiteralPath $ffmpegManifestPath -Destination (Join-Path $target "ffmpeg-manifest.json") -Force
foreach ($name in @("ffmpeg.exe", "ffprobe.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $target $name) -PathType Leaf)) {
        throw "FFMPEG_TARGET_COPY_FAILED: $(Join-Path $target $name)"
    }
}
$sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
& $installer -ServiceDirectory $target -AllowedUserSid $sid
if ($LASTEXITCODE -ne 0) { throw "RECORDER_SERVICE_INSTALL_FAILED" }
$service = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction Stop
Write-Host "WhisperXAtomRecorder: $($service.Status)"

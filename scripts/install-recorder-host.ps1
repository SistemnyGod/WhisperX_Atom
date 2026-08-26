[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$target = "C:\ProgramData\WhisperXAtom\RecorderHost"
$publishRoot = Join-Path $repo "artifacts\recorder-host-current"
$project = Join-Path $repo "apps\recorder-host\WhisperX.Atom.Recorder.Host.csproj"
$sourceExe = Join-Path $publishRoot "WhisperX.Atom.Recorder.Host.exe"
$ffmpegRoot = if (-not [string]::IsNullOrWhiteSpace($env:WHISPERX_FFMPEG_DIR)) { $env:WHISPERX_FFMPEG_DIR } else { Join-Path $repo "vendor\ffmpeg\win-x64" }

$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"{0}"' -f $PSCommandPath))
    if ($NoBuild) { $arguments += "-NoBuild" }
    try {
        $elevated = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $arguments -Wait -PassThru
        exit $elevated.ExitCode
    }
    catch { throw "ELEVATION_REQUIRED: UAC elevation was cancelled or unavailable." }
}

if (-not $NoBuild -or -not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -o $publishRoot --no-restore
    if ($LASTEXITCODE -ne 0) { throw "RECORDER_HOST_PUBLISH_FAILED" }
}
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "RECORDER_HOST_BINARY_NOT_FOUND: $sourceExe" }

$sourceFfmpeg = Join-Path $ffmpegRoot "ffmpeg.exe"
$sourceFfprobe = Join-Path $ffmpegRoot "ffprobe.exe"
if (-not (Test-Path -LiteralPath $sourceFfmpeg -PathType Leaf) -or -not (Test-Path -LiteralPath $sourceFfprobe -PathType Leaf)) {
    throw "FFMPEG_UNAVAILABLE: pinned ffmpeg.exe and ffprobe.exe are required."
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
$userConfigDirectory = Join-Path (Join-Path $env:LOCALAPPDATA "WhisperXAtom") "Agent"
New-Item -ItemType Directory -Force -Path $userConfigDirectory | Out-Null
$userConfigPath = Join-Path $userConfigDirectory "agent-config.json"
if (-not (Test-Path -LiteralPath $userConfigPath -PathType Leaf)) {
    $hostConfig = [ordered]@{
        ServerUrl = ""
        AgentId = ""
        Token = ""
        Encrypted = $true
        InstallationId = [Guid]::NewGuid()
        RecordingProfile = "ROOM"
        AudioConfiguration = [ordered]@{
            audioConfigurationVersion = 2
            captureEngine = "AUDIOGRAPH"
            microphone = [ordered]@{ selectionMode = "DEFAULT"; deviceId = $null }
            systemAudio = [ordered]@{ selectionMode = "DEFAULT"; deviceId = $null }
            userReselectRequired = $false
        }
    } | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $userConfigPath -Value $hostConfig -Encoding utf8
}
Get-ChildItem -LiteralPath $publishRoot -File | Where-Object { $_.Extension -in @(".exe", ".pdb", ".dll") } | Copy-Item -Destination $target -Force
Copy-Item -LiteralPath $sourceFfmpeg -Destination (Join-Path $target "ffmpeg.exe") -Force
Copy-Item -LiteralPath $sourceFfprobe -Destination (Join-Path $target "ffprobe.exe") -Force
[Environment]::SetEnvironmentVariable("WHISPERX_RECORDER_HOST_EXE", (Join-Path $target "WhisperX.Atom.Recorder.Host.exe"), "Machine")
Write-Host "Installed Recorder Host to $target"

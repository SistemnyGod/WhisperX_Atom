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

if (-not $NoBuild -or -not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -o $publishRoot --no-restore
    if ($LASTEXITCODE -ne 0) { throw "RECORDER_PUBLISH_FAILED" }
}
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "RECORDER_BINARY_NOT_FOUND: $sourceExe" }

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
$sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
& $installer -ServiceDirectory $target -AllowedUserSid $sid
if ($LASTEXITCODE -ne 0) { throw "RECORDER_SERVICE_INSTALL_FAILED" }
$service = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction Stop
Write-Host "WhisperXAtomRecorder: $($service.Status)"

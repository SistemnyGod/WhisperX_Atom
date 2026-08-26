[CmdletBinding()]
param(
    [string]$ServiceName = "WhisperXAtomRecorder"
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $PSCommandPath,
        "-ServiceName", $ServiceName
    )
    try {
        $elevated = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $arguments -Wait -PassThru
        if ($elevated.ExitCode -ne 0) { throw "RECORDER_SERVICE_ELEVATED_START_FAILED: exit code $($elevated.ExitCode)" }
        exit 0
    }
    catch {
        throw "RECORDER_SERVICE_ELEVATION_REQUIRED: UAC elevation was cancelled or unavailable. $($_.Exception.Message)"
    }
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    $installer = Join-Path $PSScriptRoot "install-recorder-runtime.ps1"
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "RECORDER_SERVICE_NOT_FOUND: $ServiceName" }
    & $installer -NoBuild
    if ($LASTEXITCODE -ne 0) { throw "RECORDER_SERVICE_INSTALL_FAILED: exit code $LASTEXITCODE" }
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
}

if ($service.StartType -eq [System.ServiceProcess.ServiceStartMode]::Disabled) {
    # The launcher may start the service on demand, but a reboot must not
    # revive a previously stopped recording pipeline implicitly.
    Set-Service -Name $ServiceName -StartupType Manual
}
if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
    Start-Service -Name $ServiceName
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
do {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
        Write-Host "$ServiceName is running"
        exit 0
    }
    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        throw "RECORDER_SERVICE_START_FAILED: service stopped during startup. Check Windows Event Viewer and Recorder logs."
    }
    Start-Sleep -Milliseconds 250
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw "RECORDER_SERVICE_START_TIMEOUT: $ServiceName did not become Running within 20 seconds."

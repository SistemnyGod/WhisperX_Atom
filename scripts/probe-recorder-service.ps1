#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateRange(1, 10)]
    [int]$Seconds = 3,
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$serviceName = "WhisperXAtomRecorder"
$installScript = Join-Path $repo "scripts\install-recorder-runtime.ps1"
$probeScript = Join-Path $repo "scripts\probe-audio-runtime.ps1"

$serviceConfig = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
if ($null -eq $serviceConfig) {
    throw "RECORDER_SERVICE_NOT_FOUND: $serviceName"
}

$previousStartMode = $serviceConfig.StartMode
$wasRunning = $serviceConfig.State -eq "Running"
$probeParameters = @{
    Seconds = $Seconds
    IncludeService = $true
}
if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $probeParameters.OutputRoot = $OutputRoot
}

try {
    Write-Host "Updating Recorder Service from the current workspace..."
    & $installScript -NoBuild
    if ($LASTEXITCODE -ne 0) {
        throw "RECORDER_INSTALL_FAILED: exit code $LASTEXITCODE"
    }

    Set-Service -Name $serviceName -StartupType Manual
    Start-Service -Name $serviceName -ErrorAction Stop
    (Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(15))

    Write-Host "Running controlled service audio probe..."
    & $probeScript @probeParameters
    if ($LASTEXITCODE -ne 0) {
        throw "RECORDER_AUDIO_PROBE_FAILED: exit code $LASTEXITCODE"
    }
}
finally {
    Write-Host "Restoring Recorder Service state..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue

    switch ($previousStartMode) {
        "Auto" { Set-Service -Name $serviceName -StartupType Automatic -ErrorAction SilentlyContinue }
        "Manual" { Set-Service -Name $serviceName -StartupType Manual -ErrorAction SilentlyContinue }
        "Disabled" { Set-Service -Name $serviceName -StartupType Disabled -ErrorAction SilentlyContinue }
        default { Set-Service -Name $serviceName -StartupType Manual -ErrorAction SilentlyContinue }
    }

    if ($wasRunning) {
        Start-Service -Name $serviceName -ErrorAction SilentlyContinue
    }
}

Write-Host "Probe artifacts are under artifacts\audio-runtime-probe."

[CmdletBinding()]
param(
    [string]$BundleRoot = "C:\Program Files\WhisperX Atom Server",
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [string]$TaskName = "WhisperX Atom Server Runtime",
    [switch]$ElevatedRelaunch
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$supervisor = Join-Path $bundle "supervise-server-runtime.ps1"
$manifest = Join-Path $bundle "release-manifest.json"
$envFile = Join-Path ([IO.Path]::GetFullPath($ConfigRoot)) ".env.lan"
if (-not (Test-Path -LiteralPath $supervisor -PathType Leaf)) { throw "SERVER_SUPERVISOR_MISSING: $supervisor" }
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "SERVER_MANIFEST_REQUIRED: $manifest" }
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
$manifestData = Get-Content -LiteralPath $manifest -Raw -Encoding utf8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$manifestData.buildIdentity) -or [string]$manifestData.buildIdentity -match '(?i)dev|dirty') { throw "SERVER_MANIFEST_IDENTITY_INVALID" }
& (Join-Path $PSScriptRoot "ensure-supervisor-health-token.ps1") -EnvFile $envFile

# Register-ScheduledTask writes the machine-wide Task Scheduler database. A
# normal interactive launch is supported: when the current account belongs to
# Administrators but the PowerShell token is not elevated, relaunch this same
# script through UAC and wait for its exit code. The elevated process still
# resolves WindowsIdentity from the same user, so the task remains
# Interactive/Limited and never runs as SYSTEM.
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
$isElevated = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$isAdminMember = $currentIdentity.Groups | Where-Object { $_.Value -eq 'S-1-5-32-544' }
if (-not $ElevatedRelaunch -and $isAdminMember -and -not $isElevated) {
    $powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
    $relaunchArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-BundleRoot', $BundleRoot, '-ConfigRoot', $ConfigRoot,
        '-TaskName', $TaskName, '-ElevatedRelaunch'
    )
    try {
        $child = Start-Process -FilePath $powershell -Verb RunAs -ArgumentList $relaunchArgs -Wait -PassThru
    }
    catch {
        throw 'SERVER_STARTUP_TASK_ELEVATION_CANCELLED: approve the UAC prompt or run this installer from an elevated Administrator PowerShell'
    }
    if ($child.ExitCode -ne 0) { throw "SERVER_STARTUP_TASK_ELEVATED_INSTALL_FAILED: exit=$($child.ExitCode)" }
    exit 0
}
$dockerCandidates = @(
    (Join-Path ${env:ProgramFiles} "Docker\Docker\Docker Desktop.exe"),
    (Join-Path ${env:LocalAppData} "Docker\Docker Desktop.exe")
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
if ($dockerCandidates.Count -eq 0) { throw "DOCKER_DESKTOP_NOT_FOUND" }
$powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
$arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$supervisor`" -BundleRoot `"$bundle`" -ConfigRoot `"$ConfigRoot`""
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $bundle
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -RestartCount 20 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
# Windows PowerShell exposes the scheduled-task enum as `Interactive` (the
# `InteractiveToken` name is used by a few newer APIs but is not accepted by
# New-ScheduledTaskPrincipal on the supported Windows runtime).  Interactive
# still means the current ordinary user's logon session; it does not elevate
# the supervisor or run it as SYSTEM.
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
try {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
}
catch {
    # Task registration changes the machine-wide Task Scheduler database even
    # though the task itself is deliberately owned by the ordinary server
    # user.  Surface a stable, actionable error instead of leaking the raw
    # COM exception (typically 0x80070005) into the release gate.
    $message = if ($_.Exception -and $_.Exception.Message) { $_.Exception.Message } else { "TASK_SCHEDULER_REGISTRATION_FAILED" }
    if ($message -match '(?i)access is denied|0x80070005|unauthorized') {
        throw "SERVER_STARTUP_TASK_REGISTRATION_DENIED: run this installer from an elevated Administrator PowerShell; task owner remains $($principal.UserId)"
    }
    throw "SERVER_STARTUP_TASK_REGISTRATION_FAILED: $message"
}
$registered = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $registered) { throw "SERVER_STARTUP_TASK_NOT_REGISTERED" }
if ($registered.Principal.UserId -ne $principal.UserId -or $registered.Settings.MultipleInstances -ne "IgnoreNew") { throw "SERVER_STARTUP_TASK_CONFIGURATION_INVALID" }
Start-ScheduledTask -TaskName $TaskName
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
do {
    Start-Sleep -Seconds 2
    $state = (Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop).State
    if ($state -eq "Running") { break }
} while ([DateTimeOffset]::UtcNow -lt $deadline)
if ($state -ne "Running") { throw "SERVER_STARTUP_TASK_NOT_STARTED" }
Write-Host "SERVER_STARTUP_TASK_READY=$TaskName"
Write-Host "SERVER_STARTUP_TASK_USER=$($principal.UserId)"

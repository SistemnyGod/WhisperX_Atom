[CmdletBinding()]
param(
    [string]$EnvFile = "",
    [string]$TaskName = "WhisperX Atom LAN Server"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repo ".env.lan" }
$powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
$start = Join-Path $PSScriptRoot "start-whisperx-lan-server.ps1"
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$start`" -EnvFile `"$EnvFile`""
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $repo
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -RunLevel Limited -Force | Out-Null
Write-Host "LAN startup task installed: $TaskName" -ForegroundColor Green


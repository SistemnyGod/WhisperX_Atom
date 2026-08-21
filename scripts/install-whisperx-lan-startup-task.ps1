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
$envValues = Get-Content -LiteralPath $EnvFile -Encoding utf8
$autoSummary = (($envValues | Where-Object { $_ -match '^AUTO_SUMMARY_ENABLED=' } | Select-Object -First 1) -replace '^AUTO_SUMMARY_ENABLED=', '').Trim()
$assistantEnabled = (($envValues | Where-Object { $_ -match '^ASSISTANT_ENABLED=' } | Select-Object -First 1) -replace '^ASSISTANT_ENABLED=', '').Trim()
$flags = @()
if ($autoSummary -eq 'true') { $flags += '-EnableQwen' }
if ($assistantEnabled -ne 'false') { $flags += '-EnableAssistant' }
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$start`" -EnvFile `"$EnvFile`" $($flags -join ' ')"
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $repo
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable
try {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -RunLevel Limited -Force -ErrorAction Stop | Out-Null
    $registered = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    if ($null -eq $registered) { throw "SCHEDULED_TASK_NOT_FOUND_AFTER_REGISTER" }
    Start-ScheduledTask -TaskName $TaskName
    Write-Host "LAN startup task installed: $TaskName" -ForegroundColor Green
} catch {
    throw "LAN_STARTUP_TASK_INSTALL_FAILED: $($_.Exception.Message)"
}

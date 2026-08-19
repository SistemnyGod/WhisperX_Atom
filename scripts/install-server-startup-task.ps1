[CmdletBinding()]
param(
    [string]$BundleRoot = "C:\Program Files\WhisperX Atom Server",
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [string]$TaskName = "WhisperX Atom Server Runtime"
)

$ErrorActionPreference = "Stop"
$start = Join-Path ([IO.Path]::GetFullPath($BundleRoot)) "start-server-bundle.ps1"
$envFile = Join-Path ([IO.Path]::GetFullPath($ConfigRoot)) ".env.lan"
if (-not (Test-Path -LiteralPath $start -PathType Leaf)) { throw "SERVER_START_SCRIPT_MISSING: $start" }
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
$powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$start`" -BundleRoot `"$BundleRoot`" -ConfigRoot `"$ConfigRoot`""
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $BundleRoot
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
if ($null -eq (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) { throw "SERVER_STARTUP_TASK_NOT_REGISTERED" }
Write-Host "SERVER_STARTUP_TASK_READY=$TaskName"

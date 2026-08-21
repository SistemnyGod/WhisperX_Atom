[CmdletBinding()]
param(
    [string]$BundleRoot = "C:\Program Files\WhisperX Atom Server",
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [string]$TaskName = "WhisperX Atom Server Runtime"
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$supervisor = Join-Path $bundle "supervise-server-runtime.ps1"
$manifest = Join-Path $bundle "release-manifest.json"
$envFile = Join-Path ([IO.Path]::GetFullPath($ConfigRoot)) ".env.lan"
if (-not (Test-Path -LiteralPath $supervisor -PathType Leaf)) { throw "SERVER_SUPERVISOR_MISSING: $supervisor" }
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "SERVER_MANIFEST_REQUIRED: $manifest" }
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
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
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType InteractiveToken -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
$registered = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $registered) { throw "SERVER_STARTUP_TASK_NOT_REGISTERED" }
if ($registered.Principal.UserId -ne $principal.UserId -or $registered.Settings.MultipleInstances -ne "IgnoreNew") { throw "SERVER_STARTUP_TASK_CONFIGURATION_INVALID" }
Write-Host "SERVER_STARTUP_TASK_READY=$TaskName"
Write-Host "SERVER_STARTUP_TASK_USER=$($principal.UserId)"

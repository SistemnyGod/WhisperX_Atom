param([string]$ServiceDirectory = (Join-Path $PSScriptRoot "Service"))
$ErrorActionPreference = "Stop"
$serviceName = "WhisperXAtomRecorder"
$displayName = "WhisperX Atom Recorder Service"
$serviceExe = Join-Path $ServiceDirectory "WhisperX.Atom.Recorder.Service.exe"
if (-not (Test-Path -LiteralPath $serviceExe)) { throw "Service binary not found: $serviceExe" }
$ffmpeg = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
if ($null -eq $ffmpeg) { throw "FFmpeg is required for FLAC chunk encoding. Install FFmpeg and rerun the installer." }
[Environment]::SetEnvironmentVariable("ATOM_AGENT_FFMPEG_PATH", $ffmpeg.Source, "Machine")
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.Status -ne "Stopped") { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}
New-Service -Name $serviceName -DisplayName $displayName -Description "Local-first WhisperX Atom recording service" -BinaryPathName "`"$serviceExe`"" -StartupType Automatic | Out-Null
Start-Service -Name $serviceName
Write-Host "Installed and started $displayName"

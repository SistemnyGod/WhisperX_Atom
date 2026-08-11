[CmdletBinding()]
param(
    [ValidateSet("host", "container")][string]$GpuMode = "host",
    [switch]$Rebuild,
    [switch]$SkipDesktop,
    [switch]$SkipRecorder
)

$ErrorActionPreference = "Stop"
$params = @{
    GpuMode = $GpuMode
    StartWatchdog = $true
    Rebuild = $Rebuild
    SkipDesktop = $SkipDesktop
    SkipRecorder = $SkipRecorder
}
& (Join-Path $PSScriptRoot "start-transcription-mvp.ps1") @params
if ($LASTEXITCODE -ne 0) { throw "WHISPERX_START_FAILED" }
& (Join-Path $PSScriptRoot "doctor-whisperx.ps1") -SkipRegistry:($GpuMode -eq "host")

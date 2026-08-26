[CmdletBinding()]
param([switch]$StopRecorder)

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "stop-transcription-mvp.ps1") -StopRecorder:$StopRecorder

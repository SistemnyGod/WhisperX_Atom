[CmdletBinding()]
param(
  [string]$AudioPath,
  [string]$InboxPath,
  [string]$BaseUrl = "http://192.168.2.194:8080",
  [string]$TusUrl = "http://localhost:1080",
  [int]$TimeoutSeconds = 3600,
  [switch]$Start,
  [switch]$RestartWorkers
)

$script = Join-Path $PSScriptRoot "e2e-core.ps1"
$params = @{
  BaseUrl = $BaseUrl
  TusUrl = $TusUrl
  TimeoutSeconds = $TimeoutSeconds
  WaitForGpu = $true
  WithGpu = $true
  WithLlm = $true
  RestartWorkers = $RestartWorkers
}
if ($AudioPath) { $params.AudioPath = $AudioPath }
if ($InboxPath) { $params.InboxPath = $InboxPath }
if ($Start) { $params.StartCore = $true }
& $script @params
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

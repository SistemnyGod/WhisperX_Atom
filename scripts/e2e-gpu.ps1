[CmdletBinding()]
param(
  [string]$AudioPath,
  [string]$InboxPath,
  [string]$BaseUrl = "http://localhost:8000",
  [int]$TimeoutSeconds = 3600,
  [switch]$Start
)

$script = Join-Path $PSScriptRoot "e2e-core.ps1"
$params = @{
  BaseUrl = $BaseUrl
  TimeoutSeconds = $TimeoutSeconds
  WaitForGpu = $true
  WithGpu = $true
}
if ($AudioPath) { $params.AudioPath = $AudioPath }
if ($InboxPath) { $params.InboxPath = $InboxPath }
if ($Start) { $params.StartCore = $true }
& $script @params
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
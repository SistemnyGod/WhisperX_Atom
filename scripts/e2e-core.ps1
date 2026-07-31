[CmdletBinding()]
param(
  [string]$BaseUrl = "http://localhost:8000",
  [string]$Username = $(if ($env:BOOTSTRAP_ADMIN_USERNAME) { $env:BOOTSTRAP_ADMIN_USERNAME } else { "admin" }),
  [string]$Password = $(if ($env:BOOTSTRAP_ADMIN_PASSWORD) { $env:BOOTSTRAP_ADMIN_PASSWORD } else { "change-me-now" }),
  [string]$AudioPath,
  [string]$InboxPath,
  [string]$InboxRoot = $(if ($env:WHISPERX_INBOX_HOST) { $env:WHISPERX_INBOX_HOST } else { "C:\WhisperXAtom\Inbox" }),
  [int]$TimeoutSeconds = 180,
  [switch]$StartCore
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd("/")
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
  $params = @{ Uri = "$BaseUrl$Path"; Method = $Method; WebSession = $session; ErrorAction = "Stop" }
  if ($null -ne $Body) {
    $params.ContentType = "application/json"
    $params.Body = ($Body | ConvertTo-Json -Depth 8 -Compress)
  }
  return Invoke-RestMethod @params
}

function Wait-Ready {
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    try {
      $ready = Invoke-RestMethod -Uri "$BaseUrl/ready" -TimeoutSec 5
      if ($ready.ready -eq $true) { return }
    } catch { }
    Start-Sleep -Seconds 2
  } while ((Get-Date) -lt $deadline)
  throw "API did not become ready within $TimeoutSeconds seconds"
}

function Wait-Job([string]$MeetingId) {
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $jobs = @(Invoke-Api GET "/api/meetings/$MeetingId/jobs")
    if ($jobs.Count -gt 0) {
      $job = $jobs[0]
      Write-Host ("job {0}: {1}/{2} {3}%" -f $job.id, $job.status, $job.stage, $job.progress)
      if ($job.status -in @("READY", "FAILED")) { return $job }
    }
    Start-Sleep -Seconds 2
  } while ((Get-Date) -lt $deadline)
  throw "Job did not finish within $TimeoutSeconds seconds"
}

if ($StartCore) {
  docker compose -f compose.dev.yml --profile core up -d
  if ($LASTEXITCODE -ne 0) { throw "Unable to start core Compose profile" }
}

Wait-Ready
$null = Invoke-Api POST "/api/auth/login" @{ username = $Username; password = $Password }
$meeting = $null

if ($AudioPath) {
  $meeting = Invoke-Api POST "/api/meetings" @{ title = "E2E $(Get-Date -Format s)"; description = "automated core smoke" }
  Write-Host "meeting: $($meeting.id)"
  $file = Get-Item -LiteralPath $AudioPath
  if (-not $file.Exists) { throw "Audio file not found: $AudioPath" }
  $reservation = Invoke-Api POST "/api/meetings/$($meeting.id)/uploads" @{ fileName = $file.Name; sizeBytes = $file.Length }
  $metadata = @(
    "filename $([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($file.Name)))",
    "reservationId $([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$reservation.uploadId)))",
    "filetype $([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("application/octet-stream")))"
  ) -join ","
  $createHeaders = @{ "Tus-Resumable" = "1.0.0"; "Upload-Length" = [string]$file.Length; "Upload-Metadata" = $metadata }
  $created = Invoke-WebRequest -Uri "$BaseUrl/files/" -Method Post -Headers $createHeaders -WebSession $session
  $location = $created.Headers["Location"]
  if ([string]::IsNullOrWhiteSpace($location)) { throw "tusd did not return Location" }
  if ($location -notmatch '^https?://') { $location = "$BaseUrl$location" }
  $patchHeaders = @{ "Tus-Resumable" = "1.0.0"; "Upload-Offset" = "0"; "Content-Type" = "application/offset+octet-stream" }
  Invoke-WebRequest -Uri $location -Method Patch -Headers $patchHeaders -Body ([IO.File]::ReadAllBytes($file.FullName)) -WebSession $session | Out-Null
  Write-Host "tus upload completed: $($file.Name)"
  $job = Wait-Job $meeting.id
  if ($job.status -eq "FAILED") { throw "tus job failed: $($job.error)" }
}

if ($InboxPath) {
  $inbox = Get-Item -LiteralPath $InboxPath
  if (-not $inbox.Exists) { throw "Inbox file not found: $InboxPath" }
  New-Item -ItemType Directory -Force -Path $InboxRoot | Out-Null
  $target = Join-Path $InboxRoot $inbox.Name
  Copy-Item -LiteralPath $inbox.FullName -Destination $target -Force
  Write-Host "copied to hot-folder: $target"
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $meeting = @(Invoke-Api GET "/api/meetings?limit=200") | Where-Object { $_.title -eq [IO.Path]::GetFileNameWithoutExtension($inbox.Name) } | Select-Object -First 1
    if ($meeting) { break }
    Start-Sleep -Seconds 2
  } while ((Get-Date) -lt $deadline)
  if (-not $meeting) { throw "Hot-folder importer did not register $($inbox.Name)" }
  Write-Host "import meeting: $($meeting.id)"
  $job = Wait-Job $meeting.id
  if ($job.status -eq "FAILED") { throw "hot-folder job failed: $($job.error)" }
}

if ($meeting) {
  $transcript = Invoke-Api GET "/api/meetings/$($meeting.id)/transcript"
  Write-Host ("transcript status: {0}; segments: {1}" -f $transcript.status, @($transcript.segments).Count)
}
Write-Host "Core E2E smoke completed."

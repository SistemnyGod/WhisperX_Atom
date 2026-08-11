[CmdletBinding()]
param(
  [string]$BaseUrl = "http://localhost:8080",
  [string]$TusUrl = $(if ($env:WHISPERX_TUS_URL) { $env:WHISPERX_TUS_URL } else { "http://localhost:1080" }),
  [string]$Username = $(if ($env:BOOTSTRAP_ADMIN_USERNAME) { $env:BOOTSTRAP_ADMIN_USERNAME } else { "admin" }),
  [string]$Password = $(if ($env:BOOTSTRAP_ADMIN_PASSWORD) { $env:BOOTSTRAP_ADMIN_PASSWORD } else { "" }),
  [string]$AudioPath,
  [string]$InboxPath,
  [string]$InboxRoot = $(if ($env:WHISPERX_INBOX_HOST) { $env:WHISPERX_INBOX_HOST } else { "C:\WhisperXAtom\Inbox" }),
  [int]$TimeoutSeconds = 180,
  [switch]$StartCore,
  [switch]$WithGpu,
  [switch]$WithLlm,
  [switch]$WaitForGpu,
  [switch]$RestartWorkers
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Password)) { throw "Set BOOTSTRAP_ADMIN_PASSWORD before running e2e-core.ps1" }
$BaseUrl = $BaseUrl.TrimEnd("/")
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
  $params = @{ Uri = "$BaseUrl$Path"; Method = $Method; WebSession = $session; ErrorAction = "Stop" }
  if ($null -ne $Body) {
    $params.ContentType = "application/json"
    $params.Body = ($Body | ConvertTo-Json -Depth 8 -Compress)
  }
  $response = Invoke-WebRequest @params
  if ([string]::IsNullOrWhiteSpace($response.Content)) { return $null }
  $parsed = $response.Content | ConvertFrom-Json
  if ($parsed -is [System.Array]) {
    foreach ($item in $parsed) { Write-Output $item }
  } else {
    Write-Output $parsed
  }
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
    $job = $jobs | Where-Object { $_.type -eq "TRANSCRIBE" } | Select-Object -First 1
    if ($null -ne $job) {
      Write-Host ("job {0}: {1}/{2} {3}%" -f $job.id, $job.status, $job.stage, $job.progress)
      $terminal = if ($WaitForGpu) { @("READY", "FAILED") } else { @("QUEUED", "READY", "FAILED") }
      if ($job.status -in $terminal) { return $job }
    }
    Start-Sleep -Seconds 2
  } while ((Get-Date) -lt $deadline)
  throw "Job did not finish within $TimeoutSeconds seconds"
}

function Restart-ProcessingWorkers {
  if (-not $RestartWorkers) { return }
  $composeArgs = @("compose", "-f", "compose.dev.yml", "--profile", "core")
  $services = @("media-worker")
  if ($WaitForGpu) {
    $composeArgs += @("--profile", "gpu")
    $services += "gpu-worker"
  }
  if ($WithLlm) {
    $composeArgs += @("--profile", "llm")
    $services += "summary-worker"
  }
  $composeArgs += @("restart") + $services
  Write-Host ("restarting workers: {0}" -f ($services -join ", "))
  & docker @composeArgs
  if ($LASTEXITCODE -ne 0) { throw "Unable to restart processing workers" }
}

if ($StartCore) {
  $composeArgs = @("compose", "-f", "compose.dev.yml", "--profile", "core")
  if ($WithGpu) { $composeArgs += "--profile"; $composeArgs += "gpu" }
  if ($WithLlm) { $composeArgs += "--profile"; $composeArgs += "llm" }
  $composeArgs += @("up", "-d")
  & docker @composeArgs
  if ($LASTEXITCODE -ne 0) { throw "Unable to start Compose profile" }
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
  $created = Invoke-WebRequest -Uri "$TusUrl/files/" -Method Post -Headers $createHeaders -WebSession $session
  $location = $created.Headers["Location"]
  if ([string]::IsNullOrWhiteSpace($location)) { throw "tusd did not return Location" }
  if ($location -notmatch '^https?://') { $location = "$TusUrl$location" }
  $curlArgs = @("--fail-with-body", "--silent", "--show-error", "--request", "PATCH", $location, "--header", "Tus-Resumable: 1.0.0", "--header", "Upload-Offset: 0", "--header", "Content-Type: application/offset+octet-stream", "--data-binary", "@$($file.FullName)")
  & curl.exe @curlArgs | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "tus PATCH failed for $($file.Name)" }
  Write-Host "tus upload completed: $($file.Name)"
  Restart-ProcessingWorkers
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
  Restart-ProcessingWorkers
  $job = Wait-Job $meeting.id
  if ($job.status -eq "FAILED") { throw "hot-folder job failed: $($job.error)" }
}

if ($meeting -and $WaitForGpu) {
  $transcript = Invoke-Api GET "/api/meetings/$($meeting.id)/transcript"
  Write-Host ("transcript status: {0}; segments: {1}" -f $transcript.status, @($transcript.segments).Count)
  if ($transcript.status -ne "READY" -or @($transcript.segments).Count -eq 0) { throw "GPU E2E produced no ready transcript" }
}
Write-Host "E2E smoke completed."

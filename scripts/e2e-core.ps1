[CmdletBinding()]
param(
  [string]$BaseUrl,
  [string]$TusUrl,
  [string]$Username,
  [string]$Password,
  [string]$AudioPath,
  [string]$InboxPath,
  [string]$InboxRoot,
  [int]$TimeoutSeconds = 180,
  [string]$ResultPath,
  [switch]$StartCore,
  [switch]$WithGpu,
  [switch]$WithLlm,
  [switch]$WaitForGpu,
  [switch]$RestartWorkers,
  [string]$LocalArchivePath,
  [switch]$DeliveryConfirmed,
  [switch]$MediaReady
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo
if ([string]::IsNullOrWhiteSpace($BaseUrl)) { $BaseUrl = if ($env:WHISPERX_API_URL) { $env:WHISPERX_API_URL } else { "http://192.168.2.194:8080" } }
if ([string]::IsNullOrWhiteSpace($TusUrl)) { $TusUrl = if ($env:WHISPERX_TUS_URL) { $env:WHISPERX_TUS_URL } else { "http://localhost:1080" } }
if ([string]::IsNullOrWhiteSpace($Username)) { $Username = if ($env:BOOTSTRAP_ADMIN_USERNAME) { $env:BOOTSTRAP_ADMIN_USERNAME } else { "admin" } }
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = $env:BOOTSTRAP_ADMIN_PASSWORD }
if ([string]::IsNullOrWhiteSpace($InboxRoot)) { $InboxRoot = if ($env:WHISPERX_INBOX_HOST) { $env:WHISPERX_INBOX_HOST } else { "C:\WhisperXAtom\Inbox" } }
if ([string]::IsNullOrWhiteSpace($Password)) { throw "Set BOOTSTRAP_ADMIN_PASSWORD before running e2e-core.ps1" }
Add-Type -AssemblyName System.Net.Http
$BaseUrl = $BaseUrl.TrimEnd("/")
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$runId = [guid]::NewGuid().ToString("N")
$runStartedAt = [DateTimeOffset]::UtcNow

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

function Upload-TusResumable([string]$Location, [string]$FilePath, [int64]$Length) {
  $chunkSize = 16MB
  $maxRetries = 5
  $offset = 0L
  $client = [System.Net.Http.HttpClient]::new()
  try {
    while ($offset -lt $Length) {
      $head = Invoke-WebRequest -Uri $Location -Method Head -Headers @{ "Tus-Resumable" = "1.0.0" } -WebSession $session
      $headerOffset = [int64]$head.Headers["Upload-Offset"]
      if ($headerOffset -lt 0 -or $headerOffset -gt $Length) { throw "TUS_INVALID_OFFSET: $headerOffset" }
      $offset = $headerOffset
      if ($offset -ge $Length) { break }

      $remaining = $Length - $offset
      $count = [int][Math]::Min($chunkSize, $remaining)
      $buffer = New-Object byte[] $count
      $stream = [System.IO.File]::OpenRead($FilePath)
      try {
        $stream.Seek($offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $read = 0
        while ($read -lt $count) {
          $n = $stream.Read($buffer, $read, $count - $read)
          if ($n -le 0) { throw "TUS_FILE_READ_FAILED: offset=$offset" }
          $read += $n
        }
      }
      finally { $stream.Dispose() }

      $attempt = 0
      $sent = $false
      while (-not $sent) {
        $request = $null
        $content = $null
        $response = $null
        try {
          $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new("PATCH"), [Uri]$Location)
          $request.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0") | Out-Null
          $request.Headers.TryAddWithoutValidation("Upload-Offset", [string]$offset) | Out-Null
          $content = [System.Net.Http.ByteArrayContent]::new($buffer)
          $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/offset+octet-stream")
          $request.Content = $content
          $response = $client.SendAsync($request).GetAwaiter().GetResult()
          if (-not $response.IsSuccessStatusCode) { throw "HTTP $([int]$response.StatusCode)" }
          $responseOffset = $offset + $count
          try {
            $responseHeaderOffset = $response.Headers.GetValues("Upload-Offset") | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace([string]$responseHeaderOffset)) {
              $responseOffset = [int64]$responseHeaderOffset
            }
          }
          catch { }
          if ($responseOffset -le $offset -or $responseOffset -gt $Length) {
            throw "TUS_INVALID_RESPONSE_OFFSET: $responseOffset"
          }
          $sent = $true
          $offset = $responseOffset
          Write-Host ("tus chunk uploaded: {0}/{1} MiB" -f [Math]::Floor($offset / 1MB), [Math]::Ceiling($Length / 1MB))
        }
        catch {
          try {
            $headAfterFailure = Invoke-WebRequest -Uri $Location -Method Head -Headers @{ "Tus-Resumable" = "1.0.0" } -WebSession $session
            $serverOffsetAfterFailure = [int64]$headAfterFailure.Headers["Upload-Offset"]
            if ($serverOffsetAfterFailure -gt $offset -and $serverOffsetAfterFailure -le $Length) {
              $offset = $serverOffsetAfterFailure
              $sent = $true
              Write-Host ("tus chunk already accepted: {0}/{1} MiB" -f [Math]::Floor($offset / 1MB), [Math]::Ceiling($Length / 1MB))
              continue
            }
          }
          catch { }
          $attempt++
          if ($attempt -gt $maxRetries) { throw "TUS_UPLOAD_FAILED: offset=$offset; $($_.Exception.Message)" }
          Start-Sleep -Seconds ([Math]::Min(30, [Math]::Pow(2, $attempt)))
        }
        finally {
          if ($request) { $request.Dispose() }
          if ($content) { $content.Dispose() }
          if ($response) { $response.Dispose() }
        }
      }
    }
  }
  finally { $client.Dispose() }
}

function Restart-ProcessingWorkers {
  if (-not $RestartWorkers) { return }
  $composeArgs = @("compose", "-f", "compose.dev.yml", "--profile", "core")
  $services = @("media-worker")
  if ($WaitForGpu) {
    if ($env:GPU_WORKER_MODE -eq "host") {
      & (Join-Path $PSScriptRoot "stop-host-gpu-worker.ps1")
      & (Join-Path $PSScriptRoot "start-host-gpu-worker.ps1") -PythonPath $env:WHISPERX_HOST_PYTHON
      if ($LASTEXITCODE -ne 0) { throw "Unable to restart host GPU worker" }
    } else {
      $composeArgs += @("--profile", "gpu")
      $services += "gpu-worker"
    }
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
  $meeting = Invoke-Api POST "/api/meetings" @{ title = "E2E $runId"; description = "automated transcript acceptance run $runId" }
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
  Upload-TusResumable -Location $location -FilePath $file.FullName -Length $file.Length
  Write-Host "tus upload completed: $($file.Name)"
  Restart-ProcessingWorkers
  $job = Wait-Job $meeting.id
  if ($job.status -eq "FAILED") { throw "tus job failed: $($job.error)" }
}

if ($InboxPath) {
  $inbox = Get-Item -LiteralPath $InboxPath
  if (-not $inbox.Exists) { throw "Inbox file not found: $InboxPath" }
  New-Item -ItemType Directory -Force -Path $InboxRoot | Out-Null
  $targetName = "{0}-{1}{2}" -f $inbox.BaseName, $runId, $inbox.Extension
  $target = Join-Path $InboxRoot $targetName
  Copy-Item -LiteralPath $inbox.FullName -Destination $target -Force
  Write-Host "copied to hot-folder: $target"
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $meeting = @(Invoke-Api GET "/api/meetings?limit=200") | Where-Object {
      $_.title -eq [IO.Path]::GetFileNameWithoutExtension($targetName) -and
      ([DateTimeOffset]$_.createdAt) -ge $runStartedAt
    } | Select-Object -First 1
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
  if ($transcript.status -notin @("READY", "PARTIAL_READY") -or @($transcript.segments).Count -eq 0) { throw "GPU E2E produced no ready transcript" }
}

if ($meeting -and $ResultPath) {
  $media = @(Invoke-Api GET "/api/meetings/$($meeting.id)/media")
  $jobs = @(Invoke-Api GET "/api/meetings/$($meeting.id)/jobs")
  $traceJob = $jobs | Where-Object {
    $_.PSObject.Properties.Name -contains "traceId" -and -not [string]::IsNullOrWhiteSpace([string]$_.traceId)
  } | Select-Object -First 1
  $summary = $null
  try { $summary = Invoke-Api GET "/api/meetings/$($meeting.id)/summary" } catch { }
    $result = [ordered]@{
    runId = $runId
    startedAtUtc = $runStartedAt.ToString("o")
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    meetingId = [string]$meeting.id
    mediaAssetIds = @($media | ForEach-Object { [string]$_.id })
    jobIds = @($jobs | ForEach-Object { [string]$_.id })
    transcriptId = if ($transcript) { [string]$transcript.id } else { $null }
    traceId = if ($traceJob) { [string]$traceJob.traceId } else { $null }
    transcriptStatus = if ($transcript) { [string]$transcript.status } else { $null }
    qualityScore = if ($transcript) { $transcript.qualityScore } else { $null }
    qualityWarnings = if ($transcript) { @($transcript.qualityWarnings) } else { @() }
    transcriptSegmentCount = if ($transcript) { @($transcript.segments).Count } else { 0 }
    summaryId = if ($summary) { [string]$summary.id } else { $null }
    summaryStatus = if ($summary) { [string]$summary.status } else { $null }
    localArchiveReady = if ($LocalArchivePath) { Test-Path -LiteralPath $LocalArchivePath -PathType Leaf } else { $false }
    deliveryConfirmed = [bool]$DeliveryConfirmed
    mediaReady = [bool]$MediaReady
  }
  $parent = Split-Path -Parent $ResultPath
  if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
  $result | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 -LiteralPath $ResultPath
}
Write-Host "E2E smoke completed."

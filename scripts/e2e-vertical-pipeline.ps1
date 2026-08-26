[CmdletBinding()]
param(
  [ValidateRange(10,7200)][int]$Seconds = 30,
  [ValidateRange(30,1800)][int]$FinalizeTimeoutSeconds = 180,
  [string]$DeviceId = "",
  [string]$BaseUrl,
  [string]$Username,
  [string]$Password,
  [string]$ResultPath,
  [switch]$DevelopmentHost,
  [switch]$RestartWorkers,
  [ValidateSet("host", "container")][string]$GpuMode = "",
  [string]$DevelopmentDataRoot,
  [string]$InstalledHostPath = "C:\Program Files\WhisperX Atom\RecorderHost\WhisperX.Atom.Recorder.Host.exe"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-vertical-" + [guid]::NewGuid().ToString("N"))
$recordingRoot = Join-Path $runRoot "recorder"
if ([string]::IsNullOrWhiteSpace($ResultPath)) { $ResultPath = Join-Path $repo "artifacts\acceptance\voice-to-transcript-v1.json" }

try {
  New-Item -ItemType Directory -Force -Path $recordingRoot | Out-Null
  $recordingArgs = @{
    Seconds = $Seconds
    FinalizeTimeoutSeconds = $FinalizeTimeoutSeconds
    ServerDelivery = $true
    OutputRoot = $recordingRoot
    InstalledHostPath = $InstalledHostPath
  }
  if ($DeviceId) { $recordingArgs.DeviceId = $DeviceId }
  if ($DevelopmentHost) { $recordingArgs.DevelopmentHost = $true }
  if ($DevelopmentDataRoot) { $recordingArgs.DevelopmentDataRoot = $DevelopmentDataRoot }
  & (Join-Path $PSScriptRoot "acceptance-audiograph-local-recording.ps1") @recordingArgs
  if ($LASTEXITCODE -ne 0) { throw "VERTICAL_RECORDER_E2E_FAILED:$LASTEXITCODE" }
  $reportFile = Get-ChildItem -LiteralPath $recordingRoot -Filter "report-*.json" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($null -eq $reportFile) { throw "VERTICAL_RECORDER_REPORT_MISSING" }
  $recorder = Get-Content -Raw -LiteralPath $reportFile.FullName | ConvertFrom-Json
  $meetingId = [string]$recorder.result.meetingId
  if ([string]::IsNullOrWhiteSpace($meetingId)) { throw "VERTICAL_MEETING_ID_MISSING_FROM_RECORDER" }

  # Exercise recovery only when explicitly requested.  The default gate is
  # non-destructive and never mutates the running server runtime.
  if ($RestartWorkers) {
    $compose = Join-Path $repo "compose.dev.yml"
    if (-not (Test-Path -LiteralPath $compose -PathType Leaf)) { throw "VERTICAL_COMPOSE_FILE_MISSING" }
    $effectiveGpuMode = if ($GpuMode) { $GpuMode.ToLowerInvariant() } elseif ($env:GPU_WORKER_MODE) { $env:GPU_WORKER_MODE.ToLowerInvariant() } else { "container" }
    if ($effectiveGpuMode -eq "host") {
      & (Join-Path $PSScriptRoot "stop-host-gpu-worker.ps1")
      if ($LASTEXITCODE -ne 0) { throw "VERTICAL_HOST_GPU_STOP_FAILED:$LASTEXITCODE" }
      & docker compose -f $compose --profile core --profile llm --profile memory restart media-worker summary-worker memory-worker
      if ($LASTEXITCODE -ne 0) { throw "VERTICAL_WORKER_RESTART_FAILED:$LASTEXITCODE" }
      & (Join-Path $PSScriptRoot "start-host-gpu-worker.ps1")
      if ($LASTEXITCODE -ne 0) { throw "VERTICAL_HOST_GPU_START_FAILED:$LASTEXITCODE" }
    } else {
      & docker compose -f $compose --profile core --profile gpu --profile llm --profile memory restart media-worker gpu-worker summary-worker memory-worker
      if ($LASTEXITCODE -ne 0) { throw "VERTICAL_WORKER_RESTART_FAILED:$LASTEXITCODE" }
    }
  }

  $url = if ($BaseUrl) { $BaseUrl.TrimEnd('/') } elseif ($env:WHISPERX_DEV_API_URL) { $env:WHISPERX_DEV_API_URL.TrimEnd('/') } else { "http://127.0.0.1:8080" }
  $user = if ($Username) { $Username } elseif ($env:BOOTSTRAP_ADMIN_USERNAME) { $env:BOOTSTRAP_ADMIN_USERNAME } else { "admin" }
  $pass = if ($Password) { $Password } else { $env:BOOTSTRAP_ADMIN_PASSWORD }
  if ([string]::IsNullOrWhiteSpace($pass)) { throw "VERTICAL_AUTH_PASSWORD_REQUIRED" }
  $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  Invoke-WebRequest -Uri "$url/api/auth/login" -Method Post -ContentType "application/json" -Body (@{ username = $user; password = $pass } | ConvertTo-Json -Compress) -WebSession $session | Out-Null

  $deadline = (Get-Date).AddSeconds($FinalizeTimeoutSeconds)
  $chain = $null
  $v1ReadyAtUtc = $null
  $mediaReadyAtUtc = $null
  $enrichmentReadyAtUtc = $null
  $summaryReadyAtUtc = $null
  do {
    try {
      $chains = @(Invoke-RestMethod -Uri "$url/api/meetings/$meetingId/pipeline" -WebSession $session -TimeoutSec 10)
      if ($chains.Count -gt 0) { $chain = $chains[$chains.Count - 1] }
      if ($chain) {
        if (-not $mediaReadyAtUtc -and [string]$chain.mediaStatus -in @("READY", "CONFIRMED")) { $mediaReadyAtUtc = [DateTimeOffset]::UtcNow.ToString("o") }
        if (-not $v1ReadyAtUtc -and [string]$chain.transcriptV1Id -and [string]$chain.transcriptV1Status -in @("READY", "PARTIAL_READY")) { $v1ReadyAtUtc = [DateTimeOffset]::UtcNow.ToString("o") }
        if (-not $enrichmentReadyAtUtc -and [string]$chain.transcriptV2Id -and [string]$chain.transcriptV2Status -in @("READY", "PARTIAL_READY")) { $enrichmentReadyAtUtc = [DateTimeOffset]::UtcNow.ToString("o") }
        if (-not $summaryReadyAtUtc -and [string]$chain.summaryId -and [string]$chain.summaryStatus -in @("READY", "NEEDS_REVIEW")) { $summaryReadyAtUtc = [DateTimeOffset]::UtcNow.ToString("o") }
      }
      if ($chain -and $chain.transcriptV2Id -and $chain.summaryId -and $chain.summaryStatus -in @("READY","NEEDS_REVIEW")) { break }
    } catch { }
    Start-Sleep -Seconds 3
  } while ((Get-Date) -lt $deadline)
  if (-not $chain) { throw "VERTICAL_PIPELINE_LINEAGE_MISSING" }
  $required = @("recordingSessionId","meetingId","mediaAssetId","asrJobId","transcriptV1Id","enrichmentJobId","transcriptV2Id","summaryJobId","summaryId")
  foreach ($field in $required) { if ([string]::IsNullOrWhiteSpace([string]$chain.$field)) { throw "VERTICAL_LINEAGE_FIELD_MISSING:$field" } }
  # localSessionId is the Recorder Host identifier; recordingSessionId is the
  # server-side row created during finalize.  The report carries both and the
  # server id must be compared with result.serverSessionId, not localSessionId.
  $serverSessionId = [string]$recorder.result.serverSessionId
  if ([string]::IsNullOrWhiteSpace($serverSessionId)) { throw "VERTICAL_SERVER_SESSION_ID_MISSING_FROM_RECORDER" }
  if ([string]$chain.meetingId -ne $meetingId -or [string]$chain.recordingSessionId -ne $serverSessionId) { throw "VERTICAL_SCOPE_OR_SESSION_MISMATCH" }

  $jobs = @(Invoke-RestMethod -Uri "$url/api/meetings/$meetingId/jobs" -WebSession $session -TimeoutSec 10)
  $versions = @(Invoke-RestMethod -Uri "$url/api/meetings/$meetingId/transcript/versions" -WebSession $session -TimeoutSec 10)
  $asrCount = @($jobs | Where-Object { $_.type -in @("TRANSCRIBE_ASR", "TRANSCRIBE") }).Count
  $enrichmentCount = @($jobs | Where-Object { $_.type -eq "TRANSCRIPT_ENRICH" }).Count
  $summaryCount = @($jobs | Where-Object { $_.type -eq "SUMMARIZE" }).Count
  $v1Count = @($versions | Where-Object { $_.versionKind -eq "ASR_DRAFT" }).Count
  $v2Count = @($versions | Where-Object { $_.versionKind -eq "ENRICHED" }).Count
  if ($asrCount -ne 1 -or $enrichmentCount -ne 1 -or $summaryCount -ne 1 -or $v1Count -ne 1 -or $v2Count -ne 1) { throw "VERTICAL_DUPLICATE_OR_MISSING_STAGE:asr=$asrCount,enrichment=$enrichmentCount,summary=$summaryCount,v1=$v1Count,v2=$v2Count" }

  $result = [ordered]@{
    status = "PASSED"
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    localSessionId = [string]$recorder.session.localSessionId
    recordingSessionId = [string]$chain.recordingSessionId
    meetingId = [string]$chain.meetingId
    mediaAssetId = [string]$chain.mediaAssetId
    asrJobId = [string]$chain.asrJobId
    transcriptV1Id = [string]$chain.transcriptV1Id
    enrichmentJobId = [string]$chain.enrichmentJobId
    transcriptV2Id = [string]$chain.transcriptV2Id
    summaryJobId = [string]$chain.summaryJobId
    summaryId = [string]$chain.summaryId
    pipelineCorrelationId = [string]$chain.pipelineCorrelationId
    pipelineSnapshot = $chain.snapshot
    pipelineOverallStatus = [string]$chain.snapshot.overallStatus
    pipelineCurrentStage = [string]$chain.snapshot.currentStage
    stageTimings = $chain.stageTimings
    checkpoints = [ordered]@{
      mediaReadyAtUtc = $mediaReadyAtUtc
      transcriptV1ReadyAtUtc = $v1ReadyAtUtc
      transcriptV2ReadyAtUtc = $enrichmentReadyAtUtc
      summaryReadyAtUtc = $summaryReadyAtUtc
    }
    stages = [ordered]@{ localReady = [string]$recorder.result.localFinalizeState; media = [string]$chain.mediaStatus; asr = [string]$chain.asrJobStatus; v1 = [string]$chain.transcriptV1Status; enrichment = [string]$chain.enrichmentJobStatus; v2 = [string]$chain.transcriptV2Status; summary = [string]$chain.summaryStatus }
    duplicateCheck = [ordered]@{ asrJobs = $asrCount; enrichmentJobs = $enrichmentCount; summaryJobs = $summaryCount; transcriptV1 = $v1Count; transcriptV2 = $v2Count }
    safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; credentialsIncluded = $false; tokensIncluded = $false }
  }
  $parent = Split-Path -Parent $ResultPath
  if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
  $result | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 -LiteralPath $ResultPath
  Write-Host "Vertical pipeline E2E passed: $ResultPath"
}
finally {
  if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

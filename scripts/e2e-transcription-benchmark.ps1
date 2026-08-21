[CmdletBinding()]
param(
  [ValidateSet("Capture", "Corpus")][string]$Mode = "Capture",
  [ValidateRange(1,100)][int]$Runs = 10,
  [ValidateRange(0,100)][int]$ColdRuns = 1,
  [ValidateRange(10,7200)][int]$Seconds = 10,
  [ValidateRange(30,28800)][int]$FinalizeTimeoutSeconds = 180,
  [string]$BaseUrl,
  [string]$TusUrl,
  [string]$Username,
  [string]$Password,
  [string]$DeviceId = "",
  [string]$InstalledHostPath = "C:\Program Files\WhisperX Atom\RecorderHost\WhisperX.Atom.Recorder.Host.exe",
  [string]$ResultPath,
  [switch]$DevelopmentHost,
  [ValidateSet("host", "container")][string]$GpuMode = "",
  [string]$DevelopmentDataRoot,
  [switch]$WithLlm,
  [string[]]$InputFiles = @()
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-transcription-benchmark-" + [guid]::NewGuid().ToString("N"))
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
  $ResultPath = Join-Path $repo "artifacts\acceptance\transcription-benchmark.json"
}

try {
  $coldRuns = [Math]::Min($Runs, [Math]::Max(0, $ColdRuns))
  if ($Mode -eq "Corpus" -and @($InputFiles).Count -eq 0) {
    throw "CORPUS_INPUT_FILES_REQUIRED"
  }
  if ($Mode -eq "Capture" -and @($InputFiles).Count -gt 0) {
    throw "CAPTURE_AND_CORPUS_INPUTS_ARE_MUTUALLY_EXCLUSIVE"
  }
  $inputMetadata = @{}
  foreach ($input in @($InputFiles)) {
    if (-not (Test-Path -LiteralPath $input -PathType Leaf)) { throw "BENCHMARK_INPUT_NOT_FOUND:$input" }
    $file = Get-Item -LiteralPath $input
    $inputMetadata[$file.FullName] = [ordered]@{
      fileName = $file.Name
      sizeBytes = $file.Length
      sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
  }
  New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
  $runsResult = [System.Collections.Generic.List[object]]::new()
  function Get-StageMetric($report, [string]$name) {
    $values = @($report.stageTimings | ForEach-Object {
      if ($null -ne $_ -and $_.PSObject.Properties.Name -contains $name -and $null -ne $_.$name) {
        [double]$_.$name
      }
    })
    if ($values.Count -eq 0) { return $null }
    # A vertical/core report normally contains one canonical chain.  If a
    # rolling API returns more than one chain, the slowest stage is the safe
    # upper bound for this run and is still text/audio-free telemetry.
    return [Math]::Round((@($values | Measure-Object -Maximum).Maximum), 3)
  }
  for ($index = 1; $index -le $Runs; $index++) {
    $started = [DateTimeOffset]::UtcNow
    $runPath = Join-Path $runRoot ("run-{0:00}.json" -f $index)
    $corpusFile = $null
    if ($Mode -eq "Corpus") {
      $corpusFile = Get-Item -LiteralPath $InputFiles[(($index - 1) % @($InputFiles).Count)]
    }
    $args = @{
      ResultPath = $runPath
    }
    if ($Mode -eq "Capture") {
      $args.Seconds = $Seconds
      $args.FinalizeTimeoutSeconds = $FinalizeTimeoutSeconds
      $args.InstalledHostPath = $InstalledHostPath
      if ($BaseUrl) { $args.BaseUrl = $BaseUrl }
      if ($Username) { $args.Username = $Username }
      if ($Password) { $args.Password = $Password }
      if ($DeviceId) { $args.DeviceId = $DeviceId }
      if ($DevelopmentHost) { $args.DevelopmentHost = $true }
      if ($GpuMode) { $args.GpuMode = $GpuMode }
      if ($DevelopmentDataRoot) { $args.DevelopmentDataRoot = $DevelopmentDataRoot }
      if ($index -le $coldRuns) { $args.RestartWorkers = $true }
    }
    else {
      # Corpus mode must exercise the production ingest path with the exact
      # supplied file.  It must never fall back to microphone capture.
      $args.AudioPath = $corpusFile.FullName
      $args.TimeoutSeconds = [Math]::Max($FinalizeTimeoutSeconds, 180)
      $args.WaitForGpu = $true
      if ($WithLlm) { $args.WithLlm = $true }
      if ($BaseUrl) { $args.BaseUrl = $BaseUrl }
      if ($TusUrl) { $args.TusUrl = $TusUrl }
      if ($Username) { $args.Username = $Username }
      if ($Password) { $args.Password = $Password }
      if ($index -le $coldRuns) { $args.RestartWorkers = $true }
    }

    try {
      if ($Mode -eq "Capture") {
        & (Join-Path $PSScriptRoot "e2e-vertical-pipeline.ps1") @args
      }
      else {
        & (Join-Path $PSScriptRoot "e2e-core.ps1") @args
      }
      if ($LASTEXITCODE -ne 0) { throw "VERTICAL_GATE_EXIT:$LASTEXITCODE" }
      $report = Get-Content -Raw -LiteralPath $runPath | ConvertFrom-Json
      $runsResult.Add([ordered]@{
        run = $index
        mode = $Mode.ToUpperInvariant()
        runMode = if ($index -le $coldRuns) { "COLD" } else { "WARM" }
        status = [string]$report.status
        startedAtUtc = $started.ToString("o")
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        durationMs = [int]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
        recordingSessionId = [string]$report.recordingSessionId
        meetingId = [string]$report.meetingId
        pipelineCorrelationId = [string]$report.pipelineCorrelationId
        pipelineOverallStatus = [string]$report.pipelineOverallStatus
        pipelineCurrentStage = [string]$report.pipelineCurrentStage
        inputFile = if ($corpusFile) { $inputMetadata[$corpusFile.FullName] } else { $null }
        # Keep the raw additive timing object for rolling consumers; the
        # bounded stageMetrics projection below is what baseline aggregation
        # uses and contains no transcript/audio payload.
        stageTimings = $report.stageTimings
        transcriptId = [string]$report.transcriptId
        transcriptStatus = [string]$report.transcriptStatus
        transcriptSegmentCount = [int]$report.transcriptSegmentCount
        qualityScore = $report.qualityScore
        qualityWarnings = @($report.qualityWarnings)
        stageMetrics = [ordered]@{
          media_assembly_ms = Get-StageMetric $report "media_assembly_ms"
          local_ready_to_flac_ms = Get-StageMetric $report "local_ready_to_flac_ms"
          flac_to_upload_started_ms = Get-StageMetric $report "flac_to_upload_started_ms"
          upload_ms = Get-StageMetric $report "upload_ms"
          finalize_ms = Get-StageMetric $report "finalize_ms"
          asr_ms = Get-StageMetric $report "asr_ms"
          alignment_ms = Get-StageMetric $report "alignment_ms"
          diarization_ms = Get-StageMetric $report "diarization_ms"
          summary_ms = Get-StageMetric $report "summary_ms"
        }
        checkpoints = $report.checkpoints
      })
    }
    catch {
      $runsResult.Add([ordered]@{
        run = $index
        mode = $Mode.ToUpperInvariant()
        runMode = if ($index -le $coldRuns) { "COLD" } else { "WARM" }
        status = "FAILED"
        errorCode = "VERTICAL_GATE_FAILED"
        error = $_.Exception.Message
        inputFile = if ($corpusFile) { $inputMetadata[$corpusFile.FullName] } else { $null }
        startedAtUtc = $started.ToString("o")
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        durationMs = [int]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
      })
    }
  }

  function Get-Percentile([double[]]$values, [double]$percentile) {
    $ordered = @($values | Where-Object { $_ -ge 0 } | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    $rank = ($ordered.Count - 1) * $percentile
    $lower = [Math]::Floor($rank); $upper = [Math]::Ceiling($rank)
    if ($lower -eq $upper) { return [Math]::Round([double]$ordered[$lower], 3) }
    return [Math]::Round([double]$ordered[$lower] + ($ordered[$upper] - $ordered[$lower]) * ($rank - $lower), 3)
  }
  $durationValues = @($runsResult | Where-Object { $_.status -eq "PASSED" } | ForEach-Object { [double]$_.durationMs })
  $stagePercentiles = [ordered]@{}
  foreach ($stageKey in @("media_assembly_ms","local_ready_to_flac_ms","flac_to_upload_started_ms","upload_ms","finalize_ms","asr_ms","alignment_ms","diarization_ms","summary_ms")) {
    $stageValues = @($runsResult | ForEach-Object {
      $value = $_.stageMetrics.$stageKey
      if ($null -ne $value) { [double]$value }
    })
    if ($stageValues.Count -gt 0) {
      $stagePercentiles[$stageKey] = [ordered]@{ p50 = Get-Percentile $stageValues 0.5; p95 = Get-Percentile $stageValues 0.95 }
    }
  }
  $passed = @($runsResult | Where-Object { $_.status -eq "PASSED" }).Count
  $result = [ordered]@{
    status = if ($passed -eq $Runs) { "PASSED" } else { "FAILED" }
    runs = $Runs
    mode = $Mode.ToUpperInvariant()
    coldRuns = $coldRuns
    warmRuns = $Runs - $coldRuns
    secondsPerRun = $Seconds
    passed = $passed
    failed = $Runs - $passed
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    measurements = @($runsResult)
    baseline = [ordered]@{
      totalDurationMs = [ordered]@{ p50 = Get-Percentile $durationValues 0.5; p95 = Get-Percentile $durationValues 0.95 }
      stageTimingsMs = $stagePercentiles
      inputFiles = @($inputMetadata.Values)
      qualityComparison = "IDs/hashes/metrics only; transcript text and audio are excluded"
    }
    safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; credentialsIncluded = $false; tokensIncluded = $false }
  }
  $parent = Split-Path -Parent $ResultPath
  if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
  $result | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8 -LiteralPath $ResultPath
  if ($passed -ne $Runs) { throw "TRANSCRIPTION_BENCHMARK_FAILED:$passed/$Runs" }
  Write-Host "Transcription benchmark passed: $ResultPath"
}
finally {
  if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

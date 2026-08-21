[CmdletBinding()]
param(
  [ValidateRange(1,100)][int]$Runs = 10,
  [ValidateRange(0,100)][int]$ColdRuns = 1,
  [ValidateRange(10,7200)][int]$Seconds = 10,
  [ValidateRange(30,1800)][int]$FinalizeTimeoutSeconds = 180,
  [string]$BaseUrl,
  [string]$Username,
  [string]$Password,
  [string]$DeviceId = "",
  [string]$InstalledHostPath = "C:\Program Files\WhisperX Atom\RecorderHost\WhisperX.Atom.Recorder.Host.exe",
  [string]$ResultPath,
  [switch]$DevelopmentHost,
  [ValidateSet("host", "container")][string]$GpuMode = "",
  [string]$DevelopmentDataRoot,
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
  foreach ($input in @($InputFiles)) {
    if (-not (Test-Path -LiteralPath $input -PathType Leaf)) { throw "BENCHMARK_INPUT_NOT_FOUND:$input" }
  }
  New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
  $runsResult = [System.Collections.Generic.List[object]]::new()
  for ($index = 1; $index -le $Runs; $index++) {
    $started = [DateTimeOffset]::UtcNow
    $runPath = Join-Path $runRoot ("run-{0:00}.json" -f $index)
    $args = @{
      Seconds = $Seconds
      FinalizeTimeoutSeconds = $FinalizeTimeoutSeconds
      ResultPath = $runPath
      InstalledHostPath = $InstalledHostPath
    }
    if ($BaseUrl) { $args.BaseUrl = $BaseUrl }
    if ($Username) { $args.Username = $Username }
    if ($Password) { $args.Password = $Password }
    if ($DeviceId) { $args.DeviceId = $DeviceId }
    if ($DevelopmentHost) { $args.DevelopmentHost = $true }
    if ($GpuMode) { $args.GpuMode = $GpuMode }
    if ($DevelopmentDataRoot) { $args.DevelopmentDataRoot = $DevelopmentDataRoot }
    if ($index -le $coldRuns) { $args.RestartWorkers = $true }

    try {
      & (Join-Path $PSScriptRoot "e2e-vertical-pipeline.ps1") @args
      if ($LASTEXITCODE -ne 0) { throw "VERTICAL_GATE_EXIT:$LASTEXITCODE" }
      $report = Get-Content -Raw -LiteralPath $runPath | ConvertFrom-Json
      $runsResult.Add([ordered]@{
        run = $index
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
        stageTimings = $report.stageTimings
        checkpoints = $report.checkpoints
      })
    }
    catch {
      $runsResult.Add([ordered]@{
        run = $index
        runMode = if ($index -le $coldRuns) { "COLD" } else { "WARM" }
        status = "FAILED"
        errorCode = "VERTICAL_GATE_FAILED"
        error = $_.Exception.Message
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
      $value = $_.stageTimings.$stageKey
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
      inputFiles = @($InputFiles | ForEach-Object { $item = Get-Item -LiteralPath $_; [ordered]@{ fileName = $item.Name; sizeBytes = $item.Length; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash.ToLowerInvariant() } })
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

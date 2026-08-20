[CmdletBinding()]
param(
  [ValidateRange(1,100)][int]$Runs = 10,
  [ValidateRange(10,7200)][int]$Seconds = 10,
  [ValidateRange(30,1800)][int]$FinalizeTimeoutSeconds = 180,
  [string]$BaseUrl,
  [string]$Username,
  [string]$Password,
  [string]$DeviceId = "",
  [string]$InstalledHostPath = "C:\Program Files\WhisperX Atom\RecorderHost\WhisperX.Atom.Recorder.Host.exe",
  [string]$ResultPath,
  [switch]$DevelopmentHost,
  [string]$DevelopmentDataRoot
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-transcription-benchmark-" + [guid]::NewGuid().ToString("N"))
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
  $ResultPath = Join-Path $repo "artifacts\acceptance\transcription-benchmark.json"
}

try {
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
    if ($DevelopmentDataRoot) { $args.DevelopmentDataRoot = $DevelopmentDataRoot }

    try {
      & (Join-Path $PSScriptRoot "e2e-vertical-pipeline.ps1") @args
      if ($LASTEXITCODE -ne 0) { throw "VERTICAL_GATE_EXIT:$LASTEXITCODE" }
      $report = Get-Content -Raw -LiteralPath $runPath | ConvertFrom-Json
      $runsResult.Add([ordered]@{
        run = $index
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
        status = "FAILED"
        errorCode = "VERTICAL_GATE_FAILED"
        error = $_.Exception.Message
        startedAtUtc = $started.ToString("o")
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        durationMs = [int]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
      })
    }
  }

  $passed = @($runsResult | Where-Object { $_.status -eq "PASSED" }).Count
  $result = [ordered]@{
    status = if ($passed -eq $Runs) { "PASSED" } else { "FAILED" }
    runs = $Runs
    secondsPerRun = $Seconds
    passed = $passed
    failed = $Runs - $passed
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    measurements = @($runsResult)
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

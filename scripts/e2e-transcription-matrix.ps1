[CmdletBinding()]
param(
  [ValidateSet("matrix", "10x10", "30x5", "60x5", "600x3")][string]$Profile = "matrix",
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
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-transcription-matrix-" + [guid]::NewGuid().ToString("N"))
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
  $ResultPath = Join-Path $repo "artifacts\acceptance\transcription-matrix.json"
}

$plans = [ordered]@{
  "10x10" = [ordered]@{ Runs = 10; Seconds = 10 }
  "30x5"  = [ordered]@{ Runs = 5; Seconds = 30 }
  "60x5"  = [ordered]@{ Runs = 5; Seconds = 60 }
  "600x3" = [ordered]@{ Runs = 3; Seconds = 600 }
}
$selected = if ($Profile -eq "matrix") { @($plans.Keys) } else { @($Profile) }

try {
  New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
  $profiles = [System.Collections.Generic.List[object]]::new()
  foreach ($name in $selected) {
    $plan = $plans[$name]
    $profilePath = Join-Path $runRoot "$name.json"
    $args = @{
      Runs = [int]$plan.Runs
      Seconds = [int]$plan.Seconds
      FinalizeTimeoutSeconds = $FinalizeTimeoutSeconds
      ResultPath = $profilePath
      InstalledHostPath = $InstalledHostPath
    }
    if ($BaseUrl) { $args.BaseUrl = $BaseUrl }
    if ($Username) { $args.Username = $Username }
    if ($Password) { $args.Password = $Password }
    if ($DeviceId) { $args.DeviceId = $DeviceId }
    if ($DevelopmentHost) { $args.DevelopmentHost = $true }
    if ($DevelopmentDataRoot) { $args.DevelopmentDataRoot = $DevelopmentDataRoot }

    $started = [DateTimeOffset]::UtcNow
    try {
      & (Join-Path $PSScriptRoot "e2e-transcription-benchmark.ps1") @args
      if ($LASTEXITCODE -ne 0) { throw "TRANSCRIPTION_BENCHMARK_EXIT:$LASTEXITCODE" }
      $report = Get-Content -Raw -LiteralPath $profilePath | ConvertFrom-Json
      $profiles.Add([ordered]@{
        profile = $name
        status = [string]$report.status
        runs = [int]$report.runs
        passed = [int]$report.passed
        failed = [int]$report.failed
        secondsPerRun = [int]$report.secondsPerRun
        startedAtUtc = $started.ToString("o")
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        durationMs = [int]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
        measurements = $report.measurements
      })
    }
    catch {
      $profiles.Add([ordered]@{
        profile = $name
        status = "FAILED"
        errorCode = "TRANSCRIPTION_BENCHMARK_FAILED"
        error = $_.Exception.Message
        runs = [int]$plan.Runs
        passed = 0
        failed = [int]$plan.Runs
        secondsPerRun = [int]$plan.Seconds
        startedAtUtc = $started.ToString("o")
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        durationMs = [int]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
      })
    }
  }

  $failedProfiles = @($profiles | Where-Object { $_.status -ne "PASSED" })
  $result = [ordered]@{
    status = if ($failedProfiles.Count -eq 0) { "PASSED" } else { "FAILED" }
    profile = $Profile
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    profiles = @($profiles)
    safety = [ordered]@{
      audioIncluded = $false
      transcriptIncluded = $false
      credentialsIncluded = $false
      tokensIncluded = $false
    }
  }
  $parent = Split-Path -Parent $ResultPath
  if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
  $result | ConvertTo-Json -Depth 30 | Set-Content -Encoding utf8 -LiteralPath $ResultPath
  if ($failedProfiles.Count -gt 0) { throw "TRANSCRIPTION_MATRIX_FAILED:$($failedProfiles.Count)" }
  Write-Host "Transcription matrix passed: $ResultPath"
}
finally {
  if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

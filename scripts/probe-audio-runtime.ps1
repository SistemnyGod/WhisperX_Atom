[CmdletBinding()]
param(
    [string]$CaptureEngine = "",
    [string]$DeviceId,
    [switch]$SystemAudio,
    [ValidateRange(1, 10)]
    [int]$Seconds = 3,
    [switch]$IncludeService,
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "Resolve-RecorderRuntime.ps1")
if ([string]::IsNullOrWhiteSpace($CaptureEngine)) {
    $CaptureEngine = if ((Resolve-RecorderRuntime).CaptureEngine -eq "AUDIOGRAPH") { "AudioGraph" } else { "LegacyWasapi" }
}
if ($CaptureEngine -notin @("LegacyWasapi", "AudioGraph")) { throw "CAPTURE_ENGINE_INVALID" }
if ($CaptureEngine -eq "AudioGraph") {
    $outputPath = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        Join-Path $repo "artifacts\audio-runtime\audiograph-report.json"
    } else {
        Join-Path $OutputRoot "audiograph-report.json"
    }
    & (Join-Path $repo "scripts\probe-audiograph-runtime.ps1") -DeviceId $DeviceId -Seconds $Seconds -OutputPath $outputPath
    exit $LASTEXITCODE
}
$project = Join-Path $repo "apps\recorder-agent\AudioRuntimeProbe\WhisperX.Atom.Recorder.AudioRuntimeProbe.csproj"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo "artifacts\audio-runtime-probe"
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

function Invoke-Probe([string]$Mode, [string]$OutputPath) {
    $probeArgs = @(
        "run", "--project", $project, "--configuration", "Release", "--no-build", "--",
        "--mode=$Mode", "--seconds=$Seconds"
    )
    if (-not [string]::IsNullOrWhiteSpace($DeviceId)) { $probeArgs += "--device-id=$DeviceId" }
    if ($SystemAudio) { $probeArgs += "--system-audio" }

    $lines = @(& dotnet @probeArgs)
    $exitCode = $LASTEXITCODE
    $json = $null
    try { $json = ($lines -join "`n") | ConvertFrom-Json } catch { }
    if ($null -eq $json) {
        $json = [ordered]@{
            success = $false
            errorCode = if ($Mode -eq "service") { "RECORDER_SERVICE_PROBE_UNAVAILABLE" } else { "INTERACTIVE_PROBE_UNAVAILABLE" }
            errorDetail = (($lines -join "`n").Trim())
            mode = $Mode
        }
    }
    $json | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    return [pscustomobject]@{ Result = $json; ExitCode = $exitCode }
}

Write-Host "Building shared audio probe..."
& dotnet build $project --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw "AUDIO_PROBE_BUILD_FAILED" }

$interactivePath = Join-Path $OutputRoot "interactive-user.json"
$interactive = Invoke-Probe "interactive" $interactivePath
$service = $null
if ($IncludeService) {
    $servicePath = Join-Path $OutputRoot "recorder-service.json"
    $service = Invoke-Probe "service" $servicePath
}

$comparison = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow
    probe = [ordered]@{ systemAudio = $SystemAudio; requestedDeviceId = $DeviceId; seconds = $Seconds }
    interactive = $interactive.Result
    service = if ($service) { $service.Result } else { [ordered]@{ status = "NOT_RUN"; reason = "Pass -IncludeService only when Recorder Service is intentionally running." } }
    decision = if (-not $service) {
        "UNDECIDED_SERVICE_PROBE_NOT_RUN"
    } elseif ($service.Result.success -eq $true -and
        $service.Result.streamOpened -eq $true -and
        $service.Result.streamStarted -eq $true -and
        [int]$service.Result.packetCount -gt 0 -and
        [long]$service.Result.bytesReceived -gt 0 -and
        $service.Result.endpointFound -eq $true -and
        $service.Result.endpointActive -eq $true -and
        $service.Result.formatResolved -eq $true) {
        "KEEP_SERVICE_CAPTURE"
    } else {
        "USER_CAPTURE_HOST"
    }
    safety = [ordered]@{ secretsIncluded = $false; audioBytesIncluded = $false; transcriptIncluded = $false }
}
$comparison | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputRoot "comparison.json") -Encoding utf8

Write-Host "Audio runtime probe artifacts: $OutputRoot"
if (-not $IncludeService) { Write-Host "Service probe was not run; architecture decision remains UNDECIDED." -ForegroundColor Yellow }
if ($interactive.ExitCode -ne 0) { exit $interactive.ExitCode }
if ($service -and $service.ExitCode -ne 0) { exit $service.ExitCode }

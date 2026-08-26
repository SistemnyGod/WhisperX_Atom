[CmdletBinding()]
param(
    [ValidateRange(20, 7200)]
    [int]$Seconds = 600,
    [ValidateRange(5, 7200)]
    [int]$ProbeAfterSeconds = 300,
    [ValidateRange(1, 60)]
    [int]$PollSeconds = 5,
    [ValidateRange(30, 1800)]
    [int]$FinalizeTimeoutSeconds = 300,
    [string]$DeviceId = "",
    [string]$OutputRoot = "",
    [string]$DataRoot = "",
    [string]$PipeName = "WhisperXAtomRecorderHost"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repo ("artifacts\acceptance\progressive-upload\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
} else { [IO.Path]::GetFullPath($OutputRoot) }
$reportPath = Join-Path $artifactRoot "report.json"
$sessionId = $null
$hostPid = $null
$hostWasStarted = $false
$stopRequested = $false
$samples = [System.Collections.Generic.List[object]]::new()

if ($ProbeAfterSeconds -ge $Seconds) {
    throw "PROGRESSIVE_UPLOAD_PROBE_MUST_BE_BEFORE_STOP"
}

function Invoke-HostCommand([string]$Command, [hashtable]$Payload = @{}) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine(([ordered]@{ command = $Command; protocolVersion = 6; payload = $Payload } | ConvertTo-Json -Compress -Depth 10))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "PROGRESSIVE_UPLOAD_EMPTY_RESPONSE:$Command" }
        return $line | ConvertFrom-Json
    }
    finally { $pipe.Dispose() }
}

function Get-HostProcess {
    Get-Process -Name "WhisperX.Atom.Recorder.Host" -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Ensure-Host {
    $existing = Get-HostProcess
    if ($null -ne $existing) {
        if (-not [string]::IsNullOrWhiteSpace($DataRoot)) { throw "PROGRESSIVE_UPLOAD_HOST_ALREADY_RUNNING_FOR_ISOLATED_ROOT" }
        $script:hostPid = $existing.Id
        return
    }
    $startArgs = @{ ReadyTimeoutSeconds = 20 }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) { $startArgs.DataRoot = [IO.Path]::GetFullPath($DataRoot) }
    & (Join-Path $repo "scripts\start-recorder-host.ps1") @startArgs
    $started = Get-HostProcess
    if ($null -eq $started) { throw "PROGRESSIVE_UPLOAD_RECORDER_HOST_NOT_RUNNING" }
    $script:hostPid = $started.Id
    $script:hostWasStarted = $true
}

function Get-SessionStatus {
    $response = Invoke-HostCommand "GET_SESSION_STATUS" @{ sessionId = $sessionId }
    if ($response.ok -ne $true -or $null -eq $response.sessionStatus) { throw "PROGRESSIVE_UPLOAD_SESSION_STATUS_UNAVAILABLE" }
    return $response.sessionStatus
}

function Add-Sample([int]$ElapsedSeconds, $Status) {
    $samples.Add([ordered]@{
        elapsedSeconds = $ElapsedSeconds
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        captureState = [string]$Status.captureState
        deliveryState = [string]$Status.deliveryState
        localChunkCount = [int]$Status.localChunkCount
        confirmedChunkCount = [int]$Status.confirmedChunkCount
        pendingChunkCount = [int]$Status.pendingChunkCount
        chunksReady = [int]$Status.chunksReady
        chunksUploading = [int]$Status.chunksUploading
        bytesPending = [int64]$Status.bytesPending
        serverSessionId = [string]$Status.serverSessionId
    })
}

try {
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    Ensure-Host
    $health = Invoke-HostCommand "HEALTH"
    if ($health.ok -ne $true -or $null -eq $health.health) { throw "PROGRESSIVE_UPLOAD_RECORDER_HEALTH_FAILED" }

    $probePayload = @{}
    if (-not [string]::IsNullOrWhiteSpace($DeviceId)) { $probePayload.deviceId = $DeviceId }
    $probe = Invoke-HostCommand "TEST_AUDIO_SOURCE" $probePayload
    if ($probe.ok -ne $true) { throw "PROGRESSIVE_UPLOAD_AUDIO_PROBE_FAILED:$($probe.error)" }

    # This gate intentionally requests server delivery.  It must not be
    # localOnly: the whole purpose is to prove that completed chunks leave the
    # machine while capture is still active.
    $start = Invoke-HostCommand "START" @{ title = "ACCEPTANCE_PROGRESSIVE_UPLOAD"; localOnly = $false }
    if ($start.ok -ne $true -or [string]$start.state -notmatch "RECORDING") { throw "PROGRESSIVE_UPLOAD_START_FAILED:$($start.error)" }
    $sessionId = [string]$start.sessionId
    if ([string]::IsNullOrWhiteSpace($sessionId)) { throw "PROGRESSIVE_UPLOAD_SESSION_ID_MISSING" }

    $midpoint = $null
    $startedAt = [DateTimeOffset]::UtcNow
    for ($elapsed = 0; $elapsed -lt $Seconds; $elapsed += $PollSeconds) {
        Start-Sleep -Seconds ([Math]::Min($PollSeconds, $Seconds - $elapsed))
        $status = Get-SessionStatus
        Add-Sample $([Math]::Min($Seconds, $elapsed + $PollSeconds)) $status
        if ($null -eq $midpoint -and ($elapsed + $PollSeconds) -ge $ProbeAfterSeconds) {
            $midpoint = $status
        }
        if ([string]$status.captureState -notin @("RECORDING", "PAUSED")) {
            throw "PROGRESSIVE_UPLOAD_CAPTURE_STOPPED_EARLY:$($status.captureState)"
        }
    }

    if ($null -eq $midpoint) { throw "PROGRESSIVE_UPLOAD_MIDPOINT_NOT_OBSERVED" }
    $midpointConfirmed = [int]$midpoint.confirmedChunkCount
    $midpointServerSession = [string]$midpoint.serverSessionId
    $bytesPendingValues = @($samples | ForEach-Object { [int64]$_.bytesPending } | Select-Object -Unique)
    $bytesPendingObserved = ($bytesPendingValues.Count -gt 1) -or (@($bytesPendingValues | Where-Object { $_ -gt 0 }).Count -gt 0)
    if ([string]::IsNullOrWhiteSpace($midpointServerSession) -or $midpointConfirmed -le 0) {
        throw "PROGRESSIVE_UPLOAD_NOT_CONFIRMED_AT_MIDPOINT:serverSession=$midpointServerSession,confirmed=$midpointConfirmed"
    }
    if (-not $bytesPendingObserved) {
        throw "PROGRESSIVE_UPLOAD_BYTES_PENDING_NOT_OBSERVED"
    }

    $stopStarted = [DateTimeOffset]::UtcNow
    $stop = Invoke-HostCommand "STOP"
    $stopRequested = $true
    if ($stop.ok -ne $true) { throw "PROGRESSIVE_UPLOAD_STOP_FAILED:$($stop.error)" }
    $stopLatencyMs = ([DateTimeOffset]::UtcNow - $stopStarted).TotalMilliseconds

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
    $final = $null
    do {
        Start-Sleep -Seconds $PollSeconds
        $final = Get-SessionStatus
    } while ([DateTimeOffset]::UtcNow -lt $deadline -and [string]$final.deliveryState -notin @("CONFIRMED", "COMPLETED", "DELIVERY_FAILED"))

    $finalConfirmed = [int]$final.confirmedChunkCount
    if ($finalConfirmed -lt $midpointConfirmed) { throw "PROGRESSIVE_UPLOAD_CONFIRMED_COUNT_REGRESSED" }
    if ([string]$final.deliveryState -notin @("CONFIRMED", "COMPLETED")) { throw "PROGRESSIVE_UPLOAD_FINAL_DELIVERY_FAILED:$($final.deliveryState):$($final.errorCode)" }

    $report = [ordered]@{
        schemaVersion = 1
        gate = "PROGRESSIVE_UPLOAD"
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        requestedSeconds = $Seconds
        probeAfterSeconds = $ProbeAfterSeconds
        session = [ordered]@{
            localSessionId = $sessionId
            serverSessionId = [string]$final.serverSessionId
            stopLatencyMs = [Math]::Round($stopLatencyMs, 2)
        }
        midpoint = [ordered]@{
            serverSessionId = $midpointServerSession
            confirmedChunkCount = $midpointConfirmed
            bytesPending = [int64]$midpoint.bytesPending
            bytesPendingObserved = $bytesPendingObserved
            chunksReady = [int]$midpoint.chunksReady
            chunksUploading = [int]$midpoint.chunksUploading
        }
        final = [ordered]@{
            deliveryState = [string]$final.deliveryState
            confirmedChunkCount = $finalConfirmed
            pendingChunkCount = [int]$final.pendingChunkCount
            bytesPending = [int64]$final.bytesPending
            localFinalizeState = [string]$final.localFinalizeState
            archiveState = [string]$final.archiveState
        }
        samples = $samples
        safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; credentialsIncluded = $false; tokensIncluded = $false }
        result = [ordered]@{ status = "PASSED"; progressiveConfirmed = $true; bytesPendingObserved = $bytesPendingObserved; tailConfirmed = ($finalConfirmed -ge $midpointConfirmed) }
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "Progressive upload acceptance passed: $reportPath"
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($sessionId) -and -not $stopRequested) { try { Invoke-HostCommand "STOP" | Out-Null } catch { } }
    throw
}

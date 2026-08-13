[CmdletBinding()]
param(
    [ValidateRange(10, 300)]
    [int]$Seconds = 30,
    [ValidateRange(10, 180)]
    [int]$FinalizeTimeoutSeconds = 90,
    [string]$DeviceId = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pipeName = "WhisperXAtomRecorderHost"
$artifactRoot = Join-Path $repo "artifacts\acceptance\audiograph-local"
$reportPath = Join-Path $artifactRoot ("report-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
$env:AUDIO_CAPTURE_ENGINE = "AUDIOGRAPH"
$hostWasStarted = $false
$localSessionId = $null
$stopRequested = $false

function Invoke-HostCommand([string]$Command, [hashtable]$Payload = @{}) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine(([ordered]@{ command = $Command; protocolVersion = 6; payload = $Payload } | ConvertTo-Json -Compress -Depth 8))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "AUDIOGRAPH_EMPTY_RESPONSE: $Command" }
        return $line | ConvertFrom-Json
    }
    finally { $pipe.Dispose() }
}

try {
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    $hostScript = Join-Path $repo "scripts\start-recorder-host.ps1"
    & $hostScript -ReadyTimeoutSeconds 20
    $hostWasStarted = $true

    $health = Invoke-HostCommand "HEALTH"
    if ($health.ok -ne $true) { throw "AUDIOGRAPH_HEALTH_FAILED: $($health.error)" }
    $probePayload = @{}
    if (-not [string]::IsNullOrWhiteSpace($DeviceId)) { $probePayload.deviceId = $DeviceId }
    $probe = Invoke-HostCommand "TEST_AUDIO_SOURCE" $probePayload
    if ($probe.ok -ne $true) { throw "AUDIOGRAPH_PROBE_FAILED: $($probe.error)" }
    $probeResult = if ($null -ne $probe.audioGraphProbe) { $probe.audioGraphProbe } else { $probe.audioSourceTest }
    if (-not [string]::IsNullOrWhiteSpace($DeviceId) -and [string]$probeResult.deviceId -ne $DeviceId) {
        throw "AUDIOGRAPH_FIXED_DEVICE_MISMATCH: requested=$DeviceId selected=$($probeResult.deviceId)"
    }
    $start = Invoke-HostCommand "START" @{ title = "AUDIOGRAPH_LOCAL_GATE"; localOnly = $true }
    if ($start.ok -ne $true) { throw "AUDIOGRAPH_START_FAILED: $($start.error)" }
    $localSessionId = [string]$start.sessionId
    if ([string]::IsNullOrWhiteSpace($localSessionId)) { throw "AUDIOGRAPH_SESSION_ID_MISSING" }
    if ([string]$start.state -notmatch "RECORDING") { throw "AUDIOGRAPH_FIRST_FRAME_NOT_CONFIRMED: state=$($start.state)" }

    Start-Sleep -Seconds $Seconds
    $stop = Invoke-HostCommand "STOP"
    $stopRequested = $true
    if ($stop.ok -ne $true) { throw "AUDIOGRAPH_STOP_FAILED: $($stop.error)" }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
    $status = $null
    do {
        Start-Sleep -Seconds 2
        $statusResponse = Invoke-HostCommand "GET_SESSION_STATUS" @{ sessionId = $localSessionId }
        $status = $statusResponse.sessionStatus
        if ($null -ne $status -and $status.localFinalizeState -in @("LOCAL_READY", "COMPLETED", "FAILED", "LOCAL_FAILED")) { break }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ($null -eq $status) { throw "AUDIOGRAPH_STATUS_MISSING" }
    $archivePath = [string]$status.archivePath
    $archiveExists = -not [string]::IsNullOrWhiteSpace($archivePath) -and (Test-Path -LiteralPath $archivePath -PathType Container)
    $archiveFiles = if ($archiveExists) { @(Get-ChildItem -LiteralPath $archivePath -Recurse -File -ErrorAction SilentlyContinue) } else { @() }
    $flacFiles = @($archiveFiles | Where-Object Extension -ieq ".flac")
    $report = [ordered]@{
        schemaVersion = 1
        gate = "AUDIOGRAPH_LOCAL_RECORDING"
        generatedAtUtc = [DateTimeOffset]::UtcNow
        engine = "AUDIOGRAPH"
        processModel = "CURRENT_USER_HOST"
        selection = [ordered]@{
            selectionMode = if ([string]::IsNullOrWhiteSpace($DeviceId)) { "DEFAULT" } else { "FIXED" }
            requestedDeviceId = if ([string]::IsNullOrWhiteSpace($DeviceId)) { $null } else { $DeviceId }
            selectedDeviceId = [string]$probeResult.deviceId
            selectedDeviceName = [string]$probeResult.deviceName
        }
        session = [ordered]@{ localSessionId = $localSessionId; requestedSeconds = $Seconds; startState = [string]$start.state; stopState = [string]$stop.state }
        result = [ordered]@{
            firstFrameConfirmed = ([string]$start.state -match "RECORDING")
            localFinalizeState = [string]$status.localFinalizeState
            localChunkCount = [int]$status.localChunkCount
            archiveExists = $archiveExists
            flacFileCount = $flacFiles.Count
            deliveryState = [string]$status.deliveryState
            errorCode = [string]$status.errorCode
        }
        safety = [ordered]@{ credentialsIncluded = $false; tokensIncluded = $false; audioIncluded = $false; transcriptIncluded = $false }
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "AudioGraph local report: $reportPath"
    if (-not $report.result.firstFrameConfirmed -or $report.result.localFinalizeState -ne "LOCAL_READY" -or $report.result.localChunkCount -le 0 -or $report.result.flacFileCount -le 0 -or -not $report.result.archiveExists) {
        throw "AUDIOGRAPH_LOCAL_RECORDING_GATE_FAILED: report=$reportPath"
    }
}
finally {
    if ($hostWasStarted) {
        $pidPath = Join-Path $repo "artifacts\runtime\recorder-host.pid"
        if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
            $hostPid = [int](Get-Content -LiteralPath $pidPath -Raw)
            Stop-Process -Id $hostPid -Force -ErrorAction SilentlyContinue
        }
    }
}

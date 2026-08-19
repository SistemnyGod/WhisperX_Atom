[CmdletBinding()]
param(
    [ValidateSet("baseline", "pause-resume", "server-offline", "api-crash", "workers-restart", "desktop-close", "usb-loss", "low-disk", "host-crash")]
    [string]$Scenario = "baseline",
    [ValidateRange(10, 7200)]
    [int]$Seconds = 30,
    [ValidateRange(10, 600)]
    [int]$FinalizeTimeoutSeconds = 180,
    [string]$DeviceId = "",
    [string]$OutputRoot = "",
    [string]$DataRoot = "",
    [string]$ApiContainer = "whisperx-atom-api",
    [string[]]$WorkerContainers = @("whisperx-atom-gpu-worker", "whisperx-atom-media-worker", "whisperx-atom-summary-worker"),
    [switch]$ExecuteFaults,
    [switch]$StopHost
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pipeName = "WhisperXAtomRecorderHost"
$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repo ("artifacts\acceptance\recording-e2e\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
} else { [IO.Path]::GetFullPath($OutputRoot) }
$reportPath = Join-Path $artifactRoot "report.json"
$hostPid = $null
$hostWasStarted = $false
$sessionId = $null
$stopRequested = $false
$expectedCaptureFailure = $false
$hostRestartedAfterFault = $false
$faultEvents = [System.Collections.Generic.List[object]]::new()

function Invoke-HostCommand([string]$Command, [hashtable]$Payload = @{}) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine(([ordered]@{ command = $Command; protocolVersion = 6; payload = $Payload } | ConvertTo-Json -Compress -Depth 10))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "RECORDER_IPC_EMPTY_RESPONSE: $Command" }
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
        # An isolated DataRoot must never accidentally attach to the user's
        # installed Host and write test audio into production spool.
        if (-not [string]::IsNullOrWhiteSpace($DataRoot)) { throw "E2E_HOST_ALREADY_RUNNING_FOR_ISOLATED_ROOT" }
        $script:hostPid = $existing.Id
        return
    }
    $startArgs = @{ ReadyTimeoutSeconds = 20 }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) { $startArgs.DataRoot = [IO.Path]::GetFullPath($DataRoot) }
    & (Join-Path $repo "scripts\start-recorder-host.ps1") @startArgs
    $started = Get-HostProcess
    if ($null -eq $started) { throw "RECORDER_HOST_NOT_RUNNING" }
    $script:hostPid = $started.Id
    $script:hostWasStarted = $true
}

function Get-SessionStatus {
    $response = Invoke-HostCommand "GET_SESSION_STATUS" @{ sessionId = $sessionId }
    if ($response.ok -ne $true -or $null -eq $response.sessionStatus) { throw "RECORDER_SESSION_STATUS_UNAVAILABLE" }
    return $response.sessionStatus
}

function Add-FaultEvent([string]$Name, [string]$Action, [string]$State) {
    $faultEvents.Add([ordered]@{ name = $Name; action = $Action; state = $State; atUtc = [DateTimeOffset]::UtcNow })
}

function Invoke-ContainerAction([string]$Name, [string]$Action) {
    if (-not $ExecuteFaults) { throw "E2E_FAULT_REQUIRES_EXECUTEFAULTS: $Name/$Action" }
    if ([string]::IsNullOrWhiteSpace($Name)) { throw "E2E_CONTAINER_NAME_REQUIRED" }
    & docker $Action $Name
    if ($LASTEXITCODE -ne 0) { throw "E2E_DOCKER_ACTION_FAILED: $Action $Name" }
    Add-FaultEvent $Name "docker $Action" "EXECUTED"
}

function Invoke-ScenarioFault([int]$ElapsedSeconds) {
    switch ($Scenario) {
        "pause-resume" {
            if ($ElapsedSeconds -eq [math]::Max(1, [math]::Floor($Seconds / 3))) {
                $pause = Invoke-HostCommand "PAUSE"
                if ($pause.ok -ne $true) { throw "E2E_PAUSE_FAILED: $($pause.error)" }
                Add-FaultEvent "pause" "PAUSE" "EXECUTED"
            }
            if ($ElapsedSeconds -eq [math]::Max(2, [math]::Floor($Seconds * 2 / 3))) {
                $resume = Invoke-HostCommand "RESUME"
                if ($resume.ok -ne $true) { throw "E2E_RESUME_FAILED: $($resume.error)" }
                Add-FaultEvent "resume" "RESUME" "EXECUTED"
            }
        }
        "server-offline" {
            if ($ElapsedSeconds -eq [math]::Max(1, [math]::Floor($Seconds / 3))) { Invoke-ContainerAction $ApiContainer "stop" }
            if ($ElapsedSeconds -eq [math]::Max(2, [math]::Floor($Seconds * 2 / 3))) { Invoke-ContainerAction $ApiContainer "start" }
        }
        "api-crash" {
            if ($ElapsedSeconds -eq [math]::Max(1, [math]::Floor($Seconds / 3))) { Invoke-ContainerAction $ApiContainer "kill" }
            if ($ElapsedSeconds -eq [math]::Max(2, [math]::Floor($Seconds * 2 / 3))) { Invoke-ContainerAction $ApiContainer "start" }
        }
        "workers-restart" {
            if ($ElapsedSeconds -eq [math]::Max(1, [math]::Floor($Seconds / 2))) {
                foreach ($container in $WorkerContainers) { Invoke-ContainerAction $container "restart" }
            }
        }
        "desktop-close" {
            if ($ElapsedSeconds -eq [math]::Max(1, [math]::Floor($Seconds / 2))) {
                if (-not $ExecuteFaults) { throw "E2E_FAULT_REQUIRES_EXECUTEFAULTS: desktop-close" }
                $desktop = Get-Process -Name "WhisperX.Atom.Desktop" -ErrorAction SilentlyContinue | Where-Object SessionId -eq (Get-Process -Id $PID).SessionId | Select-Object -First 1
                if ($null -ne $desktop) {
                    [void]$desktop.CloseMainWindow()
                    Add-FaultEvent "desktop" "CloseMainWindow" "EXECUTED"
                } else { Add-FaultEvent "desktop" "CloseMainWindow" "NOT_FOUND" }
            }
        }
        "usb-loss" {
            if ($ElapsedSeconds -eq 1) {
                $script:expectedCaptureFailure = $true
                Add-FaultEvent "usb-loss" "MANUAL_DISCONNECT_REQUIRED" "WAITING_USER"
                Write-Warning "Отключите выбранный USB-микрофон сейчас и подключите его после проверки. Автоматическое отключение устройств запрещено."
            }
        }
        "low-disk" {
            if ($ElapsedSeconds -eq 1) {
                $root = if ([string]::IsNullOrWhiteSpace($DataRoot)) { [IO.Path]::GetPathRoot($repo) } else { [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($DataRoot)) }
                $drive = [IO.DriveInfo]::new($root)
                Add-FaultEvent "low-disk" "CHECK_FREE_BYTES" ([string]$drive.AvailableFreeSpace)
                if ($drive.AvailableFreeSpace -gt 512MB) {
                    Add-FaultEvent "low-disk" "LOW_DISK_THRESHOLD" "NOT_REPRODUCED"
                }
            }
        }
        "host-crash" {
            if ($ElapsedSeconds -eq [math]::Max(1, [math]::Floor($Seconds / 2))) {
                if (-not $ExecuteFaults) { throw "E2E_FAULT_REQUIRES_EXECUTEFAULTS: host-crash" }
                $process = Get-Process -Id $hostPid -ErrorAction Stop
                Stop-Process -Id $process.Id -Force
                $script:expectedCaptureFailure = $true
                Add-FaultEvent "recorder-host" "Stop-Process -Force" "EXECUTED"
            }
        }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    Ensure-Host
    $health = Invoke-HostCommand "HEALTH"
    if ($health.ok -ne $true -or $null -eq $health.health) { throw "RECORDER_HEALTH_FAILED" }

    $probePayload = @{}
    if (-not [string]::IsNullOrWhiteSpace($DeviceId)) { $probePayload.deviceId = $DeviceId }
    $probe = Invoke-HostCommand "TEST_AUDIO_SOURCE" $probePayload
    if ($probe.ok -ne $true) { throw "AUDIO_PROBE_FAILED: $($probe.error)" }

    $startPayload = @{ title = "E2E_RECORDING_$Scenario"; localOnly = $true }
    $start = Invoke-HostCommand "START" $startPayload
    if ($start.ok -ne $true -or [string]$start.state -notmatch "RECORDING") { throw "E2E_START_FAILED: $($start.error)" }
    $sessionId = [string]$start.sessionId
    if ([string]::IsNullOrWhiteSpace($sessionId)) { throw "E2E_SESSION_ID_MISSING" }

    $startedAt = [DateTimeOffset]::UtcNow
    for ($elapsed = 1; $elapsed -le $Seconds; $elapsed++) {
        Start-Sleep -Seconds 1
        Invoke-ScenarioFault $elapsed
    }

    $stopStarted = [DateTimeOffset]::UtcNow
    $stop = $null
    try {
        $stop = Invoke-HostCommand "STOP"
    }
    catch {
        if (-not $expectedCaptureFailure) { throw }
        $stop = [pscustomobject]@{ ok = $true; state = "CAPTURE_FAILED_EXPECTED"; error = "EXPECTED_CAPTURE_FAILURE" }
    }
    $stopRequested = $true
    $stopLatencyMs = ([DateTimeOffset]::UtcNow - $stopStarted).TotalMilliseconds
    if ($stop.ok -ne $true) { throw "E2E_STOP_FAILED: $($stop.error)" }

    if ($Scenario -eq "host-crash") {
        # Restart only the Host that this script started/owned, then inspect
        # the same SQLite session. No second process is allowed.
        Ensure-Host
        $hostRestartedAfterFault = $true
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
    $status = $null
    do {
        Start-Sleep -Seconds 2
        $status = Get-SessionStatus
        if ($status.localFinalizeState -in @("LOCAL_READY", "RECOVERY_PENDING", "LOCAL_FAILED", "FAILED")) { break }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $status) { throw "E2E_FINAL_STATUS_MISSING" }

    $report = [ordered]@{
        schemaVersion = 1
        gate = "RECORDER_E2E"
        generatedAtUtc = [DateTimeOffset]::UtcNow
        scenario = $Scenario
        requestedSeconds = $Seconds
        session = [ordered]@{ localSessionId = $sessionId; startState = [string]$start.state; stopState = [string]$stop.state; stopLatencyMs = [math]::Round($stopLatencyMs, 2) }
        host = [ordered]@{ processId = $hostPid; pipe = $pipeName }
        probe = $probe.audioGraphProbe
        faults = $faultEvents
        result = [ordered]@{
            localFinalizeState = [string]$status.localFinalizeState
            deliveryState = [string]$status.deliveryState
            rawChunkCount = [int]$status.rawChunkCount
            rawWritingCount = [int]$status.rawWritingCount
            rawReadyCount = [int]$status.rawReadyCount
            rawFailedCount = [int]$status.rawFailedCount
            errorCode = [string]$status.errorCode
            archiveState = [string]$status.archiveState
            framesProduced = if ($null -ne $health.health.lastAudioGraphAttempt) { [int64]$health.health.lastAudioGraphAttempt.framesProduced } else { 0 }
            framesConsumed = if ($null -ne $health.health.lastAudioGraphAttempt) { [int64]$health.health.lastAudioGraphAttempt.framesConsumed } else { 0 }
        }
        safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; credentialsIncluded = $false; tokensIncluded = $false }
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "Recorder E2E report: $reportPath"

    if ($report.result.rawWritingCount -ne 0) { throw "E2E_RAW_WRITING_REMAINS: report=$reportPath" }
    if ($report.result.localFinalizeState -eq "LOCAL_FAILED" -and $Scenario -eq "baseline") { throw "E2E_LOCAL_FINALIZE_FAILED: $($report.result.errorCode)" }
    if ($expectedCaptureFailure -and [string]::IsNullOrWhiteSpace($report.result.errorCode)) { throw "E2E_EXPECTED_CAPTURE_FAILURE_NOT_PERSISTED: report=$reportPath" }
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($sessionId) -and -not $stopRequested) { try { Invoke-HostCommand "STOP" | Out-Null } catch { } }
    throw
}
finally {
    if ($StopHost -and $hostWasStarted -and $null -ne $hostPid) {
        try { Stop-Process -Id $hostPid -Force -ErrorAction SilentlyContinue } catch { }
    }
}

[CmdletBinding()]
param(
    # Long gates are intentional: the release plan requires 30 s, 15 min and 60 min runs.
    [ValidateRange(10, 3600)]
    [int]$Seconds = 30,
    [ValidateRange(10, 180)]
    [int]$FinalizeTimeoutSeconds = 90,
    [string]$DeviceId = "",
    [switch]$ServerDelivery,
    # Raw-first STOP is allowed to return before background FLAC/archive work
    # completes.  Use this switch for the explicit no-encoder smoke gate;
    # normal release gates continue waiting for the installed encoder.
    [switch]$AllowPendingArchive,
    [switch]$StopHost,
    [switch]$DevelopmentHost,
    [string]$DevelopmentDataRoot = "",
    [string]$InstalledHostPath = "C:\Program Files\WhisperX Atom\RecorderHost\WhisperX.Atom.Recorder.Host.exe",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pipeName = "WhisperXAtomRecorderHost"
$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $repo "artifacts\acceptance\audiograph-local" } else { [IO.Path]::GetFullPath($OutputRoot) }
$reportPath = Join-Path $artifactRoot ("report-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
$env:AUDIO_CAPTURE_ENGINE = "AUDIOGRAPH"
$hostWasStarted = $false
$localSessionId = $null
$stopRequested = $false
$hostProcessId = $null
$hostPath = $null
$hostBuild = $null
$prePurgeStatus = $null

function Get-ProductVersion([string]$path) {
    try {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
        return [string](Get-Item -LiteralPath $path).VersionInfo.ProductVersion
    }
    catch { return $null }
}

function Get-HostProcesses {
    @(Get-Process -Name "WhisperX.Atom.Recorder.Host" -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = [IO.Path]::GetFullPath($_.Path) } catch { }
        [pscustomobject]@{ Process = $_; Path = $path; Build = Get-ProductVersion $path }
    })
}

function Ensure-ReleaseHost {
    if ($DevelopmentHost) {
        $script = Join-Path $repo "scripts\start-recorder-host.ps1"
        $startArgs = @{ ReadyTimeoutSeconds = 20 }
        if (-not [string]::IsNullOrWhiteSpace($DevelopmentDataRoot)) {
            $startArgs.DataRoot = [IO.Path]::GetFullPath($DevelopmentDataRoot)
        }
        & $script @startArgs
        $scriptProcess = Get-HostProcesses | Select-Object -First 1
        if ($null -ne $scriptProcess) {
            $scriptProcess.Process.Id | Set-Content -LiteralPath (Join-Path $repo "artifacts\runtime\recorder-host.pid") -Encoding ascii
            $scriptProcess
        }
        return
    }

    if (-not (Test-Path -LiteralPath $InstalledHostPath -PathType Leaf)) {
        throw "RECORDER_HOST_BUILD_MISMATCH: installed Host was not found at '$InstalledHostPath'"
    }
    $expectedPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $InstalledHostPath).Path)
    $expectedBuild = Get-ProductVersion $expectedPath
    $running = @(Get-HostProcesses)
    if ($running.Count -gt 0) {
        if ($running.Count -ne 1) {
            throw "RECORDER_HOST_DUPLICATE: expected one installed Host, found $($running.Count)"
        }
        $wrong = $running | Where-Object { -not $_.Path -or $_.Path -ne $expectedPath }
        if ($wrong.Count -gt 0) {
            throw "RECORDER_HOST_BUILD_MISMATCH: running Host path '$($wrong[0].Path)' does not match '$expectedPath'"
        }
        $wrongBuild = $running | Where-Object { $expectedBuild -and $_.Build -and $_.Build -ne $expectedBuild }
        if ($wrongBuild.Count -gt 0) {
            throw "RECORDER_HOST_BUILD_MISMATCH: running Host build '$($wrongBuild[0].Build)' does not match '$expectedBuild'"
        }
        $hostProcessId = [int]$running[0].Process.Id
        $hostPath = $expectedPath
        $hostBuild = $expectedBuild
        return $running[0]
    }

    $startScript = Join-Path $repo "scripts\start-recorder-host.ps1"
    & $startScript -ExecutablePath $expectedPath -ReadyTimeoutSeconds 20
    $started = Get-HostProcesses | Select-Object -First 1
    if ($null -eq $started -or $started.Path -ne $expectedPath -or ($expectedBuild -and $started.Build -ne $expectedBuild)) {
        $actual = if ($null -eq $started) { "none" } else { "$($started.Path) ($($started.Build))" }
        throw "RECORDER_HOST_BUILD_MISMATCH: expected '$expectedPath' ($expectedBuild), actual '$actual'"
    }
    $hostProcessId = [int]$started.Process.Id
    $hostPath = $started.Path
    $hostBuild = $started.Build
    return $started
}

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

function Test-WaveFile([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $file = Get-Item -LiteralPath $Path -ErrorAction Stop
        if ($file.Length -lt 44) { return $false }
        $stream = [IO.File]::OpenRead($Path)
        try {
            $header = New-Object byte[] 4
            if ($stream.Read($header, 0, 4) -ne 4) { return $false }
            $magic = [Text.Encoding]::ASCII.GetString($header)
            return $magic -in @("RIFF", "RF64")
        }
        finally { $stream.Dispose() }
    }
    catch { return $false }
}

try {
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    $hostInfo = Ensure-ReleaseHost
    $hostWasStarted = $true
    if ([string]::IsNullOrWhiteSpace($hostPath) -and $null -ne $hostInfo) { $hostPath = [string]$hostInfo.Path }
    if ($null -eq $hostProcessId -and $null -ne $hostInfo) { $hostProcessId = [int]$hostInfo.Process.Id }
    if ([string]::IsNullOrWhiteSpace($hostBuild) -and $null -ne $hostInfo) { $hostBuild = [string]$hostInfo.Build }

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
    if (-not [string]::IsNullOrWhiteSpace($DeviceId)) {
        $selection = Invoke-HostCommand "SELECT_AUDIO_DEVICE" @{ deviceId = $DeviceId }
        if ($selection.ok -ne $true) { throw "AUDIOGRAPH_FIXED_DEVICE_SELECT_FAILED: $($selection.error)" }
    }
    # Keep the default gate offline and deterministic; opt in to the real
    # bind/upload/finalize path for an end-to-end LAN acceptance run.
    $startPayload = @{ title = if ($ServerDelivery) { "AUDIOGRAPH_SERVER_GATE" } else { "AUDIOGRAPH_LOCAL_GATE" }; localOnly = $true }
    if ($ServerDelivery) { $startPayload.localOnly = $false }
    $start = Invoke-HostCommand "START" $startPayload
    if ($start.ok -ne $true) { throw "AUDIOGRAPH_START_FAILED: $($start.error)" }
    $localSessionId = [string]$start.sessionId
    if ([string]::IsNullOrWhiteSpace($localSessionId)) { throw "AUDIOGRAPH_SESSION_ID_MISSING" }
    if ([string]$start.state -notmatch "RECORDING") { throw "AUDIOGRAPH_FIRST_FRAME_NOT_CONFIRMED: state=$($start.state)" }

    Start-Sleep -Seconds $Seconds
    $stop = Invoke-HostCommand "STOP"
    $stopRequested = $true
    if ($stop.ok -ne $true) { throw "AUDIOGRAPH_STOP_FAILED: $($stop.error)" }
    if ($null -ne $stop.sessionStatus) { $prePurgeStatus = $stop.sessionStatus }
    $postStopHealth = Invoke-HostCommand "HEALTH"

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
    $status = $null
    do {
        Start-Sleep -Seconds 2
        $statusResponse = Invoke-HostCommand "GET_SESSION_STATUS" @{ sessionId = $localSessionId }
        $status = $statusResponse.sessionStatus
        if ($null -ne $status -and [int]$status.rawChunkCount -gt 0 -and ($null -eq $prePurgeStatus -or [int]$status.rawChunkCount -gt [int]$prePurgeStatus.rawChunkCount -or ([int]$status.rawChunkCount -eq [int]$prePurgeStatus.rawChunkCount -and [int]$status.rawWritingCount -lt [int]$prePurgeStatus.rawWritingCount))) { $prePurgeStatus = $status }
        if ($null -ne $status -and $status.localFinalizeState -in @("LOCAL_READY", "RECOVERY_PENDING", "COMPLETED", "FAILED", "LOCAL_FAILED")) { break }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ($null -eq $status) { throw "AUDIOGRAPH_STATUS_MISSING" }

    # Playable audio is derived asynchronously from the durable PCM boundary.
    # Do not report the local recording as complete until the atomic WAV/RF64
    # rename has happened and every registered track file is visible.
    $playableDeadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
    $playableFiles = @()
    $playableState = [string]$status.playableAudioState
    $playableFormatValid = $false
    $playableReady = $false
    do {
        $playableState = [string]$status.playableAudioState
        $playableFiles = if ($null -ne $status.playableAudioFiles) { @($status.playableAudioFiles) } else { @() }
        $playableFormatValid = $playableFiles.Count -gt 0 -and (@($playableFiles | ForEach-Object { Test-WaveFile ([string]$_.localPath) }) -notcontains $false)
        $playableReady = $playableState -eq "READY" -and $playableFormatValid
        if ($playableReady -or $playableState -eq "FAILED") { break }
        Start-Sleep -Seconds 2
        $statusResponse = Invoke-HostCommand "GET_SESSION_STATUS" @{ sessionId = $localSessionId }
        $status = $statusResponse.sessionStatus
    } while ($null -ne $status -and [DateTimeOffset]::UtcNow -lt $playableDeadline)

    if ($null -eq $status) { throw "AUDIOGRAPH_PLAYABLE_STATUS_MISSING" }
    # A server-delivery gate must not stop at LOCAL_READY.  The recorder is
    # intentionally local-first, but this acceptance scenario also verifies
    # that the background bind/upload/finalize path reaches a durable terminal
    # state before the test Host is torn down.  Keep the local-only gate fast.
    if ($ServerDelivery) {
        $deliveryDeadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
        do {
            if ($status.deliveryState -in @("CONFIRMED", "DELIVERY_FAILED")) { break }
            Start-Sleep -Seconds 2
            $statusResponse = Invoke-HostCommand "GET_SESSION_STATUS" @{ sessionId = $localSessionId }
            $status = $statusResponse.sessionStatus
            if ($null -ne $status -and [int]$status.rawChunkCount -gt 0 -and ($null -eq $prePurgeStatus -or [int]$status.rawChunkCount -gt [int]$prePurgeStatus.rawChunkCount -or ([int]$status.rawChunkCount -eq [int]$prePurgeStatus.rawChunkCount -and [int]$status.rawWritingCount -lt [int]$prePurgeStatus.rawWritingCount))) { $prePurgeStatus = $status }
        } while ($null -ne $status -and [DateTimeOffset]::UtcNow -lt $deliveryDeadline)
    }

    # Raw-only acceptance requires that the finalizer has closed every WRITING
    # row before the report is accepted. The status may later be purged after a
    # successful server delivery, therefore preserve the pre-purge evidence.
    $rawDeadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
    do {
        if ($null -ne $status -and [int]$status.rawWritingCount -eq 0) { break }
        Start-Sleep -Seconds 2
        $statusResponse = Invoke-HostCommand "GET_SESSION_STATUS" @{ sessionId = $localSessionId }
        $status = $statusResponse.sessionStatus
        if ($null -ne $status -and [int]$status.rawChunkCount -gt 0 -and ($null -eq $prePurgeStatus -or [int]$status.rawChunkCount -gt [int]$prePurgeStatus.rawChunkCount -or ([int]$status.rawChunkCount -eq [int]$prePurgeStatus.rawChunkCount -and [int]$status.rawWritingCount -lt [int]$prePurgeStatus.rawWritingCount))) { $prePurgeStatus = $status }
    } while ($null -ne $status -and [DateTimeOffset]::UtcNow -lt $rawDeadline)

    # LOCAL_READY is the durable raw boundary.  With the regular installed
    # release we also wait for the asynchronous encoder/archive so that the
    # duration check is meaningful.  The no-encoder smoke gate opts out with
    # -AllowPendingArchive and validates only the raw-first contract.
    $archiveDeadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
    $archivePath = $null
    $archiveExists = $false
    $archiveFiles = @()
    $flacFiles = @()
    $masterPath = $null
    do {
        $archivePath = [string]$status.archivePath
        $archiveExists = -not [string]::IsNullOrWhiteSpace($archivePath) -and (Test-Path -LiteralPath $archivePath -PathType Container)
        $archiveFiles = if ($archiveExists) { @(Get-ChildItem -LiteralPath $archivePath -Recurse -File -ErrorAction SilentlyContinue) } else { @() }
        $flacFiles = @($archiveFiles | Where-Object Extension -ieq ".flac")
        $masterPath = if ($archiveExists) { Join-Path $archivePath "export\master.flac" } else { $null }
        $archiveReady = $archiveExists -and $flacFiles.Count -gt 0 -and $masterPath -and (Test-Path -LiteralPath $masterPath -PathType Leaf)
        if ($AllowPendingArchive -or $archiveReady -or [DateTimeOffset]::UtcNow -ge $archiveDeadline) { break }
        Start-Sleep -Seconds 2
        $statusResponse = Invoke-HostCommand "GET_SESSION_STATUS" @{ sessionId = $localSessionId }
        $status = $statusResponse.sessionStatus
    } while ($null -ne $status)
    $durationSeconds = $null
    if ($masterPath -and (Test-Path -LiteralPath $masterPath -PathType Leaf)) {
        $ffprobePath = Join-Path ([IO.Path]::GetDirectoryName($hostPath)) "ffprobe.exe"
        if (Test-Path -LiteralPath $ffprobePath -PathType Leaf) {
            $durationText = (& $ffprobePath -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $masterPath 2>$null | Select-Object -First 1)
            $parsedDuration = 0d
            if ([double]::TryParse([string]$durationText, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedDuration)) {
                $durationSeconds = [math]::Round($parsedDuration, 3)
            }
        }
    }
    $durationDeltaSeconds = if ($null -eq $durationSeconds) { $null } else { [math]::Round([math]::Abs($durationSeconds - $Seconds), 3) }
    $attempt = if ($null -ne $postStopHealth.health.lastAudioGraphAttempt) { $postStopHealth.health.lastAudioGraphAttempt } else { $null }
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
        host = [ordered]@{ path = $hostPath; processId = $hostProcessId; buildIdentity = $hostBuild; developmentMode = [bool]$DevelopmentHost }
        result = [ordered]@{
            firstFrameConfirmed = ([string]$start.state -match "RECORDING")
            localFinalizeState = [string]$status.localFinalizeState
            localChunkCount = [int]$status.localChunkCount
            rawChunkCount = [int]$status.rawChunkCount
            rawWritingCount = [int]$status.rawWritingCount
            rawReadyCount = [int]$status.rawReadyCount
            rawEncodingCount = [int]$status.rawEncodingCount
            rawFailedCount = [int]$status.rawFailedCount
            rawTerminalFailedCount = [int]$status.rawTerminalFailedCount
            rawBacklogHealth = [string]$status.rawBacklogHealth
            pipelineOverruns = if ($null -ne $attempt) { [int]$attempt.pipelineOverruns } else { 0 }
            framesProduced = if ($null -ne $attempt) { [int64]$attempt.framesProduced } else { 0 }
            framesConsumed = if ($null -ne $attempt) { [int64]$attempt.framesConsumed } else { 0 }
            currentQueueDepth = if ($null -ne $attempt) { [int]$attempt.currentQueueDepth } else { 0 }
            maximumQueueDepth = if ($null -ne $attempt) { [int]$attempt.maximumQueueDepth } else { 0 }
            frameQueueCapacity = 256
            framesBalanced = $null -ne $attempt -and [int64]$attempt.framesProduced -eq [int64]$attempt.framesConsumed
            frameQueueWithinLimit = $null -ne $attempt -and [int]$attempt.maximumQueueDepth -lt 192
            archiveExists = $archiveExists
            archiveReady = $archiveReady
            archiveState = [string]$status.archiveState
            playableAudioState = $playableState
            playableAudioPath = [string]$status.playableAudioPath
            playableAudioFileCount = $playableFiles.Count
            playableAudioFiles = @($playableFiles | ForEach-Object {
                [ordered]@{ trackType = [string]$_.trackType; localPath = [string]$_.localPath; sizeBytes = [int64]$_.sizeBytes; sampleCount = [int64]$_.sampleCount }
            })
            playableFormatValid = $playableFormatValid
            playableReady = $playableReady
            encodingState = [string]$status.encodingState
            flacFileCount = $flacFiles.Count
            durationSeconds = $durationSeconds
            durationDeltaSeconds = $durationDeltaSeconds
            durationWithinTolerance = $null -ne $durationDeltaSeconds -and $durationDeltaSeconds -le 0.1
            deliveryState = [string]$status.deliveryState
            errorCode = [string]$status.errorCode
            serverSessionId = [string]$status.serverSessionId
            meetingId = [string]$status.meetingId
            mediaAssetId = [string]$status.mediaAssetId
            processingJobId = [string]$status.processingJobId
            traceId = [string]$status.traceId
            prePurgeEvidence = if ($null -ne $prePurgeStatus) { [ordered]@{
                localChunkCount = [int]$prePurgeStatus.localChunkCount
                rawChunkCount = [int]$prePurgeStatus.rawChunkCount
                rawWritingCount = [int]$prePurgeStatus.rawWritingCount
                rawReadyCount = [int]$prePurgeStatus.rawReadyCount
                rawTerminalFailedCount = [int]$prePurgeStatus.rawTerminalFailedCount
                deliveryState = [string]$prePurgeStatus.deliveryState
            } } else { $null }
        }
        safety = [ordered]@{ credentialsIncluded = $false; tokensIncluded = $false; audioIncluded = $false; transcriptIncluded = $false }
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "AudioGraph local report: $reportPath"
    $deliveryFailed = $report.result.deliveryState -in @("DELIVERY_FAILED", "MEETING_NOT_FOUND")
    $deliveryIncomplete = $ServerDelivery -and $report.result.deliveryState -ne "CONFIRMED"
    $archiveRequired = -not $AllowPendingArchive
    $rawEvidence = if ($null -ne $report.result.prePurgeEvidence) { $report.result.prePurgeEvidence } else { $report.result }
    $pipelineGatePassed = $report.result.framesBalanced -and $report.result.frameQueueWithinLimit -and $report.result.pipelineOverruns -eq 0
    $rawGatePassed = $report.result.localFinalizeState -eq "LOCAL_READY" -and $rawEvidence.rawChunkCount -gt 0 -and $rawEvidence.rawWritingCount -eq 0 -and ([int]$rawEvidence.rawTerminalFailedCount -eq 0) -and $pipelineGatePassed
    $archiveGatePassed = -not $archiveRequired -or ($report.result.flacFileCount -gt 0 -and $report.result.archiveReady -and $report.result.durationWithinTolerance)
    $localChunkGatePassed = $AllowPendingArchive ? $rawGatePassed : $rawEvidence.localChunkCount -gt 0
    if (-not $report.result.firstFrameConfirmed -or $report.result.localFinalizeState -ne "LOCAL_READY" -or -not $localChunkGatePassed -or -not $report.result.playableReady -or -not $archiveGatePassed -or $deliveryFailed -or $deliveryIncomplete -or -not $pipelineGatePassed) {
        if ($deliveryIncomplete) { throw "AUDIOGRAPH_SERVER_DELIVERY_GATE_FAILED: deliveryState=$($report.result.deliveryState) error=$($report.result.errorCode) report=$reportPath" }
        throw "AUDIOGRAPH_LOCAL_RECORDING_GATE_FAILED: report=$reportPath"
    }
}
finally {
    if ($hostWasStarted -and $StopHost) {
        if ($null -eq $hostProcessId) {
            $pidPath = Join-Path $repo "artifacts\runtime\recorder-host.pid"
            if ($DevelopmentHost -and (Test-Path -LiteralPath $pidPath -PathType Leaf)) {
                $hostProcessId = [int](Get-Content -LiteralPath $pidPath -Raw)
            }
        }
        if ($null -ne $hostProcessId) {
            try {
                $process = Get-Process -Id ([int]$hostProcessId) -ErrorAction Stop
                $actualPath = $null
                try { $actualPath = [IO.Path]::GetFullPath($process.Path) } catch { }
                if ([string]::IsNullOrWhiteSpace($hostPath) -or [string]::IsNullOrWhiteSpace($actualPath) -or [IO.Path]::GetFullPath($hostPath) -eq $actualPath) {
                    Stop-Process -Id ([int]$hostProcessId) -Force -ErrorAction SilentlyContinue
                }
            }
            catch { }
        }
    }
}

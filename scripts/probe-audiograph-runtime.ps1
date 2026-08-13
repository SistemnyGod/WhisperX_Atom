[CmdletBinding()]
param(
    [string]$DeviceId = "",
    [ValidateRange(1, 10)]
    [int]$Seconds = 3,
    [switch]$StartHost,
    [string]$OutputPath = "",
    [string]$DataRoot = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pipeName = "WhisperXAtomRecorderHost"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repo "artifacts\audio-runtime\audiograph-report.json"
}

function Invoke-HostCommand([string]$Command, [hashtable]$Payload = @{}) {
    # RecorderHostPipeSecurity is the authoritative boundary (current SID and
    # Administrators). CurrentUserOnly on the client can reject an otherwise
    # valid same-user elevated/non-elevated connection on Windows.
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $request = [ordered]@{ command = $Command; protocolVersion = 6; payload = $Payload }
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 8))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "AUDIOGRAPH_EMPTY_RESPONSE: $Command" }
        return ($line | ConvertFrom-Json)
    }
    finally { $pipe.Dispose() }
}

function Get-HostHealth {
    try { return Invoke-HostCommand "HEALTH" } catch { return $null }
}

if ($StartHost -and $null -eq (Get-HostHealth)) {
    $previousEngine = $env:AUDIO_CAPTURE_ENGINE
    $env:AUDIO_CAPTURE_ENGINE = "AUDIOGRAPH"
    try {
        $startArgs = @{ ReadyTimeoutSeconds = 20 }
        if (-not [string]::IsNullOrWhiteSpace($DataRoot)) { $startArgs.DataRoot = $DataRoot }
        & (Join-Path $repo "scripts\start-recorder-host.ps1") @startArgs
        if ($LASTEXITCODE -ne 0) { throw "AUDIOGRAPH_HOST_START_FAILED" }
    }
    finally { $env:AUDIO_CAPTURE_ENGINE = $previousEngine }
}

$health = Get-HostHealth
$payload = @{}
if (-not [string]::IsNullOrWhiteSpace($DeviceId)) { $payload.deviceId = $DeviceId }
$payload.durationMs = $Seconds * 1000
$probe = Invoke-HostCommand "TEST_AUDIO_SOURCE" $payload
$graphProbe = $probe.audioGraphProbe
$selected = if ($null -ne $graphProbe) { $graphProbe } else { $probe.audioSourceTest }
$attempt = if ($null -ne $selected) { $selected.attemptDiagnostics } else { $null }
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$sessionId = (Get-Process -Id $PID).SessionId
$devices = @()
if ($null -ne $health -and $null -ne $health.health -and $null -ne $health.health.captureDevices) {
    $devices = @($health.health.captureDevices | ForEach-Object {
        [ordered]@{ id = $_.id; name = $_.name; state = $_.state; isDefault = $_.isDefault; lastSeenAtUtc = $_.lastSeenAtUtc }
    })
}

$report = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow
    process = [ordered]@{ user = $user.Name; sid = $user.User.Value; windowsSessionId = $sessionId; processId = $PID }
    capture = [ordered]@{
        engine = "AUDIOGRAPH"
        processModel = "CURRENT_USER_HOST"
        selectionMode = if ([string]::IsNullOrWhiteSpace($DeviceId)) { "DEFAULT" } else { "FIXED" }
        requestedDeviceId = if ([string]::IsNullOrWhiteSpace($DeviceId)) { $null } else { $DeviceId }
        selectedDeviceId = if ($null -ne $selected) { $selected.deviceId } else { $null }
        selectedDeviceName = if ($null -ne $selected) { $selected.deviceName } else { $null }
        graphStatus = if ($null -ne $health -and $null -ne $health.health) { $health.health.audioGraphReady } else { $false }
        deviceWatcherReady = if ($null -ne $health -and $null -ne $health.health) { $health.health.deviceWatcherReady } else { $false }
        captureState = if ($null -ne $probe) { $probe.state } else { $null }
        errorCode = if ($null -ne $selected) { $selected.errorCode } else { $probe.error }
        errorDetail = if ($null -ne $selected) { $selected.errorDetail } else { $null }
        frameCount = if ($null -ne $selected) { $selected.frameCount } else { $null }
        bytes = if ($null -ne $selected) { $selected.bytesReceived } else { $null }
        bytesReceived = if ($null -ne $attempt) { $attempt.bytesReceived } elseif ($null -ne $selected) { $selected.bytesReceived } else { 0 }
        averageRmsDb = if ($null -ne $selected) { $selected.averageRmsDb } else { $null }
        peakDb = if ($null -ne $selected) { $selected.peakDb } else { $null }
        durationMs = $Seconds * 1000
        graphCreationStatus = if ($null -ne $attempt) { $attempt.graphCreationStatus } else { $null }
        graphCreated = if ($null -ne $attempt) { $attempt.graphCreated } else { $false }
        graphExtendedErrorHResult = if ($null -ne $attempt) { $attempt.graphExtendedErrorHResult } else { $null }
        inputNodeCreationStatus = if ($null -ne $attempt) { $attempt.inputNodeCreationStatus } else { $null }
        inputNodeCreated = if ($null -ne $attempt) { $attempt.inputNodeCreated } else { $false }
        inputNodeExtendedErrorHResult = if ($null -ne $attempt) { $attempt.inputNodeExtendedErrorHResult } else { $null }
        outputNodeCreated = if ($null -ne $attempt) { $attempt.outputNodeCreated } else { $false }
        connectionCreated = if ($null -ne $attempt) { $attempt.connectionCreated } else { $false }
        graphStartCalled = if ($null -ne $attempt) { $attempt.graphStartCalled } else { $false }
        quantumStartedCount = if ($null -ne $attempt) { $attempt.quantumStartedCount } else { 0 }
        getFrameCallCount = if ($null -ne $attempt) { $attempt.getFrameCallCount } else { 0 }
        emptyFrameCount = if ($null -ne $attempt) { $attempt.emptyFrameCount } else { 0 }
        nonEmptyFrameCount = if ($null -ne $attempt) { $attempt.nonEmptyFrameCount } else { 0 }
        audioBufferLengthLast = if ($null -ne $attempt) { $attempt.audioBufferLengthLast } else { $null }
        audioBufferCapacityLast = if ($null -ne $attempt) { $attempt.audioBufferCapacityLast } else { $null }
        outputSubtype = if ($null -ne $attempt) { $attempt.outputSubtype } else { $null }
        outputBitsPerSample = if ($null -ne $attempt) { $attempt.outputBitsPerSample } else { $null }
        outputSampleRate = if ($null -ne $attempt) { $attempt.outputSampleRate } else { $null }
        outputChannelCount = if ($null -ne $attempt) { $attempt.outputChannelCount } else { $null }
        graphSamplesPerQuantum = if ($null -ne $attempt) { $attempt.graphSamplesPerQuantum } else { $null }
        nativeFrameBytes = if ($null -ne $attempt) { $attempt.nativeFrameBytes } else { 0 }
        normalizedFrameBytes = if ($null -ne $attempt) { $attempt.normalizedFrameBytes } else { 0 }
        normalizationMode = if ($null -ne $attempt) { $attempt.normalizationMode } else { $null }
        firstQuantumLatencyMs = if ($null -ne $attempt) { $attempt.firstQuantumLatencyMs } else { $null }
        firstFrameLatencyMs = if ($null -ne $attempt -and $null -ne $attempt.firstFrameLatencyMs) { $attempt.firstFrameLatencyMs } elseif ($null -ne $selected) { $selected.firstFrameLatencyMs } else { $null }
        unrecoverableErrorOccurred = if ($null -ne $attempt) { $attempt.unrecoverableErrorOccurred } else { $false }
        unrecoverableErrorHResult = if ($null -ne $attempt) { $attempt.unrecoverableErrorHResult } else { $null }
        finalCaptureState = if ($null -ne $attempt) { $attempt.finalCaptureState } else { $null }
        finalErrorCode = if ($null -ne $attempt) { $attempt.finalErrorCode } else { $null }
        finalErrorDetail = if ($null -ne $attempt) { $attempt.finalErrorDetail } else { $null }
        attemptDiagnostics = $attempt
        normalizedSampleFormat = if ($null -ne $selected) { $selected.normalizedSampleFormat } else { $null }
        sampleRate = if ($null -ne $selected) { $selected.sampleRate } else { $null }
        channels = if ($null -ne $selected) { $selected.channels } else { $null }
        devices = $devices
    }
    gates = [ordered]@{
        AUDIOGRAPH_DEVICE_DISCOVERY_READY = ($null -ne $health -and $health.health.deviceWatcherReady -eq $true -and $devices.Count -gt 0)
        AUDIOGRAPH_DEFAULT_DEVICE_READY = ([string]::IsNullOrWhiteSpace($DeviceId) -and $null -ne $selected -and $selected.endpointFound -eq $true)
        AUDIOGRAPH_FIXED_DEVICE_READY = ([string]::IsNullOrWhiteSpace($DeviceId) -or ($null -ne $selected -and $selected.deviceId -eq $DeviceId -and $selected.endpointFound -eq $true))
        AUDIOGRAPH_PERMISSION_DIAGNOSTICS_READY = ($null -ne $selected -and $null -ne $selected.accessGranted)
        AUDIOGRAPH_GRAPH_READY = ($null -ne $attempt -and $attempt.graphCreated -eq $true)
        AUDIOGRAPH_INPUT_NODE_READY = ($null -ne $attempt -and $attempt.inputNodeCreated -eq $true)
        AUDIOGRAPH_OUTPUT_NODE_READY = ($null -ne $attempt -and $attempt.outputNodeCreated -eq $true -and $attempt.connectionCreated -eq $true)
        AUDIOGRAPH_QUANTUM_READY = ($null -ne $attempt -and $attempt.quantumStartedCount -gt 0)
        AUDIOGRAPH_FIRST_FRAME_READY = ($null -ne $attempt -and $attempt.nonEmptyFrameCount -gt 0 -and $attempt.bytesReceived -gt 0)
        AUDIOGRAPH_TELEMETRY_READY = ($null -ne $attempt -and $attempt.quantumStartedCount -gt 0 -and $null -ne $attempt.firstQuantumLatencyMs)
    }
    safety = [ordered]@{ secretsIncluded = $false; audioContentIncluded = $false; transcriptIncluded = $false }
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "AudioGraph runtime report: $OutputPath"
if ($report.capture.errorCode) { Write-Host "AUDIOGRAPH_PROBE_FAILED: $($report.capture.errorCode)" -ForegroundColor Red; exit 1 }
if ($report.gates.AUDIOGRAPH_FIRST_FRAME_READY -ne $true) { Write-Host "AUDIOGRAPH_FIRST_FRAME_NOT_CONFIRMED" -ForegroundColor Yellow; exit 1 }

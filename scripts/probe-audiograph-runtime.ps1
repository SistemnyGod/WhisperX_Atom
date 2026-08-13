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
$probe = Invoke-HostCommand "TEST_AUDIO_SOURCE" $payload
$graphProbe = $probe.audioGraphProbe
$selected = if ($null -ne $graphProbe) { $graphProbe } else { $probe.audioSourceTest }
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
        firstFrameLatencyMs = if ($null -ne $selected) { $selected.firstFrameLatencyMs } else { $null }
        frameCount = if ($null -ne $selected) { $selected.frameCount } else { $null }
        bytes = if ($null -ne $selected) { $selected.bytesReceived } else { $null }
        averageRmsDb = if ($null -ne $selected) { $selected.averageRmsDb } else { $null }
        peakDb = if ($null -ne $selected) { $selected.peakDb } else { $null }
        durationMs = if ($null -ne $selected -and $null -ne $selected.durationMs) { $selected.durationMs } else { $Seconds * 1000 }
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
        AUDIOGRAPH_INPUT_NODE_READY = ($null -ne $selected -and $selected.streamOpened -eq $true)
        AUDIOGRAPH_FIRST_FRAME_READY = ($null -ne $selected -and $selected.frameCount -gt 0 -and $selected.bytesReceived -gt 0)
        AUDIOGRAPH_TELEMETRY_READY = ($null -ne $selected -and $null -ne $selected.averageRmsDb)
    }
    safety = [ordered]@{ secretsIncluded = $false; audioContentIncluded = $false; transcriptIncluded = $false }
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "AudioGraph runtime report: $OutputPath"
if ($report.capture.errorCode) { Write-Host "AUDIOGRAPH_PROBE_FAILED: $($report.capture.errorCode)" -ForegroundColor Red; exit 1 }
if ($report.gates.AUDIOGRAPH_FIRST_FRAME_READY -ne $true) { Write-Host "AUDIOGRAPH_FIRST_FRAME_NOT_CONFIRMED" -ForegroundColor Yellow; exit 1 }

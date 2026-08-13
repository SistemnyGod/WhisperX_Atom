[CmdletBinding()]
param(
    [ValidateSet("LegacyWasapi", "AudioGraph")]
    [string]$CaptureEngine = "AudioGraph",
    [ValidateRange(10, 300)]
    [int]$Seconds = 30,
    [ValidateRange(10, 180)]
    [int]$FinalizeTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($CaptureEngine -eq "AudioGraph") {
    $audioGraphGate = Join-Path $repo "scripts\acceptance-audiograph-local-recording.ps1"
    & $audioGraphGate -Seconds $Seconds -FinalizeTimeoutSeconds $FinalizeTimeoutSeconds
    exit $LASTEXITCODE
}
$serviceName = "WhisperXAtomRecorder"
$pipeName = "WhisperXAtomAgent"
$artifactRoot = Join-Path $repo "artifacts\acceptance\recorder-local"
$reportPath = Join-Path $artifactRoot ("report-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
$serviceConfig = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
if ($null -eq $serviceConfig) { throw "RECORDER_SERVICE_NOT_FOUND: $serviceName" }

$previousStartMode = $serviceConfig.StartMode
$wasRunning = $serviceConfig.State -eq "Running"
$localSessionId = $null
$stopRequested = $false

function Invoke-AgentCommand([string]$Command, [hashtable]$Payload = @{}) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $request = [ordered]@{ command = $Command; payload = $Payload }
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 8))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "RECORDER_IPC_EMPTY_RESPONSE: $Command" }
        return ($line | ConvertFrom-Json)
    }
    finally { $pipe.Dispose() }
}

function Restore-ServiceState {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    switch ($previousStartMode) {
        "Auto" { Set-Service -Name $serviceName -StartupType Automatic -ErrorAction SilentlyContinue }
        "Manual" { Set-Service -Name $serviceName -StartupType Manual -ErrorAction SilentlyContinue }
        "Disabled" { Set-Service -Name $serviceName -StartupType Disabled -ErrorAction SilentlyContinue }
        default { Set-Service -Name $serviceName -StartupType Manual -ErrorAction SilentlyContinue }
    }
    if ($wasRunning) { Start-Service -Name $serviceName -ErrorAction SilentlyContinue }
}

try {
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    if ($serviceConfig.State -ne "Running") {
        if ($serviceConfig.StartMode -eq "Disabled") { Set-Service -Name $serviceName -StartupType Manual }
        Start-Service -Name $serviceName
    }
    (Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(20))

    # This gate is intentionally server-independent. A local session must not
    # carry a fabricated server Meeting id; the Agent creates/binds a real
    # Meeting only when delivery is explicitly attempted after recovery.
    $meetingId = $null
    $start = Invoke-AgentCommand "START" @{ title = "LOCAL_CAPTURE_GATE"; localOnly = $true }
    if ($start.ok -ne $true) { throw "LOCAL_CAPTURE_START_FAILED: $($start.error)" }
    $localSessionId = [string]$start.sessionId
    if ([string]::IsNullOrWhiteSpace($localSessionId)) { throw "LOCAL_CAPTURE_SESSION_ID_MISSING" }
    if ([string]$start.state -notmatch "RECORDING") { throw "FIRST_PACKET_GATE_NOT_CONFIRMED: state=$($start.state)" }

    Start-Sleep -Seconds $Seconds
    $stop = Invoke-AgentCommand "STOP"
    $stopRequested = $true
    if ($stop.ok -ne $true) { throw "LOCAL_CAPTURE_STOP_FAILED: $($stop.error)" }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($FinalizeTimeoutSeconds)
    $status = $null
    do {
        Start-Sleep -Seconds 2
        $response = Invoke-AgentCommand "GET_SESSION_STATUS" @{ sessionId = $localSessionId }
        $status = $response.sessionStatus
        if ($null -ne $status -and $status.localFinalizeState -in @("LOCAL_READY", "COMPLETED", "FAILED", "LOCAL_FAILED")) { break }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ($null -eq $status) { throw "LOCAL_CAPTURE_STATUS_MISSING" }
    $archivePath = [string]$status.archivePath
    $archiveExists = -not [string]::IsNullOrWhiteSpace($archivePath) -and (Test-Path -LiteralPath $archivePath -PathType Container)
    $archiveFiles = if ($archiveExists) { @(Get-ChildItem -LiteralPath $archivePath -Recurse -File -ErrorAction SilentlyContinue) } else { @() }
    $flacFiles = @($archiveFiles | Where-Object Extension -ieq ".flac")
    $archiveBytes = [long](($archiveFiles | Measure-Object -Property Length -Sum).Sum)
    $report = [ordered]@{
        schemaVersion = 1
        gate = "LOCAL_RECORDING"
        generatedAtUtc = [DateTimeOffset]::UtcNow
        captureEngine = "LEGACY_WASAPI"
        serverUsed = $false
        service = [ordered]@{ name = $serviceName; account = $serviceConfig.StartName; initialState = $serviceConfig.State; initialStartMode = $previousStartMode }
        session = [ordered]@{ localSessionId = $localSessionId; meetingId = $meetingId; requestedSeconds = $Seconds; startState = [string]$start.state; stopState = [string]$stop.state }
        result = [ordered]@{
            firstPacketConfirmed = ([string]$start.state -match "RECORDING")
            localFinalizeState = [string]$status.localFinalizeState
            localChunkCount = [int]$status.localChunkCount
            archiveExists = $archiveExists
            flacFileCount = $flacFiles.Count
            archiveBytes = $archiveBytes
            deliveryState = [string]$status.deliveryState
            errorCode = [string]$status.errorCode
        }
        safety = [ordered]@{ credentialsIncluded = $false; tokensIncluded = $false; audioIncluded = $false; transcriptIncluded = $false }
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "Local recording report: $reportPath"
    if (-not $report.result.firstPacketConfirmed -or $report.session.meetingId -ne $null -or $report.result.localFinalizeState -ne "LOCAL_READY" -or $report.result.localChunkCount -le 0 -or $report.result.flacFileCount -le 0 -or -not $report.result.archiveExists -or $report.result.deliveryState -in @("DELIVERY_FAILED", "MEETING_NOT_FOUND")) {
        throw "LOCAL_RECORDING_GATE_FAILED: report=$reportPath"
    }
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($localSessionId) -and -not $stopRequested) {
        try { Invoke-AgentCommand "STOP" | Out-Null } catch { }
    }
    throw
}
finally {
    Restore-ServiceState
}

[CmdletBinding()]
param(
    [ValidateSet("BeforeReboot", "AfterReboot")]
    [string]$Phase = "BeforeReboot",
    [string]$MarkerPath = "",
    [string]$OutputRoot = "",
    [string]$ServerOrigin = "",
    [switch]$RequireServer,
    [switch]$RequireDocker
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo "artifacts\acceptance\windows-reboot-recovery"
}
if ([string]::IsNullOrWhiteSpace($MarkerPath)) {
    $MarkerPath = Join-Path $OutputRoot "reboot-marker.json"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$MarkerPath = [IO.Path]::GetFullPath($MarkerPath)
$reportPath = Join-Path $OutputRoot ("{0}-{1}.json" -f $Phase, (Get-Date -Format "yyyyMMdd-HHmmss"))
$recorderPipeName = "WhisperXAtomRecorderHost"
$voicePipeName = "WhisperXAtomVoiceHost"

function Invoke-PipeCommand(
    [Parameter(Mandatory = $true)][string]$PipeName,
    [Parameter(Mandatory = $true)][string]$Command,
    [hashtable]$Payload = @{},
    [int]$TimeoutMs = 5000) {
    if ([string]::IsNullOrWhiteSpace($PipeName) -or [string]::IsNullOrWhiteSpace($Command)) {
        throw "IPC_PIPE_OR_COMMAND_REQUIRED"
    }
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect($TimeoutMs)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine(([ordered]@{ command = $Command; protocolVersion = 6; payload = $Payload } | ConvertTo-Json -Compress -Depth 8))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "IPC_EMPTY_RESPONSE:$Command" }
        return $line | ConvertFrom-Json
    }
    finally { $pipe.Dispose() }
}

function Get-RedactedSessions($response) {
    if ($null -eq $response -or $response.ok -ne $true) { return @() }
    return @($response.localSessions | ForEach-Object {
        [ordered]@{
            sessionId = [string]$_.sessionId
            state = [string]$_.state
            localFinalizeState = [string]$_.localFinalizeState
            deliveryState = [string]$_.deliveryState
            playableAudioState = [string]$_.playableAudioState
        }
    })
}

function Get-HostEvidence {
    $health = Invoke-PipeCommand -PipeName $recorderPipeName -Command "HEALTH"
    $sessions = Invoke-PipeCommand -PipeName $recorderPipeName -Command "LIST_LOCAL_SESSIONS" -Payload @{ limit = 500 }
    $ids = @(Get-RedactedSessions $sessions | ForEach-Object { $_.sessionId } | Where-Object { $_ })
    $duplicates = @($ids | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    [ordered]@{
        reachable = $health.ok -eq $true
        state = [string]$health.state
        protocolVersion = [int]$health.protocolVersion
        runtimeBuildIdentity = [string]$health.health.runtimeBuildIdentity
        serverConnectionState = [string]$health.health.serverConnectionState
        sessionCount = $ids.Count
        duplicateSessionIds = $duplicates
        sessions = @(Get-RedactedSessions $sessions)
    }
}

function Get-VoiceEvidence {
    try {
        $status = Invoke-PipeCommand -PipeName $voicePipeName -Command "STATUS" -TimeoutMs 3000
        [ordered]@{
            reachable = $status.ok -eq $true
            state = [string]$status.data.state
            buildIdentity = [string]$status.data.buildIdentity
            ttsEngine = [string]$status.data.ttsEngine
            ttsReady = [bool]$status.data.ttsReady
            recorderPipeReady = [bool]$status.data.recorderPipeReady
        }
    }
    catch {
        [ordered]@{ reachable = $false; state = "UNAVAILABLE"; errorCode = "VOICE_HOST_UNAVAILABLE" }
    }
}

function Get-DockerEvidence {
    if (-not (Get-Command docker.exe -ErrorAction SilentlyContinue)) {
        return [ordered]@{ available = $false; errorCode = "DOCKER_UNAVAILABLE" }
    }
    try {
        $rows = @(docker ps --format '{{.Names}}|{{.Image}}|{{.Status}}' 2>$null)
        if ($LASTEXITCODE -ne 0) { throw "docker_ps_failed" }
        return [ordered]@{ available = $true; containers = @($rows | ForEach-Object { $_.ToString() }) }
    }
    catch { return [ordered]@{ available = $false; errorCode = "DOCKER_NOT_READY" } }
}

function Get-ServerEvidence {
    if ([string]::IsNullOrWhiteSpace($ServerOrigin)) {
        return [ordered]@{ checked = $false; reason = "SERVER_ORIGIN_NOT_PROVIDED" }
    }
    $result = [ordered]@{ checked = $true; live = $false; ready = $false }
    try { $result.live = (Invoke-WebRequest -UseBasicParsing -Uri "$($ServerOrigin.TrimEnd('/'))/health/live" -TimeoutSec 5).StatusCode -eq 200 } catch { }
    try { $result.ready = (Invoke-WebRequest -UseBasicParsing -Uri "$($ServerOrigin.TrimEnd('/'))/health/ready" -TimeoutSec 5).StatusCode -eq 200 } catch { }
    return $result
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
if ($Phase -eq "BeforeReboot") {
    $hostEvidence = Get-HostEvidence
    $voiceEvidence = Get-VoiceEvidence
    $dockerEvidence = Get-DockerEvidence
    $serverEvidence = Get-ServerEvidence
    $report = [ordered]@{
        schemaVersion = 1
        gate = "WINDOWS_REBOOT_RECOVERY"
        phase = "BEFORE_REBOOT"
        generatedAtUtc = [DateTimeOffset]::UtcNow
        host = $hostEvidence
        voice = $voiceEvidence
        docker = $dockerEvidence
        server = $serverEvidence
        safety = [ordered]@{ rebootRequested = $false; containersStopped = $false; audioIncluded = $false; tokensIncluded = $false }
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $MarkerPath -Encoding utf8
    if ($hostEvidence.duplicateSessionIds.Count -gt 0) { throw "REBOOT_GATE_DUPLICATE_SESSION_IDS" }
    Write-Host "Before-reboot evidence: $reportPath"
    Write-Host "Перезагрузка не выполняется автоматически. После штатной перезагрузки запустите этот скрипт с -Phase AfterReboot -MarkerPath `"$MarkerPath`"."
    exit 0
}

if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) { throw "REBOOT_GATE_MARKER_MISSING: $MarkerPath" }
$before = Get-Content -LiteralPath $MarkerPath -Raw | ConvertFrom-Json
$afterHost = Get-HostEvidence
$afterVoice = Get-VoiceEvidence
$afterDocker = Get-DockerEvidence
$afterServer = Get-ServerEvidence
$beforeIds = @($before.host.sessions | ForEach-Object { [string]$_.sessionId } | Where-Object { $_ })
$afterIds = @($afterHost.sessions | ForEach-Object { [string]$_.sessionId } | Where-Object { $_ })
$missingIds = @($beforeIds | Where-Object { $_ -notin $afterIds })
$duplicateIds = @($afterHost.duplicateSessionIds)
$hostRecovered = $afterHost.reachable -and $afterHost.protocolVersion -ge 5
$serverRecovered = (-not $RequireServer) -or ($afterServer.live -and $afterServer.ready)
$dockerRecovered = (-not $RequireDocker) -or $afterDocker.available
$passed = $hostRecovered -and $serverRecovered -and $dockerRecovered -and $missingIds.Count -eq 0 -and $duplicateIds.Count -eq 0
$report = [ordered]@{
    schemaVersion = 1
    gate = "WINDOWS_REBOOT_RECOVERY"
    phase = "AFTER_REBOOT"
    generatedAtUtc = [DateTimeOffset]::UtcNow
    result = if ($passed) { "PASSED" } else { "FAILED" }
    checks = [ordered]@{
        hostRecovered = $hostRecovered
        voiceReachable = $afterVoice.reachable
        serverRecovered = $serverRecovered
        dockerRecovered = $dockerRecovered
        localSessionIdsPreserved = $missingIds.Count -eq 0
        noDuplicateSessionIds = $duplicateIds.Count -eq 0
    }
    missingSessionIds = $missingIds
    before = $before.host
    after = $afterHost
    voice = $afterVoice
    docker = $afterDocker
    server = $afterServer
    safety = [ordered]@{ rebootRequested = $false; containersStopped = $false; audioIncluded = $false; tokensIncluded = $false }
}
$report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "After-reboot evidence: $reportPath"
if (-not $passed) { throw "REBOOT_GATE_FAILED: $reportPath" }

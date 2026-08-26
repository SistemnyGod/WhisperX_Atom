[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$Username,
    [string]$Password,
    [Parameter(Mandatory = $true)][string]$Question,
    [ValidateSet("AUTO", "GENERAL_CHAT", "CURRENT_MEETING", "LIVE_MEETING", "MEETING_MEMORY")][string]$RequestedMode = "AUTO",
    [string]$ActiveMeetingId,
    [string]$RecordingSessionId,
    [string]$CaptureState,
    [ValidateRange(50, 5000)][int]$PollIntervalMs = 100,
    [ValidateRange(5, 1800)][int]$TimeoutSeconds = 180,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = if ($env:WHISPERX_DEV_API_URL) { $env:WHISPERX_DEV_API_URL } else { "http://127.0.0.1:8080" }
}
$BaseUrl = $BaseUrl.TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($Username)) { $Username = if ($env:BOOTSTRAP_ADMIN_USERNAME) { $env:BOOTSTRAP_ADMIN_USERNAME } else { "admin" } }
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = $env:BOOTSTRAP_ADMIN_PASSWORD }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path ((Resolve-Path (Join-Path $PSScriptRoot "..")).Path) "artifacts\acceptance\mifodiy-latency.json" }

$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
function Invoke-Api([string]$Method, [string]$Path, [object]$Body = $null) {
    $parameters = @{ Uri = "$BaseUrl$Path"; Method = $Method; WebSession = $session; ErrorAction = "Stop"; TimeoutSec = 20 }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 12 -Compress
    }
    $response = Invoke-WebRequest @parameters
    [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = if ($response.Content) { $response.Content | ConvertFrom-Json } else { $null } }
}

if ([string]::IsNullOrWhiteSpace($Password)) { throw "MIFODIY_AUTH_PASSWORD_REQUIRED" }
$login = Invoke-Api POST "/api/auth/login" @{ username = $Username; password = $Password }
if ($login.StatusCode -ne 200) { throw "MIFODIY_AUTH_REJECTED:$($login.StatusCode)" }

$sha = [Security.Cryptography.SHA256]::Create()
$questionHash = ([Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Question)))).ToLowerInvariant()
$commandId = [Guid]::NewGuid().ToString("N")
$traceId = [Guid]::NewGuid().ToString("N")
$body = [ordered]@{
    question = $Question
    requestedMode = $RequestedMode
    source = "DESKTOP"
    activeMeetingId = if ($ActiveMeetingId) { $ActiveMeetingId } else { $null }
    recordingSessionId = if ($RecordingSessionId) { $RecordingSessionId } else { $null }
    captureState = if ($CaptureState) { $CaptureState } else { $null }
    commandId = $commandId
    traceId = $traceId
}

$events = [System.Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::StartNew()
$events.Add([ordered]@{ stage = "UTTERANCE_SUBMITTED"; elapsedMs = 0 })
$accepted = Invoke-Api POST "/api/assistant/requests" $body
$acceptedAt = $watch.Elapsed.TotalMilliseconds
if ($accepted.StatusCode -ne 202) { throw "MIFODIY_REQUEST_NOT_ACCEPTED:$($accepted.StatusCode)" }
$queryId = [string]$accepted.Body.queryId
if ([string]::IsNullOrWhiteSpace($queryId)) { throw "MIFODIY_QUERY_ID_MISSING" }
$events.Add([ordered]@{ stage = "REQUEST_ACCEPTED"; elapsedMs = [math]::Round($acceptedAt, 1); queryId = $queryId })

$terminal = @("READY", "ANSWERED", "ANSWERED_WITH_WARNING", "FAILED", "NEEDS_REVIEW", "NO_EVIDENCE", "GROUNDING_REJECTED", "LLM_UNAVAILABLE")
$lastStage = $null
$query = $null
try {
    do {
        $poll = Invoke-Api GET "/api/assistant/queries/$queryId"
        $query = $poll.Body
        $stage = [string]($query.processingStage ?? $query.status)
        if ($stage -and $stage -ne $lastStage) {
            $events.Add([ordered]@{ stage = $stage; elapsedMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 1) })
            $lastStage = $stage
        }
        if ($terminal -contains [string]$query.status) { break }
        Start-Sleep -Milliseconds $PollIntervalMs
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
}
finally { $watch.Stop() }

$status = if ($query -and $terminal -contains [string]$query.status) { "PASS" } else { "TIMEOUT" }
$timings = if ($query) { $query.timings } else { $null }
$firstAudio = $null
if ($query) {
    foreach ($name in @("firstAudioAt", "ttsStartedAt", "playbackStartedAt", "firstAudioPlayedAt")) {
        if ($query.PSObject.Properties.Name -contains $name -and $query.$name) { $firstAudio = $query.$name; break }
    }
    if (-not $firstAudio -and $timings) {
        foreach ($name in @("first_audio_ms", "time_to_first_audio_ms", "tts_started_ms")) {
            if ($timings.PSObject.Properties.Name -contains $name) { $firstAudio = $timings.$name; break }
        }
    }
}
$result = [ordered]@{
    schema = "mifodiy-latency-v1"
    status = $status
    capturedAtUtc = [DateTimeOffset]::UtcNow
    questionSha256 = $questionHash
    requestedMode = $RequestedMode
    queryId = $queryId
    commandId = $commandId
    traceId = $traceId
    pollIntervalMs = $PollIntervalMs
    acceptedMs = [math]::Round($acceptedAt, 1)
    totalMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 1)
    timeToFirstAudio = $firstAudio
    finalStatus = if ($query) { [string]$query.status } else { $null }
    finalProcessingStage = if ($query) { [string]$query.processingStage } else { $null }
    timings = $timings
    events = $events
}
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json -Depth 16
if ($status -ne "PASS") { exit 4 }

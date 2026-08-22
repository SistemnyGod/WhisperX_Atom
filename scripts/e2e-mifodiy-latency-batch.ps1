[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$Username,
    [string]$Password,
    [Parameter(Mandatory = $true)][string[]]$Question,
    [ValidateSet("AUTO", "GENERAL_CHAT", "CURRENT_MEETING", "LIVE_MEETING", "MEETING_MEMORY")][string]$RequestedMode = "AUTO",
    [ValidateRange(1, 100)][int]$Runs = 30,
    [ValidateRange(50, 1000)][int]$PollIntervalMs = 100,
    [ValidateRange(10, 900)][int]$TimeoutSeconds = 180,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($BaseUrl)) { $BaseUrl = if ($env:WHISPERX_DEV_API_URL) { $env:WHISPERX_DEV_API_URL } else { "http://127.0.0.1:8080" } }
$BaseUrl = $BaseUrl.TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($Username)) { $Username = if ($env:BOOTSTRAP_ADMIN_USERNAME) { $env:BOOTSTRAP_ADMIN_USERNAME } else { "admin" } }
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = $env:BOOTSTRAP_ADMIN_PASSWORD }
if ([string]::IsNullOrWhiteSpace($Password)) { throw "MIFODIY_AUTH_PASSWORD_REQUIRED" }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path ((Resolve-Path (Join-Path $PSScriptRoot "..")).Path) "artifacts\acceptance\mifodiy-latency-batch.json" }

$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
function Invoke-Api([string]$Method, [string]$Path, [object]$Body = $null) {
    $parameters = @{ Uri = "$BaseUrl$Path"; Method = $Method; WebSession = $session; ErrorAction = "Stop"; TimeoutSec = 20 }
    if ($null -ne $Body) { $parameters.ContentType = "application/json"; $parameters.Body = $Body | ConvertTo-Json -Depth 12 -Compress }
    Invoke-WebRequest @parameters | ForEach-Object { if ($_.Content) { $_.Content | ConvertFrom-Json } }
}
Invoke-Api POST "/api/auth/login" @{ username = $Username; password = $Password } | Out-Null

$terminal = @("READY", "ANSWERED", "ANSWERED_WITH_WARNING", "FAILED", "NEEDS_REVIEW", "NO_EVIDENCE", "GROUNDING_REJECTED", "LLM_UNAVAILABLE")
$rows = [System.Collections.Generic.List[object]]::new()
$sha = [Security.Cryptography.SHA256]::Create()
for ($index = 0; $index -lt $Runs; $index++) {
    $text = $Question[$index % $Question.Count]
    $questionHash = ([Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).ToLowerInvariant()
    $commandId = [Guid]::NewGuid().ToString("N")
    $traceId = [Guid]::NewGuid().ToString("N")
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $accepted = Invoke-Api POST "/api/assistant/requests" @{ question = $text; requestedMode = $RequestedMode; source = "LATENCY_GATE"; commandId = $commandId; traceId = $traceId }
    $acceptedMs = $watch.Elapsed.TotalMilliseconds
    $queryId = [string]$accepted.queryId
    $query = $null
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $query = Invoke-Api GET "/api/assistant/queries/$queryId"
        if ($terminal -contains [string]$query.status) { break }
        Start-Sleep -Milliseconds $PollIntervalMs
    }
    $watch.Stop()
    $timings = if ($query) { $query.timings } else { $null }
    $rows.Add([ordered]@{
        run = $index + 1
        questionSha256 = $questionHash
        queryId = $queryId
        commandId = $commandId
        traceId = $traceId
        acceptedMs = [math]::Round($acceptedMs, 1)
        totalMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 1)
        status = if ($query -and ($terminal -contains [string]$query.status)) { "PASS" } else { "TIMEOUT" }
        finalStatus = if ($query) { [string]$query.status } else { $null }
        processingStage = if ($query) { [string]$query.processingStage } else { $null }
        timings = $timings
    })
}

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $position = [math]::Ceiling($Percentile * $sorted.Count) - 1
    $sorted[[math]::Max(0, [math]::Min($position, $sorted.Count - 1))]
}
$acceptedValues = @($rows | ForEach-Object { [double]$_.acceptedMs })
$totalValues = @($rows | Where-Object status -eq "PASS" | ForEach-Object { [double]$_.totalMs })
$summary = [ordered]@{
    acceptedP50Ms = Get-Percentile $acceptedValues 0.50
    acceptedP95Ms = Get-Percentile $acceptedValues 0.95
    totalP50Ms = Get-Percentile $totalValues 0.50
    totalP95Ms = Get-Percentile $totalValues 0.95
    passed = @($rows | Where-Object status -eq "PASS").Count
    failed = @($rows | Where-Object status -ne "PASS").Count
}
$report = [ordered]@{ schema = "mifodiy-latency-batch-v1"; capturedAtUtc = [DateTimeOffset]::UtcNow; requestedMode = $RequestedMode; runs = $rows; summary = $summary }
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$report | ConvertTo-Json -Depth 16

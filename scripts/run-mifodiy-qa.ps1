[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CorpusPath,
    [string]$BaseUrl,
    [string]$Username,
    [string]$Password,
    [string]$OutputPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$schemaPath = Join-Path $PSScriptRoot "..\docs\mifodiy-qa-case.schema.json"
if (-not (Test-Path -LiteralPath $schemaPath)) { throw "MIFODIY_QA_SCHEMA_MISSING" }
if (-not (Test-Path -LiteralPath $CorpusPath)) { throw "MIFODIY_QA_CORPUS_MISSING" }
$corpusItem = Get-Item -LiteralPath $CorpusPath
$raw = if ($corpusItem.PSIsContainer) {
    $files = Get-ChildItem -LiteralPath $CorpusPath -Filter *.json -File
    if ($files.Count -eq 0) { throw "MIFODIY_QA_CORPUS_EMPTY" }
    @($files | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
} else { @(Get-Content -LiteralPath $CorpusPath -Raw | ConvertFrom-Json) }
$cases = if ($raw -is [System.Array]) { $raw } elseif ($raw.cases) { $raw.cases } else { @($raw) }
if ($cases.Count -lt 1) { throw "MIFODIY_QA_CORPUS_EMPTY" }

$uuid = [Guid]::Empty
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$invalid = [System.Collections.Generic.List[string]]::new()
$allowedIntents = @("FACT_LOOKUP","DECISION","RESPONSIBLE","DEADLINE","TASK","CAUSE","STATUS","TIMELINE","COMPARISON","SUMMARY","FOLLOW_UP")
$allowedAnswerTypes = @("DIRECT_FACT","MULTI_FACT","SUMMARY","COMPARISON","CONTRADICTION","PARTIAL","NO_EVIDENCE")
foreach ($case in $cases) {
    $id = [string]$case.caseId
    if ([string]::IsNullOrWhiteSpace($id) -or -not $seen.Add($id)) { $invalid.Add("caseId") ; continue }
    if ([string]::IsNullOrWhiteSpace([string]$case.question)) { $invalid.Add("${id}:question") }
    if ([string]$case.expectedMode -notin @("AUTO","GENERAL_CHAT","CURRENT_MEETING","LIVE_MEETING","MEETING_MEMORY")) { $invalid.Add("${id}:mode") }
    if ($case.expectedIntent -and [string]$case.expectedIntent -notin $allowedIntents) { $invalid.Add("${id}:intent") }
    if ($case.answerType -and [string]$case.answerType -notin $allowedAnswerTypes) { $invalid.Add("${id}:answerType") }
    foreach ($meetingId in @($case.allowedMeetingIds)) {
        if (-not [Guid]::TryParse([string]$meetingId, [ref]$uuid)) { $invalid.Add("${id}:meeting") }
    }
    if ($case.activeMeetingId -and -not [Guid]::TryParse([string]$case.activeMeetingId, [ref]$uuid)) { $invalid.Add("${id}:activeMeeting") }
    if ([string]$case.expectedMode -in @("CURRENT_MEETING","LIVE_MEETING","MEETING_MEMORY") -and @($case.allowedMeetingIds).Count -eq 0 -and -not $case.activeMeetingId) { $invalid.Add("${id}:explicitMeetingScope") }
}
if ($invalid.Count -gt 0) { throw "MIFODIY_QA_SCHEMA_INVALID:$($invalid -join ',')" }

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path ((Resolve-Path (Join-Path $PSScriptRoot "..")).Path) "artifacts\acceptance\mifodiy-qa.json"
}
$results = [System.Collections.Generic.List[object]]::new()
if (-not $ValidateOnly) {
    if ([string]::IsNullOrWhiteSpace($BaseUrl) -or [string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) { throw "MIFODIY_QA_AUTH_REQUIRED" }
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/auth/login" -WebSession $session -ContentType "application/json" -Body (@{ username = $Username; password = $Password } | ConvertTo-Json -Compress) | Out-Null
}
foreach ($case in $cases) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $questionHash = ([Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$case.question)))).ToLowerInvariant()
    $item = [ordered]@{ caseId = [string]$case.caseId; questionSha256 = $questionHash; expectedMode = [string]$case.expectedMode; expectedOutcome = [string]$case.expectedOutcome; status = if ($ValidateOnly) { "VALIDATED" } else { "NOT_RUN" }; resolvedMode = $null; finalStatus = $null; evidenceCount = $null; totalMs = $null }
    if (-not $ValidateOnly) {
        $started = [Diagnostics.Stopwatch]::StartNew()
        $commandId = [Guid]::NewGuid().ToString("N")
        $body = @{ question = [string]$case.question; requestedMode = [string]$case.expectedMode; source = "QA"; activeMeetingId = $case.activeMeetingId; commandId = $commandId; traceId = [Guid]::NewGuid().ToString("N") } | ConvertTo-Json -Depth 8 -Compress
        try {
            $accepted = Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/assistant/requests" -WebSession $session -ContentType "application/json" -Body $body
            $queryId = [string]$accepted.queryId
            $query = $null
            for ($attempt = 0; $attempt -lt 180 -and $query.status -notin @("READY","ANSWERED","ANSWERED_WITH_WARNING","FAILED","NEEDS_REVIEW","NO_EVIDENCE","GROUNDING_REJECTED","LLM_UNAVAILABLE"); $attempt++) {
                Start-Sleep -Milliseconds 500
                $query = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/api/assistant/queries/$queryId" -WebSession $session
            }
            $item.status = if ($query) { "COMPLETED" } else { "TIMEOUT" }
            $item.resolvedMode = if ($query) { [string]$query.assistantMode } else { $null }
            $item.finalStatus = if ($query) { [string]$query.status } else { $null }
            $item.evidenceCount = if ($query -and $query.evidence) { @($query.evidence).Count } else { 0 }
            $item.totalMs = [math]::Round($started.Elapsed.TotalMilliseconds, 1)
        } catch {
            $item.status = "ERROR"
            $item.finalStatus = $_.Exception.GetType().Name
        }
    }
    # The runner deliberately emits only IDs/hashes/metrics. Raw question,
    # answer, evidence text and transcript content never enter the report.
    $results.Add([pscustomobject]$item)
}
$report = [ordered]@{ schema = "mifodiy-qa-v1"; generatedAtUtc = [DateTimeOffset]::UtcNow; caseCount = $cases.Count; results = $results }
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$report | ConvertTo-Json -Depth 8

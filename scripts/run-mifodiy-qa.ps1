[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CorpusPath,
    [string]$BaseUrl,
    [string]$Username,
    [string]$Password,
    [string]$OutputPath,
    [string]$BuildIdentity,
    [string]$VoiceHostSha256,
    [string]$ModelRevision,
    [string]$ModelSha256,
    [string]$WhisperCppRevision,
    [string]$BridgeRevision,
    [int]$AbiVersion = 0,
    [int]$ThreadCount = 0,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$schemaPath = Join-Path $PSScriptRoot "..\docs\mifodiy-qa-case.schema.json"
if (-not (Test-Path -LiteralPath $schemaPath)) { throw "MIFODIY_QA_SCHEMA_MISSING" }
if (-not (Test-Path -LiteralPath $CorpusPath)) { throw "MIFODIY_QA_CORPUS_MISSING" }
$corpusItem = Get-Item -LiteralPath $CorpusPath
$raw = if ($corpusItem.PSIsContainer) {
    $files = @(Get-ChildItem -LiteralPath $CorpusPath -Filter *.json -File)
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
$answerStatuses = @("READY","ANSWERED","ANSWERED_WITH_WARNING")
foreach ($case in $cases) {
    $id = [string]$case.caseId
    if ([string]::IsNullOrWhiteSpace($id) -or -not $seen.Add($id)) { $invalid.Add("caseId"); continue }
    if ([string]::IsNullOrWhiteSpace([string]$case.question)) { $invalid.Add("${id}:question") }
    if ([string]$case.expectedMode -notin @("AUTO","GENERAL_CHAT","CURRENT_MEETING","LIVE_MEETING","MEETING_MEMORY")) { $invalid.Add("${id}:mode") }
    if ($case.expectedIntent -and [string]$case.expectedIntent -notin $allowedIntents) { $invalid.Add("${id}:intent") }
    if ($case.answerType -and [string]$case.answerType -notin $allowedAnswerTypes) { $invalid.Add("${id}:answerType") }
    foreach ($fact in @($case.expectedFacts)) {
        if ([string]::IsNullOrWhiteSpace([string]$fact.predicate) -or [string]::IsNullOrWhiteSpace([string]$fact.value)) { $invalid.Add("${id}:expectedFact") }
    }
    $target = if ($case.executionTarget) { [string]$case.executionTarget } else { "ASSISTANT_API" }
    if ($target -notin @("ASSISTANT_API", "VOICE_LOCAL")) { $invalid.Add("${id}:executionTarget") }
    if ([string]$case.expectedOutcome -eq "LOCAL_STATUS" -and $target -ne "VOICE_LOCAL") { $invalid.Add("${id}:localStatusTarget") }
    foreach ($meetingId in @($case.allowedMeetingIds)) { if (-not [Guid]::TryParse([string]$meetingId, [ref]$uuid)) { $invalid.Add("${id}:meeting") } }
    if ($case.activeMeetingId -and -not [Guid]::TryParse([string]$case.activeMeetingId, [ref]$uuid)) { $invalid.Add("${id}:activeMeeting") }
    if ([string]$case.expectedMode -in @("CURRENT_MEETING","LIVE_MEETING","MEETING_MEMORY") -and @($case.allowedMeetingIds).Count -eq 0 -and -not $case.activeMeetingId) { $invalid.Add("${id}:explicitMeetingScope") }
}
if ($invalid.Count -gt 0) { throw "MIFODIY_QA_SCHEMA_INVALID:$($invalid -join ',')" }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path ((Resolve-Path (Join-Path $PSScriptRoot "..")).Path) "artifacts\acceptance\mifodiy-qa.json" }

function Get-PathValue([object]$Object, [string]$Path) {
    $current = $Object
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $current -or -not ($current.PSObject.Properties.Name -contains $part)) { return $null }
        $current = $current.PSObject.Properties[$part].Value
    }
    return $current
}

function Get-EvidenceIds([object]$Evidence) {
    @($Evidence | ForEach-Object {
        foreach ($name in @('segmentId','evidenceSegmentId','id')) {
            if ($_.PSObject.Properties.Name -contains $name -and -not [string]::IsNullOrWhiteSpace([string]$_.PSObject.Properties[$name].Value)) { [string]$_.PSObject.Properties[$name].Value; break }
        }
    } | Where-Object { $_ })
}

function Test-Outcome([string]$Expected, [string]$Status, [string]$Target) {
    if ($Expected -eq "LOCAL_STATUS") { return $Target -eq "VOICE_LOCAL" }
    if ($Expected -eq "ANSWER") { return $Status -in $answerStatuses }
    if ($Expected -eq "NO_EVIDENCE") { return $Status -eq "NO_EVIDENCE" }
    if ($Expected -eq "GROUNDING_REJECTED") { return $Status -eq "GROUNDING_REJECTED" }
    return $false
}

if (-not $ValidateOnly) {
    if ([string]::IsNullOrWhiteSpace($BaseUrl) -or [string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) { throw "MIFODIY_QA_AUTH_REQUIRED" }
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/auth/login" -WebSession $session -ContentType "application/json" -Body (@{ username = $Username; password = $Password } | ConvertTo-Json -Compress) | Out-Null
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $questionHash = ([Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$case.question)))).ToLowerInvariant()
    $target = if ($case.executionTarget) { [string]$case.executionTarget } else { "ASSISTANT_API" }
    $item = [ordered]@{ caseId = [string]$case.caseId; questionSha256 = $questionHash; expectedMode = [string]$case.expectedMode; expectedOutcome = [string]$case.expectedOutcome; executionTarget = $target; status = if ($ValidateOnly) { "VALIDATED" } else { "NOT_RUN" }; resolvedMode = $null; actualIntent = $null; actualAnswerType = $null; finalStatus = $null; evidenceCount = 0; totalMs = $null; modePassed = $false; intentPassed = $false; outcomePassed = $false; answerTypePassed = $false; evidencePassed = $false; groundingPassed = $false; casePassed = $false }
    if ($ValidateOnly) { $results.Add([pscustomobject]$item); continue }
    if ($target -eq "VOICE_LOCAL") { $item.status = "FAILED"; $item.finalStatus = "VOICE_LOCAL_REQUIRES_BEHAVIORAL_RUNNER"; $results.Add([pscustomobject]$item); continue }
    $started = [Diagnostics.Stopwatch]::StartNew()
    $commandId = [Guid]::NewGuid().ToString("N")
    $body = @{ question = [string]$case.question; requestedMode = [string]$case.expectedMode; source = "QA"; activeMeetingId = $case.activeMeetingId; commandId = $commandId; traceId = [Guid]::NewGuid().ToString("N") } | ConvertTo-Json -Depth 8 -Compress
    $query = $null
    try {
        $accepted = Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/assistant/requests" -WebSession $session -ContentType "application/json" -Body $body
        $queryId = [string]$accepted.queryId
        for ($attempt = 0; $attempt -lt 180; $attempt++) {
            Start-Sleep -Milliseconds 500
            $query = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/api/assistant/queries/$queryId" -WebSession $session
            if ([string]$query.status -in @("READY","ANSWERED","ANSWERED_WITH_WARNING","FAILED","NEEDS_REVIEW","NO_EVIDENCE","GROUNDING_REJECTED","LLM_UNAVAILABLE")) { break }
        }
        $item.resolvedMode = if ($query) { [string]$query.assistantMode } else { $null }
        $metadata = if ($query) { $query.answerMetadata } else { $null }
        $item.actualIntent = [string](Get-PathValue $metadata "queryPlan.intent")
        $actualAnswerType = Get-PathValue $metadata "answerType"
        if ($null -eq $actualAnswerType) { $actualAnswerType = Get-PathValue $metadata "answerPlan.answerType" }
        $item.actualAnswerType = [string]$actualAnswerType
        $item.finalStatus = if ($query) { [string]$query.status } else { "TIMEOUT" }
        $evidence = @($query.evidence)
        $item.evidenceCount = $evidence.Count
        $item.totalMs = [math]::Round($started.Elapsed.TotalMilliseconds, 1)
        $item.modePassed = [string]$case.expectedMode -eq "AUTO" -or [string]$case.expectedMode -eq $item.resolvedMode
        $item.intentPassed = -not $case.expectedIntent -or [string]$case.expectedIntent -eq $item.actualIntent
        $item.answerTypePassed = -not $case.answerType -or [string]$case.answerType -eq $item.actualAnswerType
        $item.outcomePassed = Test-Outcome ([string]$case.expectedOutcome) ([string]$item.finalStatus) $target
        $evidenceIds = @(Get-EvidenceIds $evidence)
        $allowedIds = @($case.allowedMeetingIds | ForEach-Object { [string]$_ })
        $scopeOk = $true
        foreach ($entry in $evidence) { $meetingId = [string](Get-PathValue $entry "meetingId"); if ($allowedIds.Count -gt 0 -and $meetingId -and $meetingId -notin $allowedIds) { $scopeOk = $false } }
        $requiredIds = @($case.evidenceSegmentIds | ForEach-Object { [string]$_ })
        $item.evidencePassed = $scopeOk -and @($requiredIds | Where-Object { $_ -notin $evidenceIds }).Count -eq 0 -and (($case.expectedOutcome -eq "NO_EVIDENCE") -or $evidenceIds.Count -gt 0)
        $claimsValidated = Get-PathValue $metadata "claimsValidated"
        $item.groundingPassed = ([string]$item.finalStatus -ne "GROUNDING_REJECTED") -and ($null -eq $claimsValidated -or [bool]$claimsValidated)
        $answerText = [string]$query.answer
        if ([string]::IsNullOrWhiteSpace($answerText)) { $answerText = [string]$query.voiceAnswer }
        foreach ($forbiddenClaim in @($case.mustNotInfer)) { if ($forbiddenClaim -and $answerText.IndexOf([string]$forbiddenClaim, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $item.groundingPassed = $false } }
        foreach ($expectedClaim in @($case.expectedClaims)) {
            if ($expectedClaim -and $answerText.IndexOf([string]$expectedClaim, [StringComparison]::OrdinalIgnoreCase) -lt 0) { $item.groundingPassed = $false }
        }
        foreach ($expectedFact in @($case.expectedFacts)) {
            $predicate = [string]$expectedFact.predicate
            $value = [string]$expectedFact.value
            if ($value -and $answerText.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -lt 0) { $item.groundingPassed = $false }
            $predicateIntentMap = @{
                RESPONSIBLE = @("RESPONSIBLE", "FACT_LOOKUP")
                DEADLINE = @("DEADLINE", "FACT_LOOKUP")
                DECISION = @("DECISION", "FACT_LOOKUP")
                TASK = @("TASK", "FACT_LOOKUP")
                CAUSE = @("CAUSE", "FACT_LOOKUP")
                STATUS = @("STATUS", "FACT_LOOKUP")
                PERSON = @("RESPONSIBLE", "FACT_LOOKUP")
            }
            $predicateKey = $predicate.Trim().ToUpperInvariant()
            if ($predicateKey -and $item.actualIntent -and $predicateIntentMap.ContainsKey($predicateKey) -and $item.actualIntent -notin $predicateIntentMap[$predicateKey]) { $item.groundingPassed = $false }
        }
        $item.casePassed = [bool]($item.modePassed -and $item.intentPassed -and $item.outcomePassed -and $item.answerTypePassed -and $item.evidencePassed -and $item.groundingPassed)
        $item.status = if ($item.casePassed) { "PASSED" } else { "FAILED" }
    } catch { $item.status = "FAILED"; $item.finalStatus = $_.Exception.GetType().Name; $item.totalMs = [math]::Round($started.Elapsed.TotalMilliseconds, 1) }
    $results.Add([pscustomobject]$item)
}

$executed = @($results | Where-Object { $_.status -notin @("VALIDATED","NOT_RUN") })
$failed = @($results | Where-Object { $_.status -eq "FAILED" }).Count
$targetValues = @($cases | ForEach-Object { if ($_.executionTarget) { [string]$_.executionTarget } else { "ASSISTANT_API" } } | Select-Object -Unique)
$report = [ordered]@{
    schema = "mifodiy-qa-v2"
    generatedAtUtc = [DateTimeOffset]::UtcNow
    buildIdentity = if ([string]::IsNullOrWhiteSpace($BuildIdentity)) { $null } else { $BuildIdentity.Trim() }
    voiceHostSha256 = if ([string]::IsNullOrWhiteSpace($VoiceHostSha256)) { $null } else { $VoiceHostSha256.Trim().ToLowerInvariant() }
    refiner = if (-not [string]::IsNullOrWhiteSpace($ModelRevision) -or -not [string]::IsNullOrWhiteSpace($ModelSha256) -or $AbiVersion -gt 0 -or $ThreadCount -gt 0) {
        [ordered]@{
            modelRevision = if ([string]::IsNullOrWhiteSpace($ModelRevision)) { $null } else { $ModelRevision.Trim().ToLowerInvariant() }
            modelSha256 = if ([string]::IsNullOrWhiteSpace($ModelSha256)) { $null } else { $ModelSha256.Trim().ToLowerInvariant() }
            whisperCppRevision = if ([string]::IsNullOrWhiteSpace($WhisperCppRevision)) { $null } else { $WhisperCppRevision.Trim().ToLowerInvariant() }
            bridgeRevision = if ([string]::IsNullOrWhiteSpace($BridgeRevision)) { $null } else { $BridgeRevision.Trim().ToLowerInvariant() }
            abiVersion = if ($AbiVersion -gt 0) { $AbiVersion } else { $null }
        }
    } else { $null }
    threadCount = if ($ThreadCount -gt 0) { $ThreadCount } else { $null }
    executionTarget = if ($targetValues.Count -eq 1) { $targetValues[0] } else { "MIXED" }
    mode = if ($ValidateOnly) { "VALIDATION_ONLY" } elseif ($targetValues.Count -eq 1 -and $targetValues[0] -eq "ASSISTANT_API") { "ASSISTANT_API" } else { "MIXED" }
    caseCount = $cases.Count
    executedCaseCount = $executed.Count
    passedCaseCount = @($results | Where-Object { $_.casePassed -eq $true }).Count
    failedCaseCount = $failed
    caseResultsComplete = (-not $ValidateOnly) -and $executed.Count -eq $cases.Count -and @($results | Where-Object { -not $_.modePassed -or -not $_.intentPassed -or -not $_.outcomePassed -or -not $_.answerTypePassed -or -not $_.evidencePassed -or -not $_.groundingPassed -or -not $_.casePassed }).Count -eq 0
    status = if ($ValidateOnly) { "VALIDATED" } elseif ($executed.Count -gt 0 -and $executed.Count -eq $cases.Count -and $failed -eq 0) { "PASSED" } else { "FAILED" }
    results = $results
}
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$report | ConvertTo-Json -Depth 10
if (-not $ValidateOnly -and ($executed.Count -eq 0 -or $failed -gt 0)) { exit 1 }

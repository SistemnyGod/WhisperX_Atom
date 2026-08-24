[CmdletBinding()]
param(
    [string]$CorpusRoot = (Join-Path $env:ProgramData 'WhisperXAtom\QA\Mifodiy\voice-shadow'),
    [string]$OutputPath = (Join-Path $env:ProgramData 'WhisperXAtom\QA\Mifodiy\voice-shadow-evidence.json')
)

$ErrorActionPreference = 'Stop'
$schema = 'voice-shadow-corpus-v2'
$minimumCases = 700
$forbidden = @('audio', 'audioPath', 'pcm', 'pcm16', 'transcript', 'text', 'answer', 'utterance', 'shadowText', 'whisperText')
$distanceThresholds = [ordered]@{ '0.5' = 0.95; '1' = 0.95; '2' = 0.90; '3' = 0.85 }

if (-not (Test-Path -LiteralPath $CorpusRoot)) { throw "VOICE_SHADOW_CORPUS_MISSING:$CorpusRoot" }
$files = @(Get-ChildItem -LiteralPath $CorpusRoot -Filter '*.json' -File -Recurse)
$cases = @()
foreach ($file in $files) {
    $payload = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($property in $forbidden) {
        if ($null -ne $payload.PSObject.Properties[$property]) { throw "VOICE_SHADOW_EVIDENCE_CONTAINS_PRIVATE_DATA:$($file.Name):$property" }
    }
    foreach ($case in @($payload.cases)) {
        foreach ($property in $forbidden) {
            if ($null -ne $case.PSObject.Properties[$property]) { throw "VOICE_SHADOW_EVIDENCE_CONTAINS_PRIVATE_DATA:$($file.Name):$property" }
        }
        foreach ($required in @('caseId', 'scenario', 'distance', 'condition', 'expectedIntent', 'actualIntent')) {
            if ([string]::IsNullOrWhiteSpace([string]$case.$required)) { throw "VOICE_SHADOW_CASE_INVALID:$($file.Name):$required" }
        }
        $cases += $case
    }
}
if ($cases.Count -eq 0) { throw 'VOICE_SHADOW_CORPUS_EMPTY' }

$commandCases = @($cases | Where-Object {
    [string]$_.expectedIntent -notin @('AssistantQuery', 'Unknown', 'NoWake')
})
$commandHits = @($commandCases | Where-Object {
    [string]$_.expectedIntent -eq [string]$_.actualIntent
})
$falseMutations = @($cases | Where-Object {
    ([bool]$_.recorderMutation -or [bool]$_.actualRecorderMutation) -and
    [string]$_.expectedIntent -in @('AssistantQuery', 'Unknown', 'NoWake')
}).Count
$latencies = @($commandCases | Where-Object { $null -ne $_.localCommandLatencyMs } | ForEach-Object { [double]$_.localCommandLatencyMs } | Sort-Object)
$p95 = $null
if ($latencies.Count -gt 0) { $p95 = $latencies[[Math]::Min($latencies.Count - 1, [Math]::Ceiling($latencies.Count * 0.95) - 1)] }

$byDistance = [ordered]@{}
$distanceFailures = @()
foreach ($distance in $distanceThresholds.Keys) {
    $groupCases = @($cases | Where-Object { [string]$_.distance -eq $distance })
    $groupCommands = @($groupCases | Where-Object { [string]$_.expectedIntent -notin @('AssistantQuery', 'Unknown', 'NoWake') })
    $hits = @($groupCommands | Where-Object { [string]$_.expectedIntent -eq [string]$_.actualIntent })
    $recall = if ($groupCommands.Count) { [math]::Round($hits.Count / $groupCommands.Count, 4) } else { $null }
    $byDistance[$distance] = [ordered]@{
        cases = $groupCases.Count
        commandCases = $groupCommands.Count
        commandRecall = $recall
        requiredCommandRecall = $distanceThresholds[$distance]
    }
    if ($groupCommands.Count -eq 0 -or $null -eq $recall -or $recall -lt $distanceThresholds[$distance]) { $distanceFailures += $distance }
}

$wakeCases = @($cases | Where-Object { $null -ne $_.expectedWake -and $null -ne $_.actualWake })
$wakeByDistance = [ordered]@{}
$wakeFailures = @()
$wakeThresholds = [ordered]@{ '0.5' = 0.98; '1' = 0.98; '2' = 0.95; '3' = 0.85 }
foreach ($distance in $wakeThresholds.Keys) {
    $group = @($wakeCases | Where-Object { [string]$_.distance -eq $distance })
    $hits = @($group | Where-Object { [bool]$_.expectedWake -eq [bool]$_.actualWake -and (-not [bool]$_.expectedWake -or [string]$_.actualWakeResult -ne 'REJECTED') })
    $recall = if ($group.Count) { [math]::Round($hits.Count / $group.Count, 4) } else { $null }
    $wakeByDistance[$distance] = [ordered]@{ cases = $group.Count; recall = $recall; requiredRecall = $wakeThresholds[$distance] }
    if ($group.Count -eq 0 -or $null -eq $recall -or $recall -lt $wakeThresholds[$distance]) { $wakeFailures += $distance }
}

$assistantCases = @($cases | Where-Object {
    [string]$_.expectedIntent -eq 'AssistantQuery' -and $null -ne $_.voskNormalizedCorrect -and $null -ne $_.refinedNormalizedCorrect
})
$voskAccuracy = if ($assistantCases.Count) { [double](@($assistantCases | Where-Object { [bool]$_.voskNormalizedCorrect }).Count / $assistantCases.Count) } else { $null }
$refinedAccuracy = if ($assistantCases.Count) { [double](@($assistantCases | Where-Object { [bool]$_.refinedNormalizedCorrect }).Count / $assistantCases.Count) } else { $null }
$accuracyGain = if ($null -ne $voskAccuracy -and $null -ne $refinedAccuracy) { [math]::Round($refinedAccuracy - $voskAccuracy, 4) } else { $null }
$regressionCases = @($cases | Where-Object { [bool]$_.numberRegression -or [bool]$_.dateRegression -or [bool]$_.negationRegression -or [bool]$_.intentRegression }).Count

$blocked = $cases.Count -lt $minimumCases -or $commandCases.Count -eq 0 -or $distanceFailures.Count -gt 0 -or $wakeCases.Count -eq 0 -or $wakeFailures.Count -gt 0 -or $assistantCases.Count -eq 0 -or $null -eq $accuracyGain
$failed = $falseMutations -ne 0 -or ($null -ne $p95 -and $p95 -gt 2000) -or $regressionCases -ne 0 -or ($null -ne $accuracyGain -and $accuracyGain -lt 0.05)
$status = if ($failed) { 'FAILED' } elseif ($blocked) { 'BLOCKED' } else { 'READY_FOR_REVIEW' }

$evidence = [ordered]@{
    schema = $schema
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    caseCount = $cases.Count
    minimumCaseCount = $minimumCases
    commandCaseCount = $commandCases.Count
    commandRecall = if ($commandCases.Count) { [math]::Round($commandHits.Count / $commandCases.Count, 4) } else { $null }
    falseRecorderMutations = $falseMutations
    localCommandP95Ms = $p95
    byDistance = $byDistance
    wakeByDistance = $wakeByDistance
    wakeCaseCount = $wakeCases.Count
    assistantCaseCount = $assistantCases.Count
    voskAssistantAccuracy = $voskAccuracy
    refinedAssistantAccuracy = $refinedAccuracy
    refinedAccuracyGain = $accuracyGain
    criticalRegressionCases = $regressionCases
    status = $status
}
$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$evidence | ConvertTo-Json -Depth 10
if ($status -ne 'READY_FOR_REVIEW') { exit 2 }

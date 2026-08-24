[CmdletBinding()]
param(
    [string]$CorpusRoot = (Join-Path $env:ProgramData 'WhisperXAtom\QA\Mifodiy\voice-shadow'),
    [string]$OutputPath = (Join-Path $env:ProgramData 'WhisperXAtom\QA\Mifodiy\voice-shadow-evidence.json'),
    [string]$BuildIdentity,
    [string]$ManifestPath,
    [string]$VoiceHostPath,
    [int]$ThreadCount = 1,
    [ValidateSet('LIVE','DIAGNOSTIC')][string]$CaptureMode = 'DIAGNOSTIC'
)

$ErrorActionPreference = 'Stop'
$schema = 'voice-shadow-corpus-v2'
$minimumCases = 700
$forbidden = @('audio', 'audioPath', 'pcm', 'pcm16', 'transcript', 'text', 'answer', 'utterance', 'shadowText', 'whisperText')
$distanceThresholds = [ordered]@{ '0.5' = 0.95; '1' = 0.95; '2' = 0.90; '3' = 0.85 }
$wakeThresholds = [ordered]@{ '0.5' = 0.98; '1' = 0.98; '2' = 0.95; '3' = 0.85 }
$expectedModelRevision = 'c521a4b02f422512d734391fdf08bb08c0862f68'
$expectedModelSha256 = '1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b'
$expectedWhisperCppRevision = 'f049fff95a089aa9969deb009cdd4892b3e74916'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($BuildIdentity)) {
    $identityPath = Join-Path $repo 'artifacts\desktop\build-identity.json'
    if (Test-Path -LiteralPath $identityPath) { $BuildIdentity = [string](Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json).buildIdentity }
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $repo 'apps\voice-host\Models\Voice\whisper-shadow\voice-refiner.manifest.json' }
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw 'VOICE_SHADOW_REFINER_MANIFEST_MISSING' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or [string]::IsNullOrWhiteSpace($BuildIdentity) -or [string]$manifest.buildIdentity -ne $BuildIdentity) { throw 'VOICE_SHADOW_BUILD_IDENTITY_MISMATCH' }
if ([string]$manifest.model.revision -ne $expectedModelRevision -or
    [string]$manifest.model.sha256 -ne $expectedModelSha256 -or
    [string]$manifest.native.whisperCppRevision -ne $expectedWhisperCppRevision -or
    [int]$manifest.native.abiVersion -ne 1) { throw 'VOICE_SHADOW_PINNED_ASSET_MISMATCH' }
if ($ThreadCount -lt 1 -or $ThreadCount -gt 4) { throw 'VOICE_REFINER_THREADS_INVALID' }
$voiceHostSha256 = $null
if (-not [string]::IsNullOrWhiteSpace($VoiceHostPath) -and (Test-Path -LiteralPath $VoiceHostPath -PathType Leaf)) { $voiceHostSha256 = (Get-FileHash -LiteralPath $VoiceHostPath -Algorithm SHA256).Hash.ToLowerInvariant() }
$assetAttestationValid = -not [string]::IsNullOrWhiteSpace($voiceHostSha256) -and
    -not [string]::IsNullOrWhiteSpace([string]$manifest.model.sha256) -and
    -not [string]::IsNullOrWhiteSpace([string]$manifest.native.sha256) -and
    [int]$manifest.native.abiVersion -eq 1

if (-not (Test-Path -LiteralPath $CorpusRoot)) { throw "VOICE_SHADOW_CORPUS_MISSING:$CorpusRoot" }
$files = @(Get-ChildItem -LiteralPath $CorpusRoot -Filter '*.json' -File -Recurse)
$cases = @()
$manualUnconfirmed = 0
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
        if ($case.operatorConfirmed -ne $true -and $case.reviewed -ne $true) { $manualUnconfirmed++ }
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
$falseStops = @($cases | Where-Object { [string]$_.actualIntent -eq 'STOP' -and [string]$_.expectedIntent -ne 'STOP' }).Count
$refinerTimeouts = @($cases | Where-Object { [bool]$_.refinerTimeout -or [string]$_.agreement -eq 'TIMEOUT' }).Count
$queueDrops = @($cases | Where-Object { [bool]$_.queueDropped -or [string]$_.agreement -eq 'DROPPED' }).Count
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

$blocked = $CaptureMode -ne 'LIVE' -or -not $assetAttestationValid -or $cases.Count -lt $minimumCases -or $commandCases.Count -eq 0 -or $distanceFailures.Count -gt 0 -or $wakeCases.Count -eq 0 -or $wakeFailures.Count -gt 0 -or $assistantCases.Count -eq 0 -or $null -eq $accuracyGain -or $manualUnconfirmed -gt 0
$failed = $falseMutations -ne 0 -or $falseStops -ne 0 -or $queueDrops -ne 0 -or ($null -ne $p95 -and $p95 -gt 2000) -or $regressionCases -ne 0 -or ($null -ne $accuracyGain -and $accuracyGain -lt 0.05) -or ($cases.Count -gt 0 -and ($refinerTimeouts / $cases.Count) -ge 0.01)
$status = if ($failed) { 'FAILED' } elseif ($blocked) { 'BLOCKED' } else { 'PASSED' }

$evidence = [ordered]@{
    schema = $schema
    captureMode = $CaptureMode
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    buildIdentity = $BuildIdentity
    voiceHostSha256 = $voiceHostSha256
    assetAttestationValid = $assetAttestationValid
    refiner = [ordered]@{ modelRevision = [string]$manifest.model.revision; modelSha256 = [string]$manifest.model.sha256; whisperCppRevision = [string]$manifest.native.whisperCppRevision; bridgeRevision = [string]$manifest.native.bridgeRevision; nativeSha256 = [string]$manifest.native.sha256; abiVersion = [int]$manifest.native.abiVersion; threadCount = $ThreadCount }
    caseCount = $cases.Count
    minimumCaseCount = $minimumCases
    commandCaseCount = $commandCases.Count
    commandRecall = if ($commandCases.Count) { [math]::Round($commandHits.Count / $commandCases.Count, 4) } else { $null }
    falseRecorderMutations = $falseMutations
    falseStopMutations = $falseStops
    refinerTimeoutCount = $refinerTimeouts
    queueDropCount = $queueDrops
    operatorUnconfirmedCases = $manualUnconfirmed
    localCommandP95Ms = $p95
    byDistance = $byDistance
    wakeByDistance = $wakeByDistance
    wakeCaseCount = $wakeCases.Count
    assistantCaseCount = $assistantCases.Count
    voskAssistantAccuracy = $voskAccuracy
    refinedAssistantAccuracy = $refinedAccuracy
    refinedAccuracyGain = $accuracyGain
    criticalRegressionCases = $regressionCases
    safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; textIncluded = $false; questionTextIncluded = $false; shadowTextIncluded = $false }
    status = $status
}
$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$evidence | ConvertTo-Json -Depth 10
if ($status -ne 'PASSED') { exit 2 }

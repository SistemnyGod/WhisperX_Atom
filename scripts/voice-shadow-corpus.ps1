[CmdletBinding()]
param(
    [string]$CorpusRoot = (Join-Path $env:ProgramData 'WhisperXAtom\QA\Mifodiy\voice-shadow'),
    [string]$OutputPath = (Join-Path $env:ProgramData 'WhisperXAtom\QA\Mifodiy\voice-shadow-evidence.json')
)

$ErrorActionPreference = 'Stop'
$schema = 'voice-shadow-corpus-v1'
$forbidden = @('audio', 'audioPath', 'pcm', 'transcript', 'text', 'answer', 'utterance')
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
        if ([string]::IsNullOrWhiteSpace([string]$case.caseId) -or
            [string]::IsNullOrWhiteSpace([string]$case.distance) -or
            [string]::IsNullOrWhiteSpace([string]$case.expectedIntent) -or
            [string]::IsNullOrWhiteSpace([string]$case.actualIntent)) { throw "VOICE_SHADOW_CASE_INVALID:$($file.Name)" }
        $cases += $case
    }
}
if ($cases.Count -eq 0) { throw 'VOICE_SHADOW_CORPUS_EMPTY' }

$falseMutations = @($cases | Where-Object { [bool]$_.recorderMutation -and [string]$_.expectedIntent -eq 'AssistantQuery' }).Count
$commandCases = @($cases | Where-Object { [string]$_.expectedIntent -ne 'AssistantQuery' }).Count
$commandHits = @($cases | Where-Object { [string]$_.expectedIntent -eq [string]$_.actualIntent }).Count
$latencies = @($cases | Where-Object { $null -ne $_.localCommandLatencyMs } | ForEach-Object { [double]$_.localCommandLatencyMs } | Sort-Object)
$p95 = $null
if ($latencies.Count -gt 0) { $p95 = $latencies[[Math]::Min($latencies.Count - 1, [Math]::Ceiling($latencies.Count * 0.95) - 1)] }
$byDistance = [ordered]@{}
foreach ($group in @($cases | Group-Object distance)) {
    $groupCases = @($group.Group)
    $expected = @($groupCases | Where-Object { [string]$_.expectedIntent -ne 'AssistantQuery' }).Count
    $hits = @($groupCases | Where-Object { [string]$_.expectedIntent -ne 'AssistantQuery' -and [string]$_.expectedIntent -eq [string]$_.actualIntent }).Count
    $byDistance[$group.Name] = [ordered]@{ cases = $groupCases.Count; commandRecall = if ($expected) { [math]::Round($hits / $expected, 4) } else { $null } }
}

$status = if ($falseMutations -ne 0) { 'FAILED' } elseif ($commandCases -eq 0) { 'BLOCKED' } elseif (($commandHits / $commandCases) -lt 0.90) { 'FAILED' } elseif ($null -ne $p95 -and $p95 -gt 2000) { 'FAILED' } else { 'READY_FOR_REVIEW' }
$evidence = [ordered]@{
    schema = $schema
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    caseCount = $cases.Count
    commandCaseCount = $commandCases
    commandRecall = if ($commandCases) { [math]::Round($commandHits / $commandCases, 4) } else { $null }
    falseRecorderMutations = $falseMutations
    localCommandP95Ms = $p95
    byDistance = $byDistance
    status = $status
}
$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$evidence | ConvertTo-Json -Depth 10
if ($status -eq 'FAILED') { exit 2 }

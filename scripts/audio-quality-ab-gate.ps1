[CmdletBinding()]
param(
    [ValidateRange(10,20)][int]$Pairs = 10,
    [ValidateRange(13,40)][int]$DurationSeconds = 13,
    [ValidateRange(0,10)][double]$SilenceSeconds = 3,
    [ValidateRange(1,30)][double]$SpeechSeconds = 10,
    [string]$DeviceId = '',
    [string]$RatingsPath = '',
    [string]$OutputRoot = '',
    [switch]$KeepAudio
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $repo 'artifacts\acceptance\audio-quality-ab' }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$checks = [System.Collections.Generic.List[object]]::new()
$pairs = [System.Collections.Generic.List[object]]::new()
$safety = [ordered]@{ audioIncluded = [bool]$KeepAudio; transcriptIncluded = $false; credentialsIncluded = $false; tokensIncluded = $false }
function Add-Check([string]$Name, [string]$Status, [string]$Detail = '') { $checks.Add([ordered]@{ name=$Name; status=$Status; detail=$Detail }) }
function Value($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($property) { return $property.Value }
    return $null
}
function Number($Object, [string]$Name) {
    $value = Value $Object $Name
    if ($null -eq $value) { return $null }
    try { return [double]$value } catch { return $null }
}
$child = Join-Path $root 'pairs'
for ($index = 1; $index -le $Pairs; $index++) {
    $pairRoot = Join-Path $child ("pair-{0:D2}" -f $index)
    $started = [DateTime]::UtcNow
    try {
        $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts\run-audio-capture-ab.ps1'),'-DurationSeconds',$DurationSeconds,'-SilenceSeconds',$SilenceSeconds,'-SpeechSeconds',$SpeechSeconds,'-OutputRoot',$pairRoot)
        if ($DeviceId) { $args += @('-DeviceId',$DeviceId) }
        if ($KeepAudio) { $args += '-KeepAudio' }
        & powershell.exe @args | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "PAIR_EXIT_$LASTEXITCODE" }
        $reportFile = Get-ChildItem -LiteralPath $pairRoot -File -Filter 'report-*.json' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -ge $started } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($null -eq $reportFile) { throw 'PAIR_EVIDENCE_MISSING' }
        $report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
        $ab = Value $report 'response' | ForEach-Object { Value $_ 'audioCaptureAb' }
        if ($null -eq $ab -or -not [bool](Value $ab 'success')) { throw "PAIR_NOT_SUCCESSFUL: $(Value $ab 'errorCode')" }
        if (-not [bool](Value $ab 'noiseWindowConfirmed')) { throw 'PAIR_NO_CONFIRMED_NOISE_WINDOW' }
        $graph = Value $ab 'audioGraphQuality'; $raw = Value $ab 'rawQuality'
        $graphGrade = [string](Value $graph 'grade'); $rawGrade = [string](Value $raw 'grade')
        $graphSnr = Number $graph 'estimatedSnrDb'; $rawSnr = Number $raw 'estimatedSnrDb'
        $graphClip = Number $graph 'normalizedClippingRatio'; $rawClip = Number $raw 'normalizedClippingRatio'
        $graphDrop = Number $graph 'dropoutCount'; $rawDrop = Number $raw 'dropoutCount'
        $graphHash = [string](Value $ab 'audioGraphSha256'); $rawHash = [string](Value $ab 'rawSha256')
        if (-not $graphHash -or -not $rawHash -or $graphHash -eq $rawHash) { throw 'PAIR_AUDIO_HASH_INVALID' }
        $pairs.Add([ordered]@{ pair=$index; audioGraphSha256=$graphHash; rawSha256=$rawHash; audioGraphGrade=$graphGrade; rawGrade=$rawGrade; audioGraphSnrDb=$graphSnr; rawSnrDb=$rawSnr; audioGraphClipping=$graphClip; rawClipping=$rawClip; audioGraphDropouts=$graphDrop; rawDropouts=$rawDrop; silenceSeconds=$SilenceSeconds; speechSeconds=$SpeechSeconds; noiseWindowConfirmed=$true })
    } catch { Add-Check ("pair-{0:D2}" -f $index) 'FAILED' $_.Exception.Message }
}
$valid = @($pairs)
$gradeRank = @{ GOOD=3; WARNING=2; BAD=1; CRITICAL=0 }
$gradeNotWorse = @($valid | Where-Object { $gradeRank[[string]$_.rawGrade] -ge $gradeRank[[string]$_.audioGraphGrade] }).Count
$snrDeltas = @($valid | Where-Object { $null -ne $_.audioGraphSnrDb -and $null -ne $_.rawSnrDb } | ForEach-Object { [double]$_.rawSnrDb - [double]$_.audioGraphSnrDb })
$clipRatios = @($valid | Where-Object { $null -ne $_.audioGraphClipping -and [double]$_.audioGraphClipping -gt 0 } | ForEach-Object { 1 - ([double]$_.rawClipping / [double]$_.audioGraphClipping) })
$median = { param($items) if (-not $items.Count) { return $null }; $sorted=@($items | Sort-Object); $sorted[[int][math]::Floor(($sorted.Count-1)/2)] }
$medianSnrDelta = & $median $snrDeltas; $medianClipReduction = & $median $clipRatios
$ratingCount = 0; $rawWins = 0
if ($RatingsPath -and (Test-Path -LiteralPath $RatingsPath -PathType Leaf)) {
    $ratings = @(Get-Content -LiteralPath $RatingsPath -Raw | ConvertFrom-Json)
    $ratingCount = @($ratings | Where-Object { (Value $_ 'choice') -in @('RAW','AUDIOGRAPH') }).Count
    $rawWins = @($ratings | Where-Object { [string](Value $_ 'choice') -eq 'RAW' }).Count
}
$metrics = [ordered]@{ pairsRequested=$Pairs; pairsCompleted=$valid.Count; gradeNotWorseCount=$gradeNotWorse; medianSnrDeltaDb=$medianSnrDelta; medianClippingReduction=$medianClipReduction; operatorRatings=$ratingCount; rawOperatorWins=$rawWins; outcome=$null; pairs=@($valid) }
if ($valid.Count -ne $Pairs) { Add-Check 'pairCompletion' 'FAILED' "Expected $Pairs successful pairs, got $($valid.Count)." }
else { Add-Check 'pairCompletion' 'READY' "$Pairs AudioGraph/RAW pairs completed." }
if ($valid.Count -eq $Pairs -and $gradeNotWorse -ge [math]::Ceiling($Pairs * 0.8) -and (($null -ne $medianSnrDelta -and $medianSnrDelta -ge 3) -or ($null -ne $medianClipReduction -and $medianClipReduction -ge 0.5))) { Add-Check 'rawQualityMetrics' 'READY' 'RAW meets the objective quality threshold.' } else { Add-Check 'rawQualityMetrics' 'BLOCKED' 'RAW quality threshold is not met or metrics are incomplete.' }
if ($ratingCount -eq $Pairs -and $rawWins -ge [math]::Ceiling($Pairs * 0.7)) { Add-Check 'blindOperatorRating' 'READY' 'RAW selected in at least 70% of blind comparisons.' } else { Add-Check 'blindOperatorRating' 'BLOCKED' 'Attach one blind operator choice per pair; emulation cannot pass.' }
$rawRecommended = @($checks | Where-Object { $_.name -eq 'rawQualityMetrics' -and $_.status -eq 'READY' }).Count -and @($checks | Where-Object { $_.name -eq 'blindOperatorRating' -and $_.status -eq 'READY' }).Count
$metrics.outcome = if ($rawRecommended) { 'RAW_PROMOTION_RECOMMENDED' } else { 'AUDIOGRAPH_RETAIN' }
$status = if (@($checks | Where-Object status -eq 'FAILED').Count) { 'FAILED' } elseif (@($checks | Where-Object status -eq 'BLOCKED').Count) { 'BLOCKED' } else { 'PASSED' }
$evidence = [ordered]@{ schemaVersion=1; scenario='audio-quality-ab'; runId=$runId; startedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); status=$status; result=$metrics; checks=@($checks); safety=$safety }
$path = Join-Path $root "$runId.json"
$evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
Write-Output $path
if ($status -ne 'PASSED') { exit 2 }

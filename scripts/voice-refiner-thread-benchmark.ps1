[CmdletBinding()]
param(
    [ValidateSet('Contract','Installed')][string]$Mode = 'Contract',
    [string]$EvidenceRoot = '',
    [string]$OutputPath = '',
    [string]$BuildIdentity = '',
    [int[]]$ThreadCounts = @(1, 2, 4)
)

$ErrorActionPreference = 'Stop'
$schema = 'voice-refiner-thread-benchmark-v1'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repo 'artifacts\acceptance\voice-refiner-thread-benchmark.json' }
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = Join-Path $repo 'artifacts\acceptance\voice-refiner-thread-benchmark' }
if ($ThreadCounts.Count -eq 0 -or @($ThreadCounts | Where-Object { $_ -notin @(1, 2, 4) }).Count -gt 0 -or @($ThreadCounts | Select-Object -Unique).Count -ne $ThreadCounts.Count) { throw 'VOICE_REFINER_THREADS_INVALID' }

function Write-Artifact([object]$Artifact) {
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $Artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Output (ConvertTo-Json ([ordered]@{ output = $OutputPath; status = $Artifact.status }) -Compress)
}

if ($Mode -eq 'Contract') {
    Write-Artifact ([ordered]@{
        schema = $schema
        status = 'CONTRACT_ONLY'
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        threadCounts = @($ThreadCounts)
        selectionRule = [ordered]@{ maxRefinementP95Ms = 4500; maxLocalCommandP95Ms = 2000; maxRecorderLatencyDegradationPct = 5; maxQueueDrops = 0; maxOverruns = 0; chooseMinimumPassingThreadCount = $true; fallbackMode = 'SHADOW' }
        safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; textIncluded = $false }
    })
    exit 0
}

if ([string]::IsNullOrWhiteSpace($BuildIdentity)) {
    $identityPath = Join-Path $repo 'artifacts\desktop\build-identity.json'
    if (Test-Path -LiteralPath $identityPath -PathType Leaf) { $BuildIdentity = [string](Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json).buildIdentity }
}
if ([string]::IsNullOrWhiteSpace($BuildIdentity)) { throw 'VOICE_REFINER_BUILD_IDENTITY_REQUIRED' }
if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) { throw 'VOICE_REFINER_THREAD_EVIDENCE_MISSING' }

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($threads in $ThreadCounts) {
    $file = Join-Path $EvidenceRoot ("thread-$threads\voice-shadow-evidence.json")
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $rows.Add([ordered]@{ threadCount = $threads; evidencePresent = $false; passed = $false; error = 'EVIDENCE_MISSING' }); continue }
    try {
        $evidence = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        $latencies = @($evidence.metrics.localCommandLatenciesMs | ForEach-Object { [double]$_ } | Sort-Object)
        if ($latencies.Count -eq 0 -and $null -ne $evidence.metrics.localCommandP95Ms) { $latencies = @([double]$evidence.metrics.localCommandP95Ms) }
        $p95 = if ($latencies.Count) { $latencies[[Math]::Min($latencies.Count - 1, [Math]::Ceiling($latencies.Count * 0.95) - 1)] } else { $null }
        $refinerP95 = if ($null -ne $evidence.metrics.refinementP95Ms) { [double]$evidence.metrics.refinementP95Ms } else { $null }
        $degradation = if ($null -ne $evidence.metrics.recorderLatencyDegradationPct) { [double]$evidence.metrics.recorderLatencyDegradationPct } else { $null }
        $drops = if ($null -ne $evidence.metrics.queueDrops) { [int]$evidence.metrics.queueDrops } else { $null }
        $overruns = if ($null -ne $evidence.metrics.recorderOverruns) { [int]$evidence.metrics.recorderOverruns } else { $null }
        $identityOk = [string]$evidence.buildIdentity -eq $BuildIdentity
        $passed = $identityOk -and $null -ne $p95 -and $null -ne $refinerP95 -and $null -ne $degradation -and $null -ne $drops -and $null -ne $overruns -and $refinerP95 -le 4500 -and $p95 -le 2000 -and $degradation -le 5 -and $drops -eq 0 -and $overruns -eq 0
        $rows.Add([ordered]@{ threadCount = $threads; evidencePresent = $true; buildIdentityMatch = $identityOk; refinementP95Ms = $refinerP95; localCommandP95Ms = $p95; recorderLatencyDegradationPct = $degradation; queueDrops = $drops; recorderOverruns = $overruns; passed = $passed })
    } catch { $rows.Add([ordered]@{ threadCount = $threads; evidencePresent = $true; passed = $false; error = 'EVIDENCE_INVALID' }) }
}
$passing = @($rows | Where-Object { $_.passed -eq $true } | Sort-Object threadCount)
$selected = if ($passing.Count) { [int]$passing[0].threadCount } else { $null }
$status = if ($selected) { 'PASSED' } else { 'BLOCKED' }
Write-Artifact ([ordered]@{ schema = $schema; status = $status; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); buildIdentity = $BuildIdentity; testedThreadCounts = @($ThreadCounts); selectedThreadCount = $selected; promotionAllowed = $false; fallbackMode = 'SHADOW'; results = @($rows); safety = [ordered]@{ audioIncluded = $false; transcriptIncluded = $false; textIncluded = $false } })
if ($status -ne 'PASSED') { exit 2 }

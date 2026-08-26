[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BuildIdentity,
    [string]$AcceptanceRoot = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($AcceptanceRoot)) { $AcceptanceRoot = Join-Path $repo "artifacts\acceptance" }
$manifestPath = Join-Path $repo "apps\voice-host\Models\Voice\whisper-shadow\voice-refiner.manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "VOICE_RELEASE_MANIFEST_MISSING" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 2 -or [string]$manifest.buildIdentity -ne $BuildIdentity) { throw "VOICE_RELEASE_MANIFEST_IDENTITY_MISMATCH" }

function Read-Evidence([string]$name) {
    $path = Join-Path $AcceptanceRoot ($name + ".json")
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "VOICE_RELEASE_EVIDENCE_MISSING:$name" }
    try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { throw "VOICE_RELEASE_EVIDENCE_INVALID:$name" }
}

$corpus = Read-Evidence "voice-shadow-corpus"
$farField = Read-Evidence "far-field-voice"
$benchmark = Read-Evidence "voice-refiner-thread-benchmark"
foreach ($item in @($corpus, $farField, $benchmark)) {
    if ([string]$item.status -ne "PASSED" -or [string]$item.buildIdentity -ne $BuildIdentity) { throw "VOICE_RELEASE_EVIDENCE_NOT_PASSED" }
}
if ([string]$corpus.captureMode -ne "LIVE" -or [string]$farField.mode -ne "LIVE") { throw "VOICE_RELEASE_HARDWARE_EVIDENCE_REQUIRED" }
if ([string]$benchmark.schema -ne "voice-refiner-thread-benchmark-v1" -or $null -eq $benchmark.selectedThreadCount) { throw "VOICE_RELEASE_BENCHMARK_INVALID" }
if ([int]$benchmark.selectedThreadCount -lt 1 -or [int]$benchmark.selectedThreadCount -gt 4) { throw "VOICE_RELEASE_THREAD_COUNT_INVALID" }
foreach ($item in @($corpus, $farField)) {
    if ($null -eq $item.refiner -or [int]$item.refiner.abiVersion -ne [int]$manifest.native.abiVersion) { throw "VOICE_RELEASE_REFINER_ATTESTATION_MISSING" }
    if ([string]$item.refiner.modelSha256 -ne [string]$manifest.model.sha256 -or
        [string]$item.refiner.nativeSha256 -ne [string]$manifest.native.sha256 -or
        [string]$item.refiner.modelRevision -ne [string]$manifest.model.revision -or
        [string]$item.refiner.whisperCppRevision -ne [string]$manifest.native.whisperCppRevision -or
        [string]$item.refiner.bridgeRevision -ne [string]$manifest.native.bridgeRevision) {
        throw "VOICE_RELEASE_REFINER_IDENTITY_MISMATCH"
    }
    if ([bool]$item.safety.audioIncluded -or [bool]$item.safety.transcriptIncluded -or [bool]$item.safety.textIncluded) { throw "VOICE_RELEASE_PRIVACY_GATE_FAILED" }
}
if ([int]$corpus.falseRecorderMutations -ne 0 -or [int]$corpus.falseStopMutations -ne 0 -or [int]$corpus.queueDropCount -ne 0) { throw "VOICE_RELEASE_SAFETY_GATE_FAILED" }
if ([double]$corpus.refinerTimeoutCount -ge [math]::Max(1, [double]$corpus.caseCount * 0.01)) { throw "VOICE_RELEASE_TIMEOUT_GATE_FAILED" }

[ordered]@{
    schema = "voice-release-evidence-v1"
    status = "PASSED"
    buildIdentity = $BuildIdentity
    selectedThreadCount = [int]$benchmark.selectedThreadCount
    corpus = "PASSED"
    farField = "PASSED"
    benchmark = "PASSED"
    privacy = "PASSED"
} | ConvertTo-Json -Depth 5

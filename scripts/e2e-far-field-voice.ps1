[CmdletBinding()]
param(
    [ValidateSet("Contract", "Installed")][string]$Mode = "Contract",
    [string]$VoiceHostPath,
    [string]$FixtureRoot,
    [string]$OutputPath,
    [string]$ExpectedBuildIdentity,
    [string]$RefinerManifestPath,
    [ValidateRange(1,4)][int]$ThreadCount = 1,
    [ValidateRange(1, 200)][int]$TargetPerCase = 20,
    [ValidateRange(15, 900)][int]$TimeoutSeconds = 180,
    [switch]$AllowDiagnosticFixtures
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repo "artifacts\acceptance\far-field-voice-v1.json"
}
if ([string]::IsNullOrWhiteSpace($VoiceHostPath)) {
    $VoiceHostPath = Join-Path ${env:ProgramFiles} "WhisperX Atom\VoiceHost\WhisperX.Atom.Voice.Host.exe"
}

# Fixture names are intentionally explicit.  A fixture is a recorded replay
# of the same command/query set at one distance and one room condition.
$cases = @(
    @{ distance = "0.5m"; condition = "quiet"; file = "0.5m\quiet.wav"; minimumRecall = 0.98 },
    @{ distance = "0.5m"; condition = "office"; file = "0.5m\office.wav"; minimumRecall = 0.98 },
    @{ distance = "0.5m"; condition = "ventilation"; file = "0.5m\ventilation.wav"; minimumRecall = 0.98 },
    @{ distance = "0.5m"; condition = "conversation"; file = "0.5m\conversation.wav"; minimumRecall = 0.98 },
    @{ distance = "0.5m"; condition = "tts-playback"; file = "0.5m\tts-playback.wav"; minimumRecall = 0.98 },
    @{ distance = "1m"; condition = "quiet"; file = "1m\quiet.wav"; minimumRecall = 0.98 },
    @{ distance = "1m"; condition = "office"; file = "1m\office.wav"; minimumRecall = 0.98 },
    @{ distance = "1m"; condition = "ventilation"; file = "1m\ventilation.wav"; minimumRecall = 0.98 },
    @{ distance = "1m"; condition = "conversation"; file = "1m\conversation.wav"; minimumRecall = 0.98 },
    @{ distance = "1m"; condition = "tts-playback"; file = "1m\tts-playback.wav"; minimumRecall = 0.98 },
    @{ distance = "2m"; condition = "quiet"; file = "2m\quiet.wav"; minimumRecall = 0.95 },
    @{ distance = "2m"; condition = "office"; file = "2m\office.wav"; minimumRecall = 0.95 },
    @{ distance = "2m"; condition = "ventilation"; file = "2m\ventilation.wav"; minimumRecall = 0.95 },
    @{ distance = "2m"; condition = "conversation"; file = "2m\conversation.wav"; minimumRecall = 0.95 },
    @{ distance = "2m"; condition = "tts-playback"; file = "2m\tts-playback.wav"; minimumRecall = 0.95 },
    @{ distance = "3m"; condition = "quiet"; file = "3m\quiet.wav"; minimumRecall = 0.85 },
    @{ distance = "3m"; condition = "office"; file = "3m\office.wav"; minimumRecall = 0.85 },
    @{ distance = "3m"; condition = "ventilation"; file = "3m\ventilation.wav"; minimumRecall = 0.85 },
    @{ distance = "3m"; condition = "conversation"; file = "3m\conversation.wav"; minimumRecall = 0.85 }
    ,@{ distance = "3m"; condition = "tts-playback"; file = "3m\tts-playback.wav"; minimumRecall = 0.85 }
)

function Get-InstalledIdentity([string]$Path, [string]$ExpectedRoot) {
    $valid = $false
    $identity = $null
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $full = [IO.Path]::GetFullPath($Path)
        $root = ([IO.Path]::GetFullPath($ExpectedRoot)).TrimEnd('\') + '\'
        $valid = $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
        if ($valid) { $identity = [Diagnostics.FileVersionInfo]::GetVersionInfo($full).ProductVersion }
    }
    [pscustomobject]@{
        pathValid = $valid
        buildIdentity = $identity
        sha256 = if ($valid) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        identityValid = $valid -and -not [string]::IsNullOrWhiteSpace($identity) -and $identity -notmatch '(?i)(^|[+.-])(dev|dirty)([+.-]|$)'
    }
}

function Write-Artifact([object]$Artifact) {
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $Artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Output (ConvertTo-Json ([ordered]@{ output = $OutputPath; status = $Artifact.status }) -Compress)
}

if ($Mode -eq "Contract") {
    Write-Artifact ([ordered]@{
        schema = "far-field-voice-acceptance-v2"
        status = "CONTRACT_ONLY"
        mode = "CONTRACT"
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        targetPerCase = $TargetPerCase
        threadCount = $ThreadCount
        matrix = @($cases | ForEach-Object {
            [ordered]@{ distance = $_.distance; condition = $_.condition; minimumRecall = $_.minimumRecall; fixtureRequired = $_.file }
        })
        safety = [ordered]@{ recorderInvocations = 0; brokerInvocations = 0; audioIncluded = $false; transcriptIncluded = $false }
    })
    exit 0
}

if (-not $AllowDiagnosticFixtures) {
    Write-Artifact ([ordered]@{
        schema = "far-field-voice-acceptance-v2"
        status = "BLOCKED_BY_HARDWARE"
        mode = "LIVE_REQUIRED"
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        targetPerCase = $TargetPerCase
        matrix = @($cases | ForEach-Object { [ordered]@{ distance = $_.distance; condition = $_.condition; minimumRecall = $_.minimumRecall } })
        safety = [ordered]@{ recorderInvocations = 0; brokerInvocations = 0; audioIncluded = $false; transcriptIncluded = $false; fixturePathsIncluded = $false }
        reason = "REAL_MICROPHONE_MATRIX_REQUIRED"
    })
    exit 4
}

$identity = Get-InstalledIdentity $VoiceHostPath (Join-Path ${env:ProgramFiles} "WhisperX Atom\VoiceHost")
$installRoot = Split-Path (Split-Path $VoiceHostPath -Parent) -Parent
if ([string]::IsNullOrWhiteSpace($ExpectedBuildIdentity)) {
    $identityManifest = Join-Path $installRoot "Desktop\build-identity.json"
    if (Test-Path -LiteralPath $identityManifest) { $ExpectedBuildIdentity = [string](Get-Content -LiteralPath $identityManifest -Raw | ConvertFrom-Json).buildIdentity }
}
if ([string]::IsNullOrWhiteSpace($RefinerManifestPath)) { $RefinerManifestPath = Join-Path $installRoot "VoiceHost\Models\Voice\whisper-shadow\voice-refiner.manifest.json" }
$refinerManifest = if (Test-Path -LiteralPath $RefinerManifestPath -PathType Leaf) { Get-Content -LiteralPath $RefinerManifestPath -Raw | ConvertFrom-Json } else { $null }
$results = [System.Collections.Generic.List[object]]::new()
$errors = [System.Collections.Generic.List[string]]::new()
$rootFull = $null
if (-not [string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $rootFull = [IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\')
}

if (-not $identity.pathValid -or -not $identity.identityValid -or [string]$identity.buildIdentity -ne $ExpectedBuildIdentity) { $errors.Add("VOICE_HOST_BUILD_MISMATCH") }
if ($null -eq $refinerManifest -or $refinerManifest.schemaVersion -ne 2 -or [string]$refinerManifest.buildIdentity -ne $ExpectedBuildIdentity) { $errors.Add("VOICE_REFINER_MANIFEST_MISMATCH") }
if ($null -eq $rootFull -or -not (Test-Path -LiteralPath $rootFull -PathType Container)) { $errors.Add("FAR_FIELD_FIXTURE_ROOT_MISSING") }

foreach ($case in $cases) {
    $fixture = $null
    $fixtureValid = $false
    if ($rootFull) {
        $fixture = [IO.Path]::GetFullPath((Join-Path $rootFull $case.file))
        $fixtureValid = $fixture.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $fixture -PathType Leaf)
    }
    if (-not $fixtureValid) {
        $results.Add([ordered]@{ distance = $case.distance; condition = $case.condition; fixturePresent = $false; target = $TargetPerCase; detections = 0; recall = 0; falseActivations = $null; queueDrops = $null; passed = $false; errorCode = "FAR_FIELD_FIXTURE_MISSING" })
        continue
    }

    $tempLog = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-far-field-" + [guid]::NewGuid().ToString("N") + ".log")
    $tempErr = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-far-field-" + [guid]::NewGuid().ToString("N") + ".err")
    $parsed = $null
    $errorCode = $null
    try {
        # Pass arguments as an array so a fixture path containing spaces is
        # not re-parsed by a shell.  The path was already constrained below
        # FixtureRoot, so no caller-controlled executable/path is accepted.
        $arguments = @("--replay", $fixture, "--dry-run")
        $process = Start-Process -FilePath $VoiceHostPath -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $tempLog -RedirectStandardError $tempErr
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            $errorCode = "FAR_FIELD_REPLAY_TIMEOUT"
        }
        else {
            $line = @(Get-Content -LiteralPath $tempLog -ErrorAction SilentlyContinue) | Where-Object { $_ -match '"mode"\s*:\s*"replay-dry-run"' } | Select-Object -Last 1
            if ($line) { $parsed = $line | ConvertFrom-Json } else { $errorCode = "FAR_FIELD_REPLAY_RESULT_MISSING" }
        }
    }
    catch { $errorCode = "FAR_FIELD_REPLAY_FAILED" }
    finally {
        Remove-Item -LiteralPath $tempLog -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $tempErr -Force -ErrorAction SilentlyContinue
    }

    $detections = if ($parsed) { [int]$parsed.detections } else { 0 }
    $falseActivations = if ($parsed) { [int]$parsed.falseActivations } else { $null }
    $endpoints = if ($parsed) { [int]$parsed.recognizedEndpoints } else { 0 }
    $recall = if ($TargetPerCase -gt 0) { [Math]::Round($detections / $TargetPerCase, 4) } else { 0 }
    $passed = $null -ne $parsed -and $recall -ge [double]$case.minimumRecall -and $falseActivations -eq 0
    if (-not $passed -and $null -eq $errorCode) { $errorCode = "FAR_FIELD_GATE_NOT_READY" }
    $results.Add([ordered]@{ distance = $case.distance; condition = $case.condition; fixturePresent = $true; target = $TargetPerCase; detections = $detections; recognizedEndpoints = $endpoints; recall = $recall; minimumRecall = $case.minimumRecall; falseActivations = $falseActivations; queueDrops = $null; passed = $passed; errorCode = $errorCode })
}

$passed = $errors.Count -eq 0 -and $results.Count -eq $cases.Count -and (@($results | Where-Object { -not $_.passed }).Count -eq 0)
Write-Artifact ([ordered]@{
    schema = "far-field-voice-acceptance-v2"
    status = "DIAGNOSTIC_ONLY"
    mode = "FIXTURE_DIAGNOSTIC"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    build = $identity
    buildIdentity = $ExpectedBuildIdentity
    expectedBuildIdentity = $ExpectedBuildIdentity
    refiner = if ($refinerManifest) { [ordered]@{ modelRevision = [string]$refinerManifest.model.revision; modelSha256 = [string]$refinerManifest.model.sha256; whisperCppRevision = [string]$refinerManifest.native.whisperCppRevision; bridgeRevision = [string]$refinerManifest.native.bridgeRevision; nativeSha256 = [string]$refinerManifest.native.sha256; abiVersion = [int]$refinerManifest.native.abiVersion } } else { $null }
    threadCount = $ThreadCount
    targetPerCase = $TargetPerCase
    matrix = @($results)
    errors = @($errors)
    safety = [ordered]@{ recorderInvocations = 0; brokerInvocations = 0; audioIncluded = $false; transcriptIncluded = $false; fixturePathsIncluded = $false }
})
exit 4

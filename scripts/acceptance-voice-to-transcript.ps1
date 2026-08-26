[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$CoreEvidencePath,
    [string]$VoiceHostPath,
    [string]$MicrophoneDeviceId,
    [int]$Target = 50,
    [switch]$RunMicrophone
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repo "artifacts\acceptance\voice-to-transcript-v1.json" }
if ([string]::IsNullOrWhiteSpace($CoreEvidencePath)) { $CoreEvidencePath = Join-Path $repo "artifacts\acceptance\core-e2e-v1.json" }
if ([string]::IsNullOrWhiteSpace($VoiceHostPath)) { $VoiceHostPath = Join-Path ${env:ProgramFiles} "WhisperX Atom\VoiceHost\WhisperX.Atom.Voice.Host.exe" }
$output = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null

$expectedRoot = [IO.Path]::GetFullPath((Join-Path ${env:ProgramFiles} "WhisperX Atom\VoiceHost"))
$pathOk = (Test-Path -LiteralPath $VoiceHostPath -PathType Leaf) -and
    ([IO.Path]::GetFullPath($VoiceHostPath).StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase))
$runResult = $null
$voiceGatePassed = $false
$voiceGateError = $null

function Get-AliasCount($result, [string]$name) {
    if ($null -eq $result -or $null -eq $result.aliases) { return 0 }
    $property = $result.aliases.PSObject.Properties[$name]
    if ($null -eq $property) { return 0 }
    return [int]$property.Value
}

if ($RunMicrophone) {
    if (-not $pathOk) { throw "VOICE_HOST_BUILD_MISMATCH: installed Voice Host was not found under Program Files." }
    $arguments = "--mic-acceptance $([Math]::Clamp($Target, 1, 500))"
    if (-not [string]::IsNullOrWhiteSpace($MicrophoneDeviceId)) { $arguments += " --microphone-id `"$($MicrophoneDeviceId.Replace('"', ''))`"" }
    $tempLog = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-voice-gate-" + [Guid]::NewGuid().ToString("N") + ".log")
    $tempError = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-voice-gate-" + [Guid]::NewGuid().ToString("N") + ".err")
    try {
        $process = Start-Process -FilePath $VoiceHostPath -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $tempLog -RedirectStandardError $tempError
        $process.WaitForExit()
        $lines = @(Get-Content -LiteralPath $tempLog -ErrorAction SilentlyContinue)
        $jsonLine = $lines | Where-Object { $_ -match '"mode"\s*:\s*"mic-acceptance-dry-run"' } | Select-Object -Last 1
        if ($jsonLine) { $runResult = $jsonLine | ConvertFrom-Json }
        if ($runResult) {
            $myfodiy = Get-AliasCount $runResult "myfodiy"
            $mefodiy = Get-AliasCount $runResult "mefodiy"
            $atom = Get-AliasCount $runResult "atom"
            $targetCount = [Math]::Clamp($Target, 1, 500)
            $requiredAccepted = if ($targetCount -ge 50) { 45 } else { [Math]::Ceiling($targetCount * 0.90) }
            $requiredMyfodiy = if ($targetCount -ge 50) { 27 } else { [Math]::Ceiling($targetCount * 0.54) }
            $voiceGatePassed = ([int]$runResult.detections -ge $requiredAccepted) -and ($myfodiy -ge $requiredMyfodiy) -and
                ([int]$runResult.audioQueueDrops -eq 0) -and ([int]$runResult.falseActivations -eq 0) -and
                (($myfodiy + $mefodiy + $atom) -ge $requiredAccepted)
            if (-not $voiceGatePassed) { $voiceGateError = "VOICE_GATE_NOT_READY" }
        } else { $voiceGateError = "VOICE_GATE_NO_RESULT" }
    } finally {
        Remove-Item -LiteralPath $tempLog -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $tempError -Force -ErrorAction SilentlyContinue
    }
}

$core = $null
if (Test-Path -LiteralPath $CoreEvidencePath -PathType Leaf) {
    try { $core = Get-Content -LiteralPath $CoreEvidencePath -Raw | ConvertFrom-Json } catch { $core = $null }
}
$fields = @("voiceTraceId", "commandId", "localSessionId", "serverSessionId", "meetingId", "mediaAssetId", "asrJobId", "transcriptV1Id")
$chain = [ordered]@{}
foreach ($field in $fields) {
    $chain[$field] = if ($core -and ($core.PSObject.Properties.Name -contains $field)) { [string]$core.$field } else { $null }
}
$chainReady = ($fields | Where-Object { [string]::IsNullOrWhiteSpace([string]$chain[$_]) }).Count -eq 0
$status = if ($voiceGatePassed -and $chainReady) { "VOICE_TO_TRANSCRIPT_READY" } elseif ($RunMicrophone -and $voiceGatePassed) { "VOICE_GATE_PASSED_CORE_E2E_PENDING" } else { "BLOCKED" }
$buildIdentity = if ($pathOk) { [Diagnostics.FileVersionInfo]::GetVersionInfo($VoiceHostPath).ProductVersion } else { $null }

# This artifact is intentionally sanitized: no recognized text, audio paths,
# credentials, tokens or transcript contents are copied from the runner output.
$artifact = [ordered]@{
    schema = "voice-to-transcript-v1"
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    status = $status
    buildIdentity = $buildIdentity
    voiceHostPath = if ($pathOk) { $VoiceHostPath } else { $null }
    installedPathValid = $pathOk
    endpoint = [ordered]@{ requestedDeviceId = $MicrophoneDeviceId; effectiveDeviceId = if ($runResult) { [string]$runResult.microphoneDeviceId } else { $null }; effectiveDeviceName = if ($runResult) { [string]$runResult.microphoneName } else { $null } }
    aliases = if ($runResult) { $runResult.aliases } else { [ordered]@{ myfodiy = 0; mefodiy = 0; atom = 0 } }
    falseActivations = if ($runResult) { [int]$runResult.falseActivations } else { $null }
    backgroundFalseActivations = $null
    queueDrops = if ($runResult) { [int64]$runResult.audioQueueDrops } else { $null }
    latencyMs = [ordered]@{ recognitionP50 = $null; recognitionP95 = $null; commandAckP50 = $null; totalP95 = $null }
    voiceGateError = $voiceGateError
    chain = $chain
}
$artifact | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output (ConvertTo-Json ([ordered]@{ output = $output; status = $status; installedPathValid = $pathOk }) -Compress)
if ($status -eq "VOICE_TO_TRANSCRIPT_READY") { exit 0 }
exit 4

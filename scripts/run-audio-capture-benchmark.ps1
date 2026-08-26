[CmdletBinding()]
param(
    [string]$PipeName = 'WhisperXAtomRecorderHost',
    [string]$DeviceId = '',
    [ValidateRange(1,40)][int]$DurationSeconds = 10,
    [switch]$KeepAudio,
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts\acceptance\audio-capture-parity' }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
try {
    $pipe.Connect(5000)
    $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
    $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
    $writer.AutoFlush = $true
    $payload = [ordered]@{ deviceId = if ($DeviceId) { $DeviceId } else { $null }; durationSeconds = $DurationSeconds; keepAudio = [bool]$KeepAudio }
    $writer.WriteLine(([ordered]@{ command = 'RUN_AUDIO_CAPTURE_BENCHMARK'; protocolVersion = 6; payload = $payload } | ConvertTo-Json -Compress -Depth 8))
    $response = $reader.ReadLine() | ConvertFrom-Json
    $capture = $response.audioCaptureAb
    $report = [ordered]@{
        schemaVersion = 2
        scenario = 'audio-capture-parity'
        captureMode = 'LIVE'
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        # A single native probe is evidence collection only. The full gate
        # requires the Audacity/AudioGraph/RAW comparison and WER runner.
        status = if ($response.ok -eq $true) { 'READY_FOR_REVIEW' } else { 'BLOCKED' }
        parityComplete = $false
        inputs = @('AUDACITY_BASELINE','AUDIOGRAPH','WASAPI_SHARED_NATIVE','WASAPI_RAW')
        buildIdentity = [string]$capture.buildIdentity
        response = [ordered]@{ ok = [bool]$response.ok; state = [string]$response.state; error = [string]$response.error }
        capture = [ordered]@{
            variant = [string]$capture.captureVariant
            deviceIdSha256 = if ($capture.deviceId) {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$capture.deviceId))).ToLowerInvariant() }
                finally { $sha.Dispose() }
            } else { $null }
            nativeSampleRate = $capture.nativeSampleRate
            nativeChannels = $capture.nativeChannels
            nativeSampleFormat = $capture.nativeSampleFormat
            nativeSha256 = $capture.nativeSha256
            diagnosticDirectory = if ($KeepAudio) { $capture.diagnosticDirectory } else { $null }
        }
        safety = [ordered]@{ audioIncluded = [bool]$KeepAudio; transcriptIncluded = $false; credentialsIncluded = $false; tokensIncluded = $false; pathsIncluded = [bool]$KeepAudio }
    }
    $path = Join-Path $OutputRoot ("report-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Output $path
    if ($response.ok -ne $true) { exit 2 }
}
finally { $pipe.Dispose() }

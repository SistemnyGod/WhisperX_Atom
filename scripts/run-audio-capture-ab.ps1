[CmdletBinding()]
param(
    [string]$PipeName = 'WhisperXAtomRecorderHost',
    [string]$DeviceId = '',
    [ValidateRange(13,40)][int]$DurationSeconds = 13,
    [ValidateRange(0,10)][double]$SilenceSeconds = 3,
    [ValidateRange(1,30)][double]$SpeechSeconds = 10,
    [switch]$KeepAudio,
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts\acceptance\audio-capture-ab' }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
try {
    $pipe.Connect(5000)
    $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
    $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
    $writer.AutoFlush = $true
    $payload = [ordered]@{ deviceId = if ($DeviceId) { $DeviceId } else { $null }; durationSeconds = $DurationSeconds; silenceSeconds = $SilenceSeconds; speechSeconds = $SpeechSeconds; keepAudio = [bool]$KeepAudio }
    $writer.WriteLine(([ordered]@{ command = 'RUN_AUDIO_CAPTURE_AB'; protocolVersion = 6; payload = $payload } | ConvertTo-Json -Compress -Depth 8))
    $response = $reader.ReadLine() | ConvertFrom-Json
    $report = [ordered]@{
        schemaVersion = 1
        scenario = 'audio-capture-ab'
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = if ($response.ok -eq $true) { 'PASSED' } else { 'BLOCKED' }
        response = [ordered]@{
            ok = [bool]$response.ok
            state = [string]$response.state
            error = [string]$response.error
            audioCaptureAb = $response.audioCaptureAb
        }
        safety = [ordered]@{ audioIncluded = [bool]$KeepAudio; transcriptIncluded = $false; credentialsIncluded = $false; tokensIncluded = $false }
    }
    $path = Join-Path $OutputRoot ("report-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Output $path
    if ($response.ok -ne $true) { exit 2 }
}
finally { $pipe.Dispose() }

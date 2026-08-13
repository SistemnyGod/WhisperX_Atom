[CmdletBinding()]
param(
    [string]$DataRoot = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pipeName = "WhisperXAtomRecorderHost"
$env:AUDIO_CAPTURE_ENGINE = "AUDIOGRAPH"

function Invoke-Health {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine((([ordered]@{ command = "HEALTH"; protocolVersion = 6; payload = @{} }) | ConvertTo-Json -Compress -Depth 8))
        $readTask = $reader.ReadLineAsync()
        if (-not $readTask.Wait(5000)) { throw "HEALTH_RESPONSE_TIMEOUT" }
        $line = $readTask.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "HEALTH_EMPTY_RESPONSE" }
        $response = $line | ConvertFrom-Json
        if ($null -eq $response) { throw "HEALTH_NULL_RESPONSE" }
        if ([int]$response.currentProtocolVersion -lt 6 -or [int]$response.minimumSupportedProtocolVersion -gt 6) {
            throw "HEALTH_PROTOCOL_INCOMPATIBLE"
        }
        return $response
    }
    finally {
        if ($null -ne $writer) { try { $writer.Dispose() } catch { } }
        if ($null -ne $reader) { try { $reader.Dispose() } catch { } }
        try { $pipe.Dispose() } catch { }
    }
}

function Invoke-EmptyClient {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
    }
    finally {
        $pipe.Dispose()
    }
}

try {
    $startArgs = @{ ReadyTimeoutSeconds = 20 }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) { $startArgs.DataRoot = $DataRoot }
    & (Join-Path $repo "scripts\start-recorder-host.ps1") @startArgs

    $pidPath = Join-Path $repo "artifacts\runtime\recorder-host.pid"
    $pidBefore = [int](Get-Content -LiteralPath $pidPath -Raw).Trim()
    if ($null -eq (Get-Process -Id $pidBefore -ErrorAction SilentlyContinue)) { throw "RECORDER_HOST_NOT_RUNNING_BEFORE_TEST" }

    Invoke-EmptyClient
    Start-Sleep -Milliseconds 500

    $aliveAfterEmpty = $null -ne (Get-Process -Id $pidBefore -ErrorAction SilentlyContinue)
    if (-not $aliveAfterEmpty) { throw "RECORDER_HOST_DIED_AFTER_EMPTY_CLIENT" }

    $health = Invoke-Health
    $pidAfter = [int](Get-Content -LiteralPath $pidPath -Raw).Trim()
    $healthAfterEmpty = $null -ne $health
    $ready = $aliveAfterEmpty -and $healthAfterEmpty -and $pidBefore -eq $pidAfter

    [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow
        hostPidBefore = $pidBefore
        hostPidAfter = $pidAfter
        gates = [ordered]@{
            RECORDER_HOST_SURVIVES_EMPTY_CLIENT = $aliveAfterEmpty
            RECORDER_HOST_HEALTH_AFTER_EMPTY_CLIENT = $healthAfterEmpty
            RECORDER_HOST_READY = $ready
        }
    } | ConvertTo-Json -Depth 8

    if (-not $ready) { exit 1 }
}
finally {
    Remove-Item Env:AUDIO_CAPTURE_ENGINE -ErrorAction SilentlyContinue
}

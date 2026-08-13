[CmdletBinding()]
param(
    [ValidateSet("LegacyWasapi", "AudioGraph")]
    [string]$CaptureEngine = "LegacyWasapi",
    [switch]$ProbeAudio,
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo ("artifacts\diagnostics\runtime-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

function Get-CurrentIdentity {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    [ordered]@{
        user = $identity.Name
        sid = $identity.User.Value
        windowsSessionId = (Get-Process -Id $PID).SessionId
        processId = $PID
    }
}

function Invoke-RecorderHealth([string]$PipeName) {
    $pipe = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect(1500)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine(([ordered]@{ command = "HEALTH"; protocolVersion = if ($CaptureEngine -eq "AudioGraph") { 6 } else { 5 }; payload = @{} } | ConvertTo-Json -Compress -Depth 4))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { return $null }
        return $line | ConvertFrom-Json
    }
    catch { return [ordered]@{ ok = $false; error = "RECORDER_HEALTH_UNAVAILABLE" } }
    finally { if ($null -ne $pipe) { $pipe.Dispose() } }
}

$pipeName = if ($CaptureEngine -eq "AudioGraph") { "WhisperXAtomRecorderHost" } else { "WhisperXAtomAgent" }
$health = Invoke-RecorderHealth $pipeName
$probeDirectory = Join-Path $OutputRoot "audio-probe"
$probeResult = $null
if ($ProbeAudio) {
    try {
        & (Join-Path $repo "scripts\probe-audio-runtime.ps1") -CaptureEngine $CaptureEngine -OutputRoot $probeDirectory
        $probePath = if ($CaptureEngine -eq "AudioGraph") { Join-Path $probeDirectory "audiograph-report.json" } else { Join-Path $probeDirectory "comparison.json" }
        if (Test-Path -LiteralPath $probePath -PathType Leaf) { $probeResult = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json }
    }
    catch { $probeResult = [ordered]@{ success = $false; errorCode = "AUDIO_PROBE_FAILED"; errorDetail = $_.Exception.Message } }
}

$service = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction SilentlyContinue
$processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @("WhisperX.Atom.Recorder.Host", "WhisperX.Atom.Recorder.Service") } | ForEach-Object {
    $startedAt = $null
    try { $startedAt = $_.StartTime.ToUniversalTime() } catch { }
    [ordered]@{ name = $_.ProcessName; processId = $_.Id; sessionId = $_.SessionId; startedAt = $startedAt }
})
$report = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow
    identity = Get-CurrentIdentity
    capture = [ordered]@{ engine = $CaptureEngine.ToUpperInvariant(); pipe = $pipeName; health = $health; probe = $probeResult }
    recorderService = [ordered]@{ name = "WhisperXAtomRecorder"; installed = $null -ne $service; state = if ($service) { [string]$service.Status } else { "NOT_FOUND" } }
    processes = $processes
    files = [ordered]@{ probeDirectory = if ($ProbeAudio) { $probeDirectory } else { $null } }
    safety = [ordered]@{ secretsIncluded = $false; tokensIncluded = $false; cookiesIncluded = $false; audioContentIncluded = $false; transcriptIncluded = $false }
}
$report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $OutputRoot "report.json") -Encoding utf8
Write-Host "Runtime diagnostics: $(Join-Path $OutputRoot 'report.json')"

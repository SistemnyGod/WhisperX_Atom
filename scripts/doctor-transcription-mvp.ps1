[CmdletBinding()]
param([switch]$SkipRegistry)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = Join-Path $repo "artifacts\transcription-mvp"
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$checks = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()

function Check([string]$name, [scriptblock]$action, [switch]$WarningOnly) {
    try { $checks[$name] = & $action }
    catch {
        $checks[$name] = if ($WarningOnly) { "warning" } else { "failed" }
        if (-not $WarningOnly) { $failures.Add($name) }
    }
}

Check "docker" { docker info | Out-Null; "ready" }
Check "compose" { docker compose version | Out-Null; "ready" }
if (-not $SkipRegistry) { Check "registry" { docker manifest inspect nats:2.11-alpine | Out-Null; "ready" } -WarningOnly }
Check "cuda" { nvidia-smi | Out-Null; "ready" } -WarningOnly
Check "hfToken" { if ([string]::IsNullOrWhiteSpace($env:HF_TOKEN)) { throw "HF_TOKEN отсутствует; diarization будет частичной" }; "configured" } -WarningOnly
Check "ffmpeg" { if (-not (Get-Command ffmpeg.exe -ErrorAction Stop)) { throw "ffmpeg отсутствует" }; "ready" }

$dataRoot = if ($env:WHISPERX_DATA_HOST) { $env:WHISPERX_DATA_HOST } else { "C:\WhisperXAtom\Data" }
$inboxRoot = if ($env:WHISPERX_INBOX_HOST) { $env:WHISPERX_INBOX_HOST } else { "C:\WhisperXAtom\Inbox" }
$archiveRoot = if ($env:WHISPERX_ARCHIVE_HOST) { $env:WHISPERX_ARCHIVE_HOST } else { "C:\WhisperXAtom\Archive" }
$apiBase = if ($env:WHISPERX_API_URL) { $env:WHISPERX_API_URL.TrimEnd('/') } else { "http://localhost:8080" }
foreach ($pair in @(@("data",$dataRoot), @("inbox",$inboxRoot), @("archive",$archiveRoot))) {
    $name = $pair[0]; $path = $pair[1]
    Check $name { New-Item -ItemType Directory -Force -Path $path | Out-Null; "ready" }
}

Check "api" { $r=Invoke-WebRequest -UseBasicParsing "$apiBase/health"; if ($r.StatusCode -ne 200) { throw "health $($r.StatusCode)" }; "ready" }
Check "apiReady" { $r=Invoke-WebRequest -UseBasicParsing "$apiBase/ready"; if ($r.StatusCode -ne 200) { throw "ready $($r.StatusCode)" }; "ready" }
Check "services" {
    $names = @(docker compose --env-file (Join-Path $repo ".env") -f compose.dev.yml --profile core --profile gpu ps --services)
    foreach ($required in @("postgres","nats","api","tusd","outbox-relay","import-worker","media-worker","gpu-worker")) { if ($names -notcontains $required) { throw "service $required absent" } }
    "ready"
}
Check "transcriptOnly" {
    $running = @(docker compose --env-file (Join-Path $repo ".env") -f compose.dev.yml --profile llm --profile llm-diagnostic ps --services --filter status=running)
    foreach ($excluded in @("summary-worker", "llama-server")) { if ($running -contains $excluded) { throw "excluded service $excluded is running" } }
    "ready"
}
Check "recorder" {
    $service = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction Stop
    if ($service.Status -ne "Running") { throw "Recorder Service $($service.Status)" }
    "ready"
} -WarningOnly

$report = [ordered]@{ generatedAtUtc=[DateTimeOffset]::UtcNow; runtime="transcription-mvp"; checks=$checks; ok=($failures.Count -eq 0); failures=$failures }
$report | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $artifactRoot "doctor.json")
$report | ConvertTo-Json -Depth 6
if ($failures.Count -gt 0) { exit 1 }

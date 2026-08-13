[CmdletBinding()]
param(
    [switch]$SkipRegistry,
    [string]$Username,
    [string]$Password
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
. (Join-Path $PSScriptRoot "Resolve-RecorderRuntime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo
if ([string]::IsNullOrWhiteSpace($Username)) { $Username = if ($env:BOOTSTRAP_ADMIN_USERNAME) { $env:BOOTSTRAP_ADMIN_USERNAME } else { "admin" } }
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = $env:BOOTSTRAP_ADMIN_PASSWORD }
$artifactRoot = Join-Path $repo "artifacts\transcription-mvp"
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$checks = [ordered]@{}
$diagnostics = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()

function Check([string]$name, [scriptblock]$action, [switch]$WarningOnly) {
    try { $checks[$name] = & $action }
    catch {
        $checks[$name] = if ($WarningOnly) { "warning" } else { "failed" }
        if (-not $WarningOnly) { $failures.Add($name) }
    }
}

function Get-RunningWhisperXServiceNames {
    $composeArgs = @(
        "compose", "--env-file", (Join-Path $repo ".env"),
        "-f", (Join-Path $repo "compose.dev.yml"),
        "--profile", "core", "--profile", "gpu",
        "ps", "--status", "running", "--services"
    )

    $composeNames = @(& docker @composeArgs)
    if ($LASTEXITCODE -eq 0 -and $composeNames.Count -gt 0) {
        return @($composeNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    # Docker Desktop may allow a read-only `docker ps` while denying the
    # compose project pipe. The container names still provide enough
    # evidence for a diagnostic check without mutating runtime state.
    $containerNames = @(& docker ps --format "{{.Names}}")
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect running Docker containers" }

    $serviceNames = [System.Collections.Generic.List[string]]::new()
    foreach ($containerName in $containerNames) {
        if ($containerName -match '^whisperx-atom-(?<service>.+)-\d+$') {
            $serviceNames.Add($Matches.service)
        }
    }
    if ($serviceNames.Count -eq 0) { throw "No running WhisperX containers found" }
    return @($serviceNames | Sort-Object -Unique)
}

function Test-RecorderHostPipe {
    $pipeName = (Resolve-RecorderRuntime).PipeName
    $pipe = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect(1000)
        return $pipe.IsConnected
    }
    catch { return $false }
    finally { if ($null -ne $pipe) { $pipe.Dispose() } }
}

# The recorder, API and workers can be healthy even when this terminal does not
# have permission to Docker Desktop's named pipe. Runtime readiness below is the
# authoritative health check; keep the Docker probe as supplemental evidence.
Check "docker" { docker ps --format "{{.ID}}" | Out-Null; if ($LASTEXITCODE -ne 0) { throw "Docker daemon is not accessible" }; "ready" } -WarningOnly
Check "compose" { docker compose version | Out-Null; "ready" }
if (-not $SkipRegistry) { Check "registry" { docker manifest inspect nats:2.11-alpine | Out-Null; "ready" } -WarningOnly }
Check "cuda" { nvidia-smi | Out-Null; "ready" } -WarningOnly
Check "hfToken" { if ([string]::IsNullOrWhiteSpace($env:HF_TOKEN)) { throw "HF_TOKEN отсутствует; diarization будет частичной" }; "configured" } -WarningOnly
Check "ffmpeg" { if (-not (Get-Command ffmpeg.exe -ErrorAction Stop)) { throw "ffmpeg отсутствует" }; "ready" }

$dataRoot = if ($env:WHISPERX_DATA_HOST) { $env:WHISPERX_DATA_HOST } else { "C:\WhisperXAtom\Data" }
$inboxRoot = if ($env:WHISPERX_INBOX_HOST) { $env:WHISPERX_INBOX_HOST } else { "C:\WhisperXAtom\Inbox" }
$archiveRoot = if ($env:WHISPERX_ARCHIVE_HOST) { $env:WHISPERX_ARCHIVE_HOST } else { "C:\WhisperXAtom\Archive" }
# This doctor targets the local Development compose profile. Production has a
# separate deployment command and must be checked through its HTTPS gateway.
$apiBase = if ($env:WHISPERX_DEV_API_URL) { $env:WHISPERX_DEV_API_URL.TrimEnd('/') } else { "http://127.0.0.1:8080" }
$gpuMode = if ($env:GPU_WORKER_MODE) { $env:GPU_WORKER_MODE.ToLowerInvariant() } else { "host" }
foreach ($pair in @(@("data",$dataRoot), @("inbox",$inboxRoot), @("archive",$archiveRoot))) {
    $name = $pair[0]; $path = $pair[1]
    Check $name { New-Item -ItemType Directory -Force -Path $path | Out-Null; "ready" }
}

Check "api" { $r=Invoke-WebRequest -UseBasicParsing "$apiBase/health"; if ($r.StatusCode -ne 200) { throw "health $($r.StatusCode)" }; "ready" }
Check "apiReady" { $r=Invoke-WebRequest -UseBasicParsing "$apiBase/ready"; if ($r.StatusCode -ne 200) { throw "ready $($r.StatusCode)" }; "ready" }
Check "systemReadiness" {
    if ([string]::IsNullOrWhiteSpace($Password)) { throw "BOOTSTRAP_ADMIN_PASSWORD is required for authenticated readiness" }
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $loginBody = @{ username = $Username; password = $Password } | ConvertTo-Json -Compress
    $login = Invoke-WebRequest -UseBasicParsing -Uri "$apiBase/api/auth/login" -Method Post -ContentType "application/json" -Body $loginBody -WebSession $session
    if ($login.StatusCode -ne 200) { throw "API login failed" }
    $response = Invoke-WebRequest -UseBasicParsing -Uri "$apiBase/api/system/readiness" -WebSession $session
    $payload = $response.Content | ConvertFrom-Json
    $diagnostics.systemReadiness = $payload
    if (-not $payload.ready) { throw "system readiness is false" }
    "ready"
}
Check "summaryRuntime" {
    if (-not $diagnostics.systemReadiness) { throw "system readiness unavailable" }
    $qwen = $diagnostics.systemReadiness.components.qwen
    $diagnostics.summaryWorker = $diagnostics.systemReadiness.components.workers.'summary-worker'
    $workerCapabilities = $diagnostics.summaryWorker.capabilities
    $diagnostics.qwenModel = $qwen
    $diagnostics.llamaCpp = [ordered]@{
        status = $qwen.status
        reason = $qwen.reason
        binaryAvailable = $workerCapabilities.llamaRuntimeAvailable
    }
    $diagnostics.gpuLease = [ordered]@{
        status = if ($qwen.status -eq "BUSY") { "BUSY" } else { "READY" }
        reason = $qwen.reason
    }
    $diagnostics.qwenModel = [ordered]@{
        status = $qwen.status
        reason = $qwen.reason
        path = $workerCapabilities.modelPath
        manifest = $workerCapabilities.manifestPath
        revision = $workerCapabilities.modelManifestRevision
        sha256 = $workerCapabilities.modelManifestSha256
        expectedSha256 = $workerCapabilities.modelExpectedSha256
        size = $workerCapabilities.modelManifestSize
        validationReason = $workerCapabilities.modelValidationReason
    }
    if ($qwen.status -eq "UNAVAILABLE") { throw "Qwen runtime unavailable: $($qwen.reason)" }
    $qwen.status
} -WarningOnly
Check "services" {
    try {
        $names = @(Get-RunningWhisperXServiceNames)
        $requiredServices = @("postgres","nats","api","tusd","outbox-relay","import-worker","media-worker")
        if ($gpuMode -eq "container") { $requiredServices += "gpu-worker" }
        foreach ($required in $requiredServices) { if ($names -notcontains $required) { throw "service $required absent" } }
        "ready"
    }
    catch {
        if ($diagnostics.systemReadiness -and $diagnostics.systemReadiness.ready) {
            "ready-api"
        } else {
            throw
        }
    }
}
Check "transcriptOnly" {
    try {
        $running = @(Get-RunningWhisperXServiceNames)
        foreach ($excluded in @("summary-worker", "llama-server")) { if ($running -contains $excluded) { throw "excluded service $excluded is running" } }
        "ready"
    }
    catch {
        # Without access to the Docker pipe, running worker composition cannot
        # be inspected from this terminal. Do not turn an otherwise healthy
        # runtime into a false-negative result.
        if ($checks.docker -eq "warning" -and $diagnostics.systemReadiness -and $diagnostics.systemReadiness.ready) {
            "unverified"
        } else {
            throw
        }
    }
}
Check "recorder" {
    if (Test-RecorderHostPipe) { return "ready" }
    $service = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq "Running") { return "ready" }
    $runtimeRoot = Join-Path $repo "artifacts\runtime"
    $pidPath = Join-Path $runtimeRoot "recorder-host.pid"
    if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
        try {
            $hostPid = [int](Get-Content -LiteralPath $pidPath -Raw).Trim()
            if ((Get-Process -Id $hostPid -ErrorAction SilentlyContinue) -and (Test-RecorderHostPipe)) { return "ready" }
        } catch { }
    }
    throw "Recorder Service/host is not running"
} -WarningOnly

$report = [ordered]@{ generatedAtUtc=[DateTimeOffset]::UtcNow; runtime="transcription-mvp"; checks=$checks; diagnostics=$diagnostics; ok=($failures.Count -eq 0); failures=$failures }
$report | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $artifactRoot "doctor.json")
$report | ConvertTo-Json -Depth 6
if ($failures.Count -gt 0) { exit 1 }

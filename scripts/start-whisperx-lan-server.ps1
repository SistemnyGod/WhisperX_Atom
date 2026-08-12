[CmdletBinding()]
param(
    [string]$EnvFile = "",
    [switch]$Rebuild,
    [switch]$EnableQwen,
    [switch]$InstallStartupTask,
    [switch]$ConfigureFirewall
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repo ".env.lan" }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
    throw "ENV_REQUIRED: create $EnvFile from .env.lan.example and replace placeholders."
}
if (-not (Get-Command docker.exe -ErrorAction SilentlyContinue)) { throw "DOCKER_UNAVAILABLE: docker.exe was not found." }

function Read-EnvValue([string]$name) {
    $line = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match "^$name=" } | Select-Object -First 1
    if ($null -eq $line) { return $null }
    return ($line -replace "^$name=", "").Trim()
}

$lanAddress = Read-EnvValue "LAN_BIND_ADDRESS"
$serverOrigin = Read-EnvValue "SERVER_ORIGIN"
$allowHttp = Read-EnvValue "ALLOW_INSECURE_LAN_HTTP"
if ([string]::IsNullOrWhiteSpace($lanAddress) -or [string]::IsNullOrWhiteSpace($serverOrigin)) { throw "LAN_CONFIG_INVALID: LAN_BIND_ADDRESS and SERVER_ORIGIN are required." }
if ($allowHttp -ne "true") { throw "LAN_HTTP_EXPLICIT_REQUIRED: set ALLOW_INSECURE_LAN_HTTP=true only for the isolated LAN profile." }
$originUri = $null
if (-not [Uri]::TryCreate($serverOrigin.TrimEnd('/'), [UriKind]::Absolute, [ref]$originUri) -or $originUri.Scheme -ne "http") { throw "LAN_SERVER_ORIGIN_INVALID: use http://<private-ip>:8080." }
$originAddress = $null
if (-not [System.Net.IPAddress]::TryParse($originUri.Host, [ref]$originAddress) -or $originAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw "LAN_SERVER_ORIGIN_INVALID: origin host must be an IPv4 address." }
$octets = $originAddress.GetAddressBytes()
$private = $octets[0] -eq 10 -or ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or ($octets[0] -eq 192 -and $octets[1] -eq 168)
if (-not $private -or $originAddress.IPAddressToString -eq "127.0.0.1") { throw "LAN_SERVER_ORIGIN_INVALID: origin must be a non-loopback private IPv4." }
$bindAddress = $null
if (-not [System.Net.IPAddress]::TryParse($lanAddress, [ref]$bindAddress) -or $bindAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw "LAN_BIND_ADDRESS_INVALID: use a private IPv4 address." }
$bindOctets = $bindAddress.GetAddressBytes()
$bindPrivate = $bindOctets[0] -eq 10 -or ($bindOctets[0] -eq 172 -and $bindOctets[1] -ge 16 -and $bindOctets[1] -le 31) -or ($bindOctets[0] -eq 192 -and $bindOctets[1] -eq 168)
if (-not $bindPrivate -or $bindAddress.IPAddressToString -eq "127.0.0.1") { throw "LAN_BIND_ADDRESS_INVALID: address must be a non-loopback private IPv4." }
if ($bindAddress.IPAddressToString -ne $originAddress.IPAddressToString) { throw "LAN_BIND_ADDRESS_MISMATCH: LAN_BIND_ADDRESS must match SERVER_ORIGIN host." }
foreach ($secretName in @("POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN", "AGENT_ENROLLMENT_SECRET")) {
    $secret = Read-EnvValue $secretName
    if ([string]::IsNullOrWhiteSpace($secret) -or $secret.Length -lt 16 -or $secret -match "^(generate-|replace-with|change-me|password|changeme)$") {
        throw "LAN_SECRET_INVALID: $secretName must be a strong non-placeholder value."
    }
}

foreach ($path in @("C:\WhisperXAtom\Data", "C:\WhisperXAtom\Inbox", "C:\WhisperXAtom\Archive", "C:\WhisperXAtom\Models")) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

Push-Location $repo
try {
    docker info | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "DOCKER_UNAVAILABLE: Docker Desktop is not ready." }
    $composeBase = @("compose", "--env-file", $EnvFile, "-f", "compose.dev.yml", "-f", "compose.lan.yml")
    $coreCompose = $composeBase + @("--profile", "core", "--profile", "lan", "up", "-d", "--pull", "never")
    if ($Rebuild) { $coreCompose += "--build" }
    $coreCompose += @("postgres", "nats", "api", "tusd", "lan-gateway")
    & docker @coreCompose
    if ($LASTEXITCODE -ne 0) { throw "LAN_CORE_START_FAILED" }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    $coreReady = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $live = Invoke-WebRequest -UseBasicParsing -Uri "$serverOrigin/health/live" -TimeoutSec 5
            $ready = Invoke-WebRequest -UseBasicParsing -Uri "$serverOrigin/health/ready" -TimeoutSec 5
            if ($live.StatusCode -eq 200 -and $ready.StatusCode -eq 200) { $coreReady = $true; break }
        } catch { }
        Start-Sleep -Seconds 3
    }
    if (-not $coreReady) { throw "SERVER_DEGRADED: core health did not become ready within 120 seconds." }

    # Do not activate workers against an inherited backlog. Cancelled/failed
    # historical records remain untouched; only runnable work blocks startup.
    $guardCompose = $composeBase + @("--profile", "core", "exec", "-T", "postgres", "psql", "-U", "whisperx", "-d", "whisperx_atom", "-At", "-c", "SELECT (SELECT COUNT(*) FROM jobs WHERE status IN ('QUEUED','RUNNING','RETRY_WAIT','MEDIA_RETRY_WAIT')) || ' ' || (SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL);")
    $guardText = (& docker @guardCompose | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $guardText -notmatch '^\d+\s+\d+$') { throw "STARTUP_QUEUE_GUARD_FAILED: could not inspect jobs/outbox state." }
    $guardCounts = $guardText -split '\s+'
    $runnableJobs = [int64]$guardCounts[0]
    $pendingOutbox = [int64]$guardCounts[1]
    if ($runnableJobs -gt 0 -or $pendingOutbox -gt 0) {
        throw "STARTUP_QUEUE_GUARD_BLOCKED: runnable_jobs=$runnableJobs pending_outbox=$pendingOutbox. Resolve or explicitly cancel old work before enabling workers."
    }

    $workerCompose = $composeBase + @("--profile", "core", "--profile", "gpu", "--profile", "lan")
    if ($EnableQwen) { $workerCompose += @("--profile", "llm") }
    $workerCompose += @("up", "-d", "--pull", "never")
    if ($Rebuild) { $workerCompose += "--build" }
    $workerServices = @("outbox-relay", "import-worker", "media-worker", "gpu-worker")
    if ($EnableQwen) { $workerServices += "summary-worker" }
    $workerCompose += $workerServices
    & docker @workerCompose
    if ($LASTEXITCODE -ne 0) { throw "LAN_WORKER_START_FAILED" }

    $requiredServices = if ($EnableQwen) { @("postgres", "nats", "api", "tusd", "outbox-relay", "import-worker", "media-worker", "gpu-worker", "summary-worker", "lan-gateway") } else { @("postgres", "nats", "api", "tusd", "outbox-relay", "import-worker", "media-worker", "gpu-worker", "lan-gateway") }
    $psArgs = $composeBase + @("--profile", "core", "--profile", "gpu", "--profile", "lan")
    if ($EnableQwen) { $psArgs += @("--profile", "llm") }
    $psArgs += @("ps", "--format", "{{.Service}} {{.State}}")
    $stateText = (& docker @psArgs | Out-String)
    $degraded = @($requiredServices | Where-Object { $stateText -notmatch ("(?m)^" + [regex]::Escape($_) + "\s+running") })
    if ($degraded.Count -gt 0) { Write-Warning "SERVER_DEGRADED: services not running: $($degraded -join ', ')" }
    if ($ConfigureFirewall) {
        & (Join-Path $PSScriptRoot "configure-whisperx-lan-firewall.ps1") -Subnet (Read-EnvValue "LAN_SUBNET") -Port 8080
    }
    if ($InstallStartupTask) {
        & (Join-Path $PSScriptRoot "install-whisperx-lan-startup-task.ps1") -EnvFile $EnvFile
    }
    $qwenText = if ($EnableQwen) { "Qwen enabled" } else { "Qwen disabled; enable with -EnableQwen after transcript gates" }
    Write-Host "WhisperX Atom LAN server core started at $serverOrigin ($qwenText)" -ForegroundColor Green
}
finally { Pop-Location }

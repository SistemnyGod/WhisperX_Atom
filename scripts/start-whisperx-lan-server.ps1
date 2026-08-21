[CmdletBinding()]
param(
    [string]$EnvFile = "",
    [switch]$Rebuild,
    [switch]$EnableQwen,
    [switch]$EnableAssistant,
    [switch]$SkipLegacyStop,
    [switch]$InstallStartupTask,
    [switch]$ConfigureFirewall
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectName = "whisperx-atom"
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
$gpuMode = Read-EnvValue "GPU_WORKER_MODE"
$autoSummary = Read-EnvValue "AUTO_SUMMARY_ENABLED"
$assistantEnabled = Read-EnvValue "ASSISTANT_ENABLED"
$enableDiarization = Read-EnvValue "ENABLE_DIARIZATION"
$diarizationMode = Read-EnvValue "DIARIZATION_MODE"
$diarizationRevision = Read-EnvValue "DIARIZATION_MODEL_REVISION"
$hfToken = Read-EnvValue "HF_TOKEN"
if ([string]::IsNullOrWhiteSpace($assistantEnabled)) { $assistantEnabled = "true" }
if ([string]::IsNullOrWhiteSpace($lanAddress) -or [string]::IsNullOrWhiteSpace($serverOrigin)) { throw "LAN_CONFIG_INVALID: LAN_BIND_ADDRESS and SERVER_ORIGIN are required." }
if ($allowHttp -ne "true") { throw "LAN_HTTP_EXPLICIT_REQUIRED: set ALLOW_INSECURE_LAN_HTTP=true only for the isolated LAN profile." }
if ($gpuMode -ne "container") { throw "LAN_GPU_MODE_REQUIRED: set GPU_WORKER_MODE=container in .env.lan." }
# A persisted true value is an explicit operator choice and must survive a
# reboot/startup task. The switch remains available for one-off development
# runs, while invalid values fail closed instead of silently changing GPU
# behaviour.
if ($autoSummary -notin @("true", "false", "")) { throw "LAN_QWEN_FLAG_INVALID: AUTO_SUMMARY_ENABLED must be true or false." }
if ($autoSummary -eq "true") { $EnableQwen = $true }
if ([string]::IsNullOrWhiteSpace($enableDiarization)) { $enableDiarization = "true" }
if ($enableDiarization -notin @("true", "false")) { throw "LAN_DIARIZATION_FLAG_INVALID: ENABLE_DIARIZATION must be true or false." }
if ([string]::IsNullOrWhiteSpace($diarizationMode)) { $diarizationMode = "preferred" }
if ($diarizationMode -notin @("preferred", "required", "disabled")) { throw "LAN_DIARIZATION_MODE_INVALID: use preferred, required or disabled." }
if ($enableDiarization -eq "true" -and $diarizationMode -ne "disabled") {
    # A floating or placeholder model revision makes a rebuild non-reproducible.
    # Fail before Docker starts rather than advertising a diarization-capable
    # runtime that may silently pull a different gated model later.
    if ([string]::IsNullOrWhiteSpace($diarizationRevision) -or $diarizationRevision -match "^(replace-with|latest|main|master|dev|dirty)" -or $diarizationRevision -notmatch '^[0-9a-fA-F]{7,64}$') {
        throw "LAN_DIARIZATION_REVISION_REQUIRED: DIARIZATION_MODEL_REVISION must be an immutable model revision (commit/SHA)."
    }
    # Never start a LAN runtime that advertises diarization while the worker
    # cannot authenticate to the gated pyannote repositories.  The token is
    # read from the operator-owned env file and is never logged.
    if ([string]::IsNullOrWhiteSpace($hfToken) -or $hfToken -notmatch '^hf_[A-Za-z0-9]{20,}$') {
        throw "LAN_DIARIZATION_TOKEN_REQUIRED: ENABLE_DIARIZATION=true requires a valid HF_TOKEN in .env.lan."
    }
    $env:HF_TOKEN = $hfToken
}
# Exporting the value for this Compose invocation is important: compose.lan.yml
# otherwise expands the .env.lan default and may start a worker that never
# queues automatic summaries.
if ($EnableQwen) {
    $env:AUTO_SUMMARY_ENABLED = "true"
    $autoSummary = "true"
}
if ($EnableAssistant) {
    # Assistant uses the same Qwen worker but does not enable automatic
    # meeting summaries.  This keeps the user-facing assistant independent
    # from AUTO_SUMMARY_ENABLED.
    $env:ASSISTANT_ENABLED = "true"
    $assistantEnabled = "true"
}
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
& (Join-Path $PSScriptRoot "ensure-supervisor-health-token.ps1") -EnvFile $EnvFile
foreach ($secretName in @("POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN", "AGENT_ENROLLMENT_SECRET", "SUPERVISOR_HEALTH_TOKEN")) {
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
    if (-not $SkipLegacyStop) {
        # Match the old Compose project by label. Name-prefix matching is
        # unsafe because the canonical project's lan-gateway is named
        # whisperx-atom-lan-gateway-1 as well.
        $legacyNames = @(docker ps -a --filter "label=com.docker.compose.project=whisperx-atom-lan" --format "{{.Names}}")
        if ($LASTEXITCODE -ne 0) { throw "LEGACY_RUNTIME_INSPECTION_FAILED" }
        $legacyRunning = @($legacyNames | ForEach-Object {
            $state = (& docker inspect -f "{{.State.Running}}" $_ 2>$null | Out-String).Trim()
            if ($state -eq "true") { $_ }
        })
        foreach ($legacy in $legacyRunning) {
            Write-Warning "Stopping preserved legacy container $legacy"
            docker stop $legacy | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "LEGACY_RUNTIME_STOP_FAILED: $legacy" }
        }
    }
    $composeBase = @("compose", "--project-name", $projectName, "--env-file", $EnvFile, "-f", "compose.dev.yml", "-f", "compose.lan.yml")
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
    $artifact = Join-Path $repo "artifacts\acceptance\lan-server"
    New-Item -ItemType Directory -Force -Path $artifact | Out-Null
    [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow
        project = $projectName
        serverOrigin = $serverOrigin
        status = "SERVER_CORE_READY"
        checks = [ordered]@{ live = "READY"; ready = "READY" }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifact "core-readiness.json") -Encoding utf8

    # Recovery is owned by the API's OperationalRecoveryService and the
    # worker-specific durable recovery commands.  This launcher intentionally
    # does not know job stages or mutate PostgreSQL state; keeping one recovery
    # owner prevents a restart from turning ASR jobs back into media jobs.

    $workerCompose = $composeBase + @("--profile", "core", "--profile", "gpu", "--profile", "lan")
    if ($EnableQwen -or $EnableAssistant -or $assistantEnabled -eq "true") { $workerCompose += @("--profile", "llm") }
    $workerCompose += @("up", "-d", "--pull", "never")
    if ($Rebuild) { $workerCompose += "--build" }
    $workerServices = @("outbox-relay", "import-worker", "media-worker", "gpu-worker")
    if ($EnableQwen -or $EnableAssistant -or $assistantEnabled -eq "true") { $workerServices += "summary-worker" }
    $workerCompose += $workerServices
    & docker @workerCompose
    if ($LASTEXITCODE -ne 0) { throw "LAN_WORKER_START_FAILED" }

    $requiredServices = if ($EnableQwen -or $EnableAssistant -or $assistantEnabled -eq "true") { @("postgres", "nats", "api", "tusd", "outbox-relay", "import-worker", "media-worker", "gpu-worker", "summary-worker", "lan-gateway") } else { @("postgres", "nats", "api", "tusd", "outbox-relay", "import-worker", "media-worker", "gpu-worker", "lan-gateway") }
    $psArgs = $composeBase + @("--profile", "core", "--profile", "gpu", "--profile", "lan")
    if ($EnableQwen -or $EnableAssistant -or $assistantEnabled -eq "true") { $psArgs += @("--profile", "llm") }
    $psArgs += @("ps", "--format", "{{.Service}} {{.State}}")
    $stateText = (& docker @psArgs | Out-String)
    $degraded = @($requiredServices | Where-Object { $stateText -notmatch ("(?m)^" + [regex]::Escape($_) + "\s+running") })
    if ($degraded.Count -gt 0) { Write-Warning "SERVER_DEGRADED: services not running: $($degraded -join ', ')" }

    # A running container is not proof that a worker can process work. The
    # worker Dockerfiles expose a heartbeat-backed healthcheck; wait for it so
    # the launcher cannot report PROCESSING_READY while a worker is stuck
    # during startup or has lost its database connection.
    $healthCompose = $composeBase + @("--profile", "core", "--profile", "gpu", "--profile", "lan")
    if ($EnableQwen -or $EnableAssistant -or $assistantEnabled -eq "true") { $healthCompose += @("--profile", "llm") }
    function Test-WorkerHealthy([string]$service) {
        $containerId = ((& docker @healthCompose ps -q $service 2>$null | Select-Object -First 1) | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($containerId)) { return $false }
        $health = ((& docker inspect -f "{{if .State.Health}}{{.State.Health.Status}}{{else}}no-healthcheck{{end}}" $containerId 2>$null) | Out-String).Trim().ToLowerInvariant()
        # Processing readiness must fail closed. A missing Docker healthcheck
        # is a packaging/configuration defect, not proof that the worker is OK.
        return $health -eq "healthy"
    }
    $workerHealthDeadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    $workerHealthPending = @($workerServices)
    while ($workerHealthPending.Count -gt 0 -and [DateTimeOffset]::UtcNow -lt $workerHealthDeadline) {
        $workerHealthPending = @($workerServices | Where-Object { -not (Test-WorkerHealthy $_) })
        if ($workerHealthPending.Count -gt 0) { Start-Sleep -Seconds 3 }
    }
    if ($workerHealthPending.Count -gt 0) {
        throw "LAN_WORKER_HEALTH_FAILED: $($workerHealthPending -join ', ') did not reach healthy heartbeat state."
    }
    [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow
        project = $projectName
        serverOrigin = $serverOrigin
        status = if ($degraded.Count -eq 0) { "PROCESSING_READY" } else { "SERVER_DEGRADED" }
        assistantMode = if ($EnableQwen) { "AUTO_SUMMARY_ENABLED" } elseif ($assistantEnabled -eq "true" -or $EnableAssistant) { "ASSISTANT_ONLY" } else { "DISABLED" }
        qwen = if ($EnableQwen) { "ENABLED" } else { "DISABLED" }
        summaryWorkerRequired = ($EnableQwen -or $EnableAssistant -or $assistantEnabled -eq "true")
        requiredServices = $requiredServices
        degradedServices = $degraded
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifact "processing-readiness.json") -Encoding utf8
    [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow
        project = $projectName
        envFile = ".env.lan"
        serverOrigin = $serverOrigin
        profiles = @("core", "gpu", "lan")
        assistant = if ($assistantEnabled -eq "true" -or $EnableAssistant) { "ENABLED" } else { "DISABLED" }
        qwen = if ($EnableQwen) { "ENABLED" } else { "DISABLED" }
        publicBinding = "$lanAddress`:8080"
        legacyPolicy = "preserve_and_stop"
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifact "compose-config.txt") -Encoding utf8
    if ($ConfigureFirewall) {
        & (Join-Path $PSScriptRoot "configure-whisperx-lan-firewall.ps1") -Subnet (Read-EnvValue "LAN_SUBNET") -Port 8080
    }
    if ($InstallStartupTask) {
        & (Join-Path $PSScriptRoot "install-whisperx-lan-startup-task.ps1") -EnvFile $EnvFile
    }
    $qwenText = if ($EnableQwen) { "Qwen enabled; automatic summary after quality V2" } else { "Qwen disabled; enable with -EnableQwen after transcript gates or set AUTO_SUMMARY_ENABLED=true" }
    Write-Host "WhisperX Atom LAN server core started at $serverOrigin ($qwenText)" -ForegroundColor Green
}
finally { Pop-Location }

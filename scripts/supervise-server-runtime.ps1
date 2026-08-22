[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [int]$DockerTimeoutSeconds = 600,
    [int]$PollSeconds = 30
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
$manifestPath = Join-Path $bundle "release-manifest.json"
$startScript = Join-Path $bundle "start-runtime.ps1"
$logRoot = Join-Path $config "Logs"
$logPath = Join-Path $logRoot "server-supervisor.log"
$mutex = $null
$mutexOwned = $false
$script:ConsecutiveFailures = 0
$script:RecoveryCycles = 0
$script:RestartHistory = @{}
$script:UnhealthyServices = @()
$script:IdentityMismatch = $false

New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
if (Test-Path -LiteralPath $logPath -PathType Leaf) {
    $logInfo = Get-Item -LiteralPath $logPath
    if ($logInfo.Length -gt 5MB) {
        $rotated = "$logPath.1"
        if (Test-Path -LiteralPath $rotated) { Remove-Item -LiteralPath $rotated -Force }
        Move-Item -LiteralPath $logPath -Destination $rotated -Force
    }
}

function Write-SupervisorLog([string]$Message, [string]$Level = "INFO") {
    $line = "{0:o} [{1}] {2}" -f [DateTimeOffset]::UtcNow, $Level, $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
}

function Read-EnvValue([string]$Name) {
    $line = Get-Content -LiteralPath $envFile -Encoding utf8 | Where-Object { $_ -match "^$Name=" } | Select-Object -First 1
    if ($null -eq $line) { return $null }
    return ($line -replace "^$Name=", '').Trim()
}

function Assert-StaticRuntimeFiles {
    foreach ($required in @($envFile, $manifestPath, $startScript, (Join-Path $bundle "compose.dev.yml"), (Join-Path $bundle "compose.lan.yml"), (Join-Path $bundle "compose.release.yml"))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "SERVER_RUNTIME_FILE_MISSING" }
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $identity = [string]$manifest.buildIdentity
    $tag = [string]$manifest.releaseTag
    if ([string]::IsNullOrWhiteSpace($identity) -or $identity -match '(?i)dev|dirty' -or [string]::IsNullOrWhiteSpace($tag) -or $tag -match '(?i)dev|dirty|latest') {
        throw "SERVER_RELEASE_IDENTITY_INVALID"
    }
    return $manifest
}

function Start-DockerDesktopIfNeeded {
    $candidates = @(
        (Join-Path ${env:ProgramFiles} "Docker\Docker\Docker Desktop.exe"),
        (Join-Path ${env:LocalAppData} "Docker\Docker Desktop.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    if ($candidates.Count -eq 0) { throw "DOCKER_DESKTOP_NOT_FOUND" }
    try {
        Start-Process -FilePath $candidates[0] -WindowStyle Hidden | Out-Null
        Write-SupervisorLog "Docker Desktop start requested"
    }
    catch {
        throw "DOCKER_DESKTOP_START_FAILED"
    }
}

function Wait-DockerEngine([int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $delay = 5
    $requested = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        & docker info 1>$null 2>$null
        if ($LASTEXITCODE -eq 0) { return $true }
        if (-not $requested) {
            Start-DockerDesktopIfNeeded
            $requested = $true
        }
        Start-Sleep -Seconds $delay
        $delay = [Math]::Min(30, $delay * 2)
    }
    return $false
}

function Get-ComposeArguments([object]$Manifest) {
    $tag = [string]$Manifest.releaseTag
    $identity = [string]$Manifest.buildIdentity
    $env:COMPOSE_PROJECT_NAME = "whisperx-atom"
    $env:WHISPERX_RELEASE_TAG = $tag
    $env:WHISPERX_RELEASE_VERSION = $identity
    $env:WHISPERX_BUILD_IDENTITY = $identity
    $env:WHISPERX_REVISION = [string]$Manifest.commit
    $env:APP_VERSION = $identity
    $arguments = @("compose", "--project-name", "whisperx-atom", "--env-file", $envFile, "-f", (Join-Path $bundle "compose.dev.yml"), "-f", (Join-Path $bundle "compose.lan.yml"), "-f", (Join-Path $bundle "compose.release.yml"), "--profile", "core", "--profile", "gpu", "--profile", "lan")
    if ((Read-EnvValue "AUTO_SUMMARY_ENABLED") -eq "true" -or (Read-EnvValue "ASSISTANT_ENABLED") -ne "false") { $arguments += @("--profile", "llm") }
    return $arguments
}

function Start-WhisperXRuntime([object]$Manifest) {
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $startScript -BundleRoot $bundle -ConfigRoot $config 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-SupervisorLog "Runtime start failed; will retry" "WARN"
        return $false
    }
    Write-SupervisorLog "Runtime start completed"
    return $true
}

function Test-RequiredContainersRunning([object]$Manifest) {
    $compose = Get-ComposeArguments $Manifest
    $running = @(& docker @compose ps --services --status running 2>$null | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $required = @("api", "outbox-relay", "import-worker", "media-worker", "gpu-worker")
    if ((Read-EnvValue "AUTO_SUMMARY_ENABLED") -eq "true" -or (Read-EnvValue "ASSISTANT_ENABLED") -ne "false") { $required += "summary-worker" }
    foreach ($service in $required) {
        if ($running -notcontains $service) { return $false }
    }
    return $true
}

function Test-WhisperXRuntime([object]$Manifest) {
    $script:RuntimeContainersNeedStart = -not (Test-RequiredContainersRunning $Manifest)
    if ($script:RuntimeContainersNeedStart) { return $false }
    $origin = Read-EnvValue "SERVER_ORIGIN"
    if ([string]::IsNullOrWhiteSpace($origin)) {
        Write-SupervisorLog "SERVER_ORIGIN is not configured" "WARN"
        return $false
    }

    try {
        $live = Invoke-RestMethod -Uri ($origin.TrimEnd('/') + "/health/live") -TimeoutSec 5
        if ($null -eq $live) { throw "HEALTH_LIVE_EMPTY" }
    }
    catch {
        Write-SupervisorLog "API health/live probe failed" "WARN"
        $script:UnhealthyServices = @("api")
        return $false
    }

    try {
        $token = Read-EnvValue "SUPERVISOR_HEALTH_TOKEN"
        if ([string]::IsNullOrWhiteSpace($token)) {
            Write-SupervisorLog "SUPERVISOR_HEALTH_TOKEN is not configured" "ERROR"
            return $false
        }
        $headers = @{ "X-WhisperX-Supervisor-Token" = $token }
        $readiness = Invoke-RestMethod -Uri ($origin.TrimEnd('/') + "/api/internal/runtime/readiness") -Headers $headers -TimeoutSec 10
        if ($null -eq $readiness -or $null -eq $readiness.workers -or [string]::IsNullOrWhiteSpace([string]$readiness.buildIdentity)) {
            throw "API_READINESS_INVALID"
        }
        $script:IdentityMismatch = [string]$readiness.buildIdentity -ne [string]$Manifest.buildIdentity
        if ($script:IdentityMismatch -or $readiness.releaseIdentityValid -ne $true) {
            Write-SupervisorLog "Authenticated readiness identity mismatch" "WARN"
            return $false
        }

        $required = @("outbox-relay", "import-worker", "media-worker", "gpu-worker")
        if ((Read-EnvValue "AUTO_SUMMARY_ENABLED") -eq "true" -or (Read-EnvValue "ASSISTANT_ENABLED") -ne "false") { $required += "summary-worker" }
        $unhealthy = [System.Collections.Generic.List[string]]::new()
        foreach ($workerName in $required) {
            $worker = @($readiness.workers | Where-Object { $_.name -eq $workerName }) | Select-Object -First 1
            if ($null -eq $worker) {
                Write-SupervisorLog ("Authenticated readiness missing worker " + $workerName) "WARN"
                $unhealthy.Add($workerName)
                continue
            }
            if ([string]$worker.status -notin @("READY", "BUSY")) {
                Write-SupervisorLog ("Authenticated readiness worker " + $workerName + " is " + [string]$worker.status) "WARN"
                $unhealthy.Add($workerName)
            }
            if ([string]$worker.version -ne [string]$Manifest.buildIdentity) {
                Write-SupervisorLog ("Authenticated readiness worker identity mismatch: " + $workerName) "WARN"
                $script:IdentityMismatch = $true
                $unhealthy.Add($workerName)
            }
        }

        $queue = $readiness.queue
        if ($null -ne $queue -and [int]$queue.orphanedGpuJobs -gt 0) {
            Write-SupervisorLog ("GPU recovery required; orphaned jobs=" + [int]$queue.orphanedGpuJobs) "WARN"
            $unhealthy.Add("gpu-worker")
        }
        $script:UnhealthyServices = @($unhealthy | Select-Object -Unique)
        if ($script:UnhealthyServices.Count -gt 0) { return $false }
        return $true
    }
    catch {
        # A failed or malformed authenticated probe is an API health failure,
        # not an unknown state.  Keep the component in the targeted recovery
        # set so three consecutive failures can restart only the API instead
        # of silently waiting for a broad reconciliation cycle.
        $script:UnhealthyServices = @("api")
        $script:IdentityMismatch = $false
        $detail = if ($_.Exception -and $_.Exception.Message) { $_.Exception.Message } else { "API_READINESS_UNAVAILABLE" }
        if ($detail -eq "API_READINESS_INVALID") {
            Write-SupervisorLog "Authenticated readiness probe returned an invalid payload (API_READINESS_INVALID)" "WARN"
        } else {
            Write-SupervisorLog "Authenticated readiness probe failed; API targeted recovery is eligible" "WARN"
        }
        return $false
    }
}

function Test-RestartBudget([string]$Service) {
    $now = [DateTimeOffset]::UtcNow
    if (-not $script:RestartHistory.ContainsKey($Service)) { $script:RestartHistory[$Service] = [System.Collections.Generic.List[DateTimeOffset]]::new() }
    $history = $script:RestartHistory[$Service]
    for ($i = $history.Count - 1; $i -ge 0; $i--) {
        if ($now - $history[$i] -gt [TimeSpan]::FromMinutes(15)) { $history.RemoveAt($i) }
    }
    if ($history.Count -ge 3) { return $false }
    if ($history.Count -gt 0 -and $now - $history[$history.Count - 1] -lt [TimeSpan]::FromMinutes(2)) { return $false }
    return $true
}

function Invoke-TargetedRecovery([object]$Manifest) {
    if ($script:IdentityMismatch) {
        Write-SupervisorLog "Identity mismatch is fail-closed; targeted restart suppressed" "ERROR"
        return
    }
    $compose = Get-ComposeArguments $Manifest
    $script:RecoveryCycles++
    $services = @($script:UnhealthyServices | Select-Object -Unique)
    if ($services -contains "gpu-worker") {
        if (Test-RestartBudget "gpu-worker") {
            Write-SupervisorLog "GPU ownership/readiness unhealthy; running transactional recovery"
            & docker @compose stop gpu-worker 2>&1 | Out-Null
            $stopped = $LASTEXITCODE -eq 0
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bundle "recover-gpu-runtime.ps1") -BundleRoot $bundle -ConfigRoot $config
            $previewOk = $LASTEXITCODE -eq 0
            if ($previewOk) {
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bundle "recover-gpu-runtime.ps1") -BundleRoot $bundle -ConfigRoot $config -Apply
            }
            if ($stopped -and $previewOk -and $LASTEXITCODE -eq 0) {
                $script:RestartHistory["gpu-worker"].Add([DateTimeOffset]::UtcNow)
                & docker @compose up -d --no-deps --pull never gpu-worker 2>&1 | Out-Null
            } else { Write-SupervisorLog "GPU recovery command failed" "ERROR" }
        } else { Write-SupervisorLog "GPU restart budget exhausted" "ERROR" }
        $services = @($services | Where-Object { $_ -ne "gpu-worker" })
    }
    foreach ($service in $services) {
        if (-not (Test-RestartBudget $service)) {
            Write-SupervisorLog ("Restart budget exhausted for " + $service) "ERROR"
            continue
        }
        Write-SupervisorLog ("Targeted restart requested for " + $service) "WARN"
        & docker @compose restart $service 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $script:RestartHistory[$service].Add([DateTimeOffset]::UtcNow) }
    }
    if ($script:RecoveryCycles -ge 2) {
        $reconcile = @("api", "outbox-relay", "import-worker", "media-worker", "gpu-worker")
        if ((Read-EnvValue "AUTO_SUMMARY_ENABLED") -eq "true" -or (Read-EnvValue "ASSISTANT_ENABLED") -ne "false") { $reconcile += "summary-worker" }
        Write-SupervisorLog "Targeted recovery did not restore readiness; reconciling WhisperX services" "ERROR"
        & docker @compose up -d --no-deps --pull never @reconcile 2>&1 | Out-Null
        $script:RecoveryCycles = 0
    }
}

try {
    $mutex = [Threading.Mutex]::new($false, "Global\WhisperXAtom.ServerSupervisor")
    $mutexOwned = $mutex.WaitOne(0)
    if (-not $mutexOwned) { Write-SupervisorLog "Another supervisor instance is already running"; exit 0 }

    $manifest = Assert-StaticRuntimeFiles
    Write-SupervisorLog ("Supervisor started for identity " + [string]$manifest.buildIdentity)
    while ($true) {
        try {
            if (-not (Wait-DockerEngine $DockerTimeoutSeconds)) {
                Write-SupervisorLog "Docker Engine did not become ready within timeout" "ERROR"
            }
            elseif (-not (Test-WhisperXRuntime $manifest)) {
                if ($script:RuntimeContainersNeedStart) {
                    [void](Start-WhisperXRuntime $manifest)
                    if (Test-WhisperXRuntime $manifest) { Write-SupervisorLog "WhisperX runtime is healthy" }
                    else { Write-SupervisorLog "WhisperX runtime is not healthy yet" "WARN" }
                }
                else {
                    $script:ConsecutiveFailures++
                    Write-SupervisorLog ("WhisperX readiness is unhealthy; consecutive failures=" + $script:ConsecutiveFailures) "WARN"
                    if ($script:ConsecutiveFailures -ge 3) {
                        Invoke-TargetedRecovery $manifest
                        $script:ConsecutiveFailures = 0
                    }
                }
            } else { $script:ConsecutiveFailures = 0; $script:RecoveryCycles = 0 }
        }
        catch {
            Write-SupervisorLog "Supervisor iteration failed with a stable runtime error" "WARN"
        }
        Start-Sleep -Seconds ([Math]::Max(5, $PollSeconds))
    }
}
catch {
    try { Write-SupervisorLog "Supervisor stopped with a stable runtime error" "ERROR" } catch { }
    exit 1
}
finally {
    if ($mutexOwned -and $null -ne $mutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}

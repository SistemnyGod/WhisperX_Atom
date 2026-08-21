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

function Test-WhisperXRuntime([object]$Manifest) {
    $compose = Get-ComposeArguments $Manifest
    $running = @(& docker @compose ps --services --status running 2>$null | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $required = @("api", "outbox-relay", "import-worker", "media-worker", "gpu-worker", "summary-worker")
    foreach ($service in $required) {
        if ($running -notcontains $service) { return $false }
    }
    $origin = Read-EnvValue "SERVER_ORIGIN"
    if (-not [string]::IsNullOrWhiteSpace($origin)) {
        try {
            $null = Invoke-RestMethod -Uri ($origin.TrimEnd('/') + "/health/live") -TimeoutSec 5
        }
        catch { return $false }
    }
    return $true
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
                [void](Start-WhisperXRuntime $manifest)
                if (Test-WhisperXRuntime $manifest) { Write-SupervisorLog "WhisperX runtime is healthy" }
                else { Write-SupervisorLog "WhisperX runtime is not healthy yet" "WARN" }
            }
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

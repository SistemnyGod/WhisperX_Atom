[CmdletBinding()]
param(
    [string]$EnvFile = "",
    [switch]$Rebuild,
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
    $compose = @("compose", "--env-file", $EnvFile, "-f", "compose.dev.yml", "-f", "compose.lan.yml", "--profile", "core", "--profile", "gpu", "--profile", "llm", "--profile", "lan", "up", "-d", "--pull", "never")
    if ($Rebuild) { $compose += "--build" }
    & docker @compose
    if ($LASTEXITCODE -ne 0) { throw "LAN_COMPOSE_START_FAILED" }
    if ($ConfigureFirewall) {
        & (Join-Path $PSScriptRoot "configure-whisperx-lan-firewall.ps1") -Subnet (Read-EnvValue "LAN_SUBNET") -Port 8080
    }
    if ($InstallStartupTask) {
        & (Join-Path $PSScriptRoot "install-whisperx-lan-startup-task.ps1") -EnvFile $EnvFile
    }
    Write-Host "WhisperX Atom LAN server started at $serverOrigin" -ForegroundColor Green
}
finally { Pop-Location }

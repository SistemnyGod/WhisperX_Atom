[CmdletBinding()]
param(
    [string]$EnvFile = "",
    [switch]$CheckOnly,
    [switch]$SkipDockerInfo
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectName = "whisperx-atom"
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repo ".env.production" }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "PRODUCTION_ENV_REQUIRED: create $EnvFile from the deployment secret store." }
if (-not (Get-Command docker.exe -ErrorAction SilentlyContinue)) { throw "DOCKER_UNAVAILABLE: docker.exe was not found." }

function Read-EnvValue([string]$name) {
    $line = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match "^$name=" } | Select-Object -First 1
    if ($null -eq $line) { return $null }
    return ($line -replace "^$name=", "").Trim()
}

function Require-Value([string]$name) {
    $value = Read-EnvValue $name
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match "^(replace-with|generate-|change-me|changeme|password)$") {
        throw "PRODUCTION_CONFIG_REQUIRED: $name is missing or still a placeholder."
    }
    return $value
}

$publicHost = Require-Value "PUBLIC_HOST"
$publicOrigin = Require-Value "PUBLIC_ORIGIN"
$tlsRoot = Require-Value "TLS_CERTS_HOST"
$releaseTag = Require-Value "WHISPERX_RELEASE_TAG"
$cookieSecure = Read-EnvValue "COOKIE_SECURE"
$allowHttp = Read-EnvValue "ALLOW_INSECURE_LAN_HTTP"
$originUri = $null
if (-not [Uri]::TryCreate($publicOrigin.TrimEnd('/'), [UriKind]::Absolute, [ref]$originUri) -or $originUri.Scheme -ne "https") {
    throw "PRODUCTION_HTTPS_REQUIRED: PUBLIC_ORIGIN must use https://."
}
if (-not [string]::Equals($originUri.Host, $publicHost, [StringComparison]::OrdinalIgnoreCase)) {
    throw "PRODUCTION_HOST_MISMATCH: PUBLIC_ORIGIN host must match PUBLIC_HOST."
}
if ($cookieSecure -and $cookieSecure -ne "true") { throw "PRODUCTION_COOKIE_SECURE_REQUIRED" }
if ($allowHttp -eq "true") { throw "PRODUCTION_INSECURE_HTTP_FORBIDDEN" }
if ($releaseTag -match "(^|[-+])(dev|dirty|latest)($|[-+])") { throw "PRODUCTION_RELEASE_TAG_INVALID: dev/dirty/latest tags are forbidden." }
if (-not (Test-Path -LiteralPath $tlsRoot -PathType Container)) { throw "PRODUCTION_TLS_CERTS_MISSING: $tlsRoot" }
foreach ($file in @("fullchain.pem", "privkey.pem")) {
    $path = Join-Path $tlsRoot $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "PRODUCTION_TLS_FILE_MISSING: $path" }
}
foreach ($secretName in @("POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN", "AGENT_ENROLLMENT_SECRET", "VOICE_HOST_TOKEN", "SUPERVISOR_HEALTH_TOKEN")) {
    [void](Require-Value $secretName)
}

Push-Location $repo
try {
    if (-not $SkipDockerInfo) {
        docker info | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "DOCKER_UNAVAILABLE: Docker Desktop is not ready." }
    }
    $compose = @(
        "compose", "--project-name", $projectName, "--env-file", $EnvFile,
        "-f", "compose.dev.yml", "-f", "compose.release.yml", "-f", "compose.prod.yml",
        "--profile", "core", "--profile", "gpu", "--profile", "llm", "--profile", "prod"
    )
    $config = & docker @($compose + @("config"))
    if ($LASTEXITCODE -ne 0) { throw "PRODUCTION_COMPOSE_CONFIG_FAILED" }
    if (($config -join "`n") -match "(?m)^\s+build:") { throw "PRODUCTION_BUILD_CONTEXT_PRESENT: release override must remove build sections." }
    if ($CheckOnly) {
        Write-Output "Production configuration validated: HTTPS, secure cookies, pinned release tag, TLS files and immutable images contract."
        exit 0
    }
    & docker @($compose + @("up", "-d", "--no-build"))
    if ($LASTEXITCODE -ne 0) { throw "PRODUCTION_RUNTIME_START_FAILED" }
    & docker @($compose + @("ps"))
    if ($LASTEXITCODE -ne 0) { throw "PRODUCTION_RUNTIME_INSPECTION_FAILED" }
}
finally { Pop-Location }

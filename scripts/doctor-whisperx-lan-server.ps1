[CmdletBinding()]
param([string]$EnvFile = "", [PSCredential]$Credential)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectName = "whisperx-atom"
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repo ".env.lan" }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "ENV_REQUIRED: $EnvFile is missing." }

function Read-EnvValue([string]$name) {
    $line = Get-Content -LiteralPath $EnvFile -Encoding utf8 | Where-Object { $_ -match "^$name=" } | Select-Object -First 1
    if ($null -eq $line) { return $null }
    return ($line -replace "^$name=", "").Trim()
}

$origin = (Read-EnvValue "SERVER_ORIGIN").TrimEnd('/')
$checks = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [scriptblock]$action) {
    try { $checks[$name] = & $action }
    catch { $checks[$name] = "FAILED"; $failures.Add($name) }
}

Check "composeConfig" {
    & docker compose --project-name $projectName --env-file $EnvFile -f (Join-Path $repo "compose.dev.yml") -f (Join-Path $repo "compose.lan.yml") --profile core --profile gpu --profile llm --profile lan config --quiet
    if ($LASTEXITCODE -ne 0) { throw "compose config failed" }
    "READY"
}
Check "projectName" {
    $names = @(docker compose --project-name $projectName --env-file $EnvFile -f (Join-Path $repo "compose.dev.yml") -f (Join-Path $repo "compose.lan.yml") --profile core --profile gpu --profile lan ps --format "{{.Project}}" 2>$null)
    if ($names.Count -gt 0 -and @($names | Where-Object { $_ -ne $projectName }).Count -gt 0) { throw "unexpected compose project name" }
    $projectName
}
Check "lanConfiguration" {
    $uri = $null
    if (-not [Uri]::TryCreate($origin, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne "http") { throw "SERVER_ORIGIN must be private HTTP" }
    $address = $null
    if (-not [System.Net.IPAddress]::TryParse($uri.Host, [ref]$address)) { throw "SERVER_ORIGIN host must be IPv4" }
    $octets = $address.GetAddressBytes()
    $private = $octets[0] -eq 10 -or ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or ($octets[0] -eq 192 -and $octets[1] -eq 168)
    if (-not $private -or $address.IPAddressToString -eq "127.0.0.1") { throw "SERVER_ORIGIN must be non-loopback private IPv4" }
    $bindAddressText = Read-EnvValue "LAN_BIND_ADDRESS"
    $bindAddress = $null
    if (-not [System.Net.IPAddress]::TryParse($bindAddressText, [ref]$bindAddress) -or $bindAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw "LAN_BIND_ADDRESS must be IPv4" }
    $bindOctets = $bindAddress.GetAddressBytes()
    $bindPrivate = $bindOctets[0] -eq 10 -or ($bindOctets[0] -eq 172 -and $bindOctets[1] -ge 16 -and $bindOctets[1] -le 31) -or ($bindOctets[0] -eq 192 -and $bindOctets[1] -eq 168)
    if (-not $bindPrivate -or $bindAddress.IPAddressToString -eq "127.0.0.1") { throw "LAN_BIND_ADDRESS must be non-loopback private IPv4" }
    if ($bindAddress.IPAddressToString -ne $address.IPAddressToString) { throw "LAN_BIND_ADDRESS must match SERVER_ORIGIN host" }
    foreach ($secretName in @("POSTGRES_PASSWORD", "BOOTSTRAP_ADMIN_PASSWORD", "TUS_HOOK_SECRET", "IMPORT_WORKER_TOKEN", "AGENT_ENROLLMENT_SECRET")) {
        $secret = Read-EnvValue $secretName
        if ([string]::IsNullOrWhiteSpace($secret) -or $secret.Length -lt 16 -or $secret -match "^(generate-|replace-with|change-me|password|changeme)$") { throw "$secretName is missing or placeholder" }
    }
    "READY"
}
Check "gatewayLive" {
    $response = Invoke-WebRequest -UseBasicParsing -Uri "$origin/health/live" -TimeoutSec 5
    if ($response.StatusCode -ne 200) { throw "health/live returned $($response.StatusCode)" }
    "READY"
}
Check "gatewayReady" {
    $response = Invoke-WebRequest -UseBasicParsing -Uri "$origin/health/ready" -TimeoutSec 5
    if ($response.StatusCode -ne 200) { throw "health/ready returned $($response.StatusCode)" }
    "READY"
}
Check "gatewayPort" {
    $address = Read-EnvValue "LAN_BIND_ADDRESS"
    if (-not (Test-NetConnection -ComputerName $address -Port 8080 -InformationLevel Quiet)) { throw "TCP 8080 unavailable" }
    "READY"
}
Check "coreServices" {
    $psArgs = @("compose", "--project-name", $projectName, "--env-file", $EnvFile, "-f", (Join-Path $repo "compose.dev.yml"), "-f", (Join-Path $repo "compose.lan.yml"), "--profile", "core", "--profile", "gpu", "--profile", "lan", "ps", "--format", "{{.Service}} {{.State}}")
    $states = (& docker @psArgs | Out-String)
    $required = @("postgres", "nats", "api", "tusd", "lan-gateway")
    $missing = @($required | Where-Object { $states -notmatch ("(?m)^" + [regex]::Escape($_) + "\s+running") })
    if ($missing.Count -gt 0) { throw "CORE_SERVICES_UNAVAILABLE:$($missing -join ',')" }
    "RUNNING"
}
Check "processingServices" {
    $psArgs = @("compose", "--project-name", $projectName, "--env-file", $EnvFile, "-f", (Join-Path $repo "compose.dev.yml"), "-f", (Join-Path $repo "compose.lan.yml"), "--profile", "core", "--profile", "gpu", "--profile", "lan", "ps", "--format", "{{.Service}} {{.State}} {{.Health}}")
    $states = (& docker @psArgs | Out-String)
    $required = @("outbox-relay", "import-worker", "media-worker", "gpu-worker")
    if ((Read-EnvValue "AUTO_SUMMARY_ENABLED") -eq "true") { $required += "summary-worker" }
    $missing = @($required | Where-Object { $states -notmatch ("(?m)^" + [regex]::Escape($_) + "\s+running\s+healthy") })
    if ($missing.Count -gt 0) { throw "PROCESSING_SERVICES_UNHEALTHY:$($missing -join ',')" }
    "HEALTHY"
}
Check "legacyRuntime" {
    $legacyNames = @(docker ps --filter "label=com.docker.compose.project=whisperx-atom-lan" --format "{{.Names}}")
    $running = @($legacyNames | ForEach-Object {
        $state = (& docker inspect -f "{{.State.Running}}" $_ 2>$null).Trim()
        if ($state -eq "true") { $_ }
    })
    if ($running.Count -gt 0) { throw "LEGACY_RUNTIME_RUNNING:$($running -join ',')" }
    "STOPPED_OR_ABSENT"
}
Check "qwen" {
    if ((Read-EnvValue "AUTO_SUMMARY_ENABLED") -ne "true") { "DISABLED" } else { "ENABLED_REQUIRES_EXPLICIT_GATE" }
}
Check "processingReadiness" {
    if (-not $Credential) { "AUTH_REQUIRED"; return }
    $web = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $body = @{ username = $Credential.UserName; password = $Credential.GetNetworkCredential().Password } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$origin/api/auth/login" -Body $body -ContentType "application/json" -WebSession $web -TimeoutSec 10 | Out-Null
    $readiness = Invoke-RestMethod -Uri "$origin/api/system/readiness" -WebSession $web -TimeoutSec 10
    if (-not $readiness.ready) { throw "PROCESSING_READINESS_DEGRADED" }
    "READY"
}

$report = [ordered]@{ generatedAtUtc=[DateTimeOffset]::UtcNow; runtime="lan"; project=$projectName; serverOrigin=$origin; checks=$checks; failures=$failures; qwen=if((Read-EnvValue "AUTO_SUMMARY_ENABLED") -eq "true"){ "ENABLED" } else { "DISABLED" }; ok=($failures.Count -eq 0) }
$artifact = Join-Path $repo "artifacts\acceptance\lan-server"
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
[ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow
    project = $projectName
    serverOrigin = $origin
    status = if ($checks["gatewayLive"] -eq "READY" -and $checks["gatewayReady"] -eq "READY" -and $checks["coreServices"] -eq "RUNNING") { "SERVER_CORE_READY" } else { "SERVER_DEGRADED" }
    checks = [ordered]@{ live = $checks["gatewayLive"]; ready = $checks["gatewayReady"]; services = $checks["coreServices"] }
} | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $artifact "core-readiness.json")
[ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow
    project = $projectName
    serverOrigin = $origin
    status = if ($checks["processingReadiness"] -eq "READY") { "PROCESSING_READY" } elseif ($checks["processingReadiness"] -eq "AUTH_REQUIRED") { "AUTH_REQUIRED" } else { "PROCESSING_DEGRADED" }
    services = $checks["processingServices"]
    authenticatedReadiness = $checks["processingReadiness"]
} | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $artifact "processing-readiness.json")
$report | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $artifact "doctor.json")
$report | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) { exit 1 }

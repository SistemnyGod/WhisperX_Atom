[CmdletBinding()]
param([string]$EnvFile = "")

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
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
    & docker compose --env-file $EnvFile -f (Join-Path $repo "compose.dev.yml") -f (Join-Path $repo "compose.lan.yml") --profile core --profile gpu --profile llm --profile lan config --quiet
    if ($LASTEXITCODE -ne 0) { throw "compose config failed" }
    "READY"
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

$report = [ordered]@{ generatedAtUtc=[DateTimeOffset]::UtcNow; runtime="lan"; serverOrigin=$origin; checks=$checks; failures=$failures; ok=($failures.Count -eq 0) }
$artifact = Join-Path $repo "artifacts\acceptance\lan-server"
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $artifact "doctor.json")
$report | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) { exit 1 }

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$devCompose = Join-Path $repo "compose.dev.yml"
$prodCompose = Join-Path $repo "compose.prod.yml"
$program = Join-Path $repo "apps\server\WhisperX.Atom.Api\Program.cs"
$caddyProd = Join-Path $repo "infrastructure\caddy\Caddyfile.prod"

function Assert-Contains([string]$content, [string]$expected, [string]$description) {
    if ($content -notmatch [regex]::Escape($expected)) {
        throw "Missing $description"
    }
}

$dev = Get-Content -LiteralPath $devCompose -Raw
$prod = Get-Content -LiteralPath $prodCompose -Raw
$source = Get-Content -LiteralPath $program -Raw
$tls = Get-Content -LiteralPath $caddyProd -Raw

Assert-Contains $dev "ASPNETCORE_ENVIRONMENT: Development" "Development API environment"
Assert-Contains $dev "DOTNET_ENVIRONMENT: Development" "Development .NET environment"
Assert-Contains $dev 'COOKIE_SECURE: "false"' "Development insecure-cookie allowance"
Assert-Contains $dev '127.0.0.1:8080:8080' "Development loopback binding"
Assert-Contains $prod "ASPNETCORE_ENVIRONMENT: Production" "Production API environment"
Assert-Contains $prod 'COOKIE_SECURE: "true"' "Production secure-cookie setting"
Assert-Contains $prod "POSTGRES_PASSWORD:?" "Production required secret guard"
Assert-Contains $prod "prod-gateway:" "Production gateway"
Assert-Contains $tls "tls /etc/caddy/certs/fullchain.pem /etc/caddy/certs/privkey.pem" "Production TLS certificate"
Assert-Contains $source "builder.Environment.IsProduction()" "Production guard"
Assert-Contains $source "PRODUCTION_COOKIE_SECURE_REQUIRED" "Cookie guard marker"
Assert-Contains $source "PRODUCTION_SECRET_INVALID" "Secret guard marker"

Write-Output "API configuration test passed (Development profile, Production override and guards)."

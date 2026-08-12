[CmdletBinding()]
param(
    [string]$ServerOrigin = "http://192.168.2.194:8080",
    [switch]$Managed
)

$ErrorActionPreference = "Stop"
$uri = $null
if (-not [Uri]::TryCreate($ServerOrigin.TrimEnd('/'), [UriKind]::Absolute, [ref]$uri)) { throw "SERVER_ORIGIN_INVALID" }
if ($uri.Scheme -notin @("http", "https")) { throw "SERVER_ORIGIN_SCHEME_INVALID" }
$target = "C:\ProgramData\WhisperXAtom\client-config.json"
$directory = Split-Path -Parent $target
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$json = [ordered]@{ schemaVersion = 1; serverOrigin = $uri.ToString().TrimEnd('/'); managed = [bool]$Managed } | ConvertTo-Json
$part = "$target.part"
Set-Content -LiteralPath $part -Value $json -Encoding utf8
Move-Item -LiteralPath $part -Destination $target -Force
Write-Host "Machine ServerOrigin configured: $($uri.ToString().TrimEnd('/'))" -ForegroundColor Green

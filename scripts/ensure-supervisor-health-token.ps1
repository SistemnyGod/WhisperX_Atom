[CmdletBinding()]
param(
    [string]$EnvFile = "C:\ProgramData\WhisperXAtom\Server\.env.lan"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $EnvFile" }
$lines = @(Get-Content -LiteralPath $EnvFile -Encoding utf8)
$current = ($lines | Where-Object { $_ -match '^SUPERVISOR_HEALTH_TOKEN=' } | Select-Object -First 1)
$value = if ($null -eq $current) { $null } else { ($current -replace '^SUPERVISOR_HEALTH_TOKEN=', '').Trim() }
if ([string]::IsNullOrWhiteSpace($value) -or $value -match '(?i)generate-|replace-with|change-me|password|changeme') {
    $value = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $found = $false
    $lines = @($lines | ForEach-Object {
        if ($_ -match '^SUPERVISOR_HEALTH_TOKEN=') { $found = $true; "SUPERVISOR_HEALTH_TOKEN=$value" } else { $_ }
    })
    if (-not $found) { $lines += "SUPERVISOR_HEALTH_TOKEN=$value" }
    Set-Content -LiteralPath $EnvFile -Value $lines -Encoding utf8
}
Write-Host "SUPERVISOR_HEALTH_TOKEN_READY=true"

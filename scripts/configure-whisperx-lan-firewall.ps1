[CmdletBinding()]
param(
    [string]$Subnet = "192.168.2.0/24",
    [int]$Port = 8080,
    [string]$RuleName = "WhisperX Atom LAN Gateway",
    [string]$InterfaceAlias = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Subnet)) { throw "LAN_SUBNET_REQUIRED" }
if ($Port -lt 1 -or $Port -gt 65535) { throw "LAN_PORT_INVALID" }
if (-not [string]::IsNullOrWhiteSpace($InterfaceAlias)) {
    $profile = Get-NetConnectionProfile -InterfaceAlias $InterfaceAlias -ErrorAction Stop
    if ($profile.NetworkCategory -eq "Public") {
        Set-NetConnectionProfile -InterfaceAlias $InterfaceAlias -NetworkCategory Private
    }
}
New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress $Subnet -Profile Domain,Private -ErrorAction SilentlyContinue | Out-Null
New-NetFirewallRule -DisplayName "$RuleName (Public block)" -Direction Inbound -Action Block -Protocol TCP -LocalPort $Port -Profile Public -ErrorAction SilentlyContinue | Out-Null
Write-Host "Firewall rule configured for TCP $Port from $Subnet; Public profile remains blocked" -ForegroundColor Green

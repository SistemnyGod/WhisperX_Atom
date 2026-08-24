[CmdletBinding()]
param(
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [string]$Reason = "MANUAL_MAINTENANCE"
)

$ErrorActionPreference = "Stop"
$config = [IO.Path]::GetFullPath($ConfigRoot)
$marker = Join-Path $config "maintenance.lock"
New-Item -ItemType Directory -Force -Path $config | Out-Null

$safeReason = ($Reason -replace '[\r\n\x00-\x1f]', ' ').Trim()
if ([string]::IsNullOrWhiteSpace($safeReason)) { $safeReason = "MANUAL_MAINTENANCE" }
if ($safeReason.Length -gt 160) { $safeReason = $safeReason.Substring(0, 160) }

$payload = [ordered]@{
    schemaVersion = 1
    enabledAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    requestedBy = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    reason = $safeReason
}
$temporary = "$marker.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $marker -Force
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}

Write-Host "SERVER_MAINTENANCE_ENABLED=true"
Write-Host "SERVER_MAINTENANCE_MARKER=$marker"
Write-Host "SERVER_MAINTENANCE_NOTE=Supervisor will not start Docker or recover WhisperX services until maintenance mode is cleared"

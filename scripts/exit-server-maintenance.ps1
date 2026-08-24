[CmdletBinding()]
param(
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server"
)

$ErrorActionPreference = "Stop"
$config = [IO.Path]::GetFullPath($ConfigRoot)
$marker = Join-Path $config "maintenance.lock"
if (Test-Path -LiteralPath $marker -PathType Leaf) {
    Remove-Item -LiteralPath $marker -Force
}

Write-Host "SERVER_MAINTENANCE_ENABLED=false"
Write-Host "SERVER_MAINTENANCE_NOTE=The supervisor may restore the WhisperX runtime on its next polling cycle"

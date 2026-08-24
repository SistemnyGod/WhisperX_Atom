[CmdletBinding()]
param(
    [string]$BundleRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$IgnoredArguments
)

$ErrorActionPreference = "Stop"
$config = [IO.Path]::GetFullPath($ConfigRoot)
$launcher = Join-Path $config "run-server-supervisor.ps1"
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw "SERVER_SUPERVISOR_LAUNCHER_MISSING" }

& (Get-Command powershell.exe -ErrorAction Stop).Source -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File $launcher -ConfigRoot $config
exit $LASTEXITCODE

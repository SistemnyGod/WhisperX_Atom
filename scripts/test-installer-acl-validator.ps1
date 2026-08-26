[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$installerPath = Join-Path $repo "apps\desktop\Installer\Install-Service.ps1"
$source = Get-Content -LiteralPath $installerPath -Raw
$startMarker = "# ACL display names are localized"
$endMarker = "if (-not (Test-Path -LiteralPath `$serviceExe))"
$start = $source.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = $source.IndexOf($endMarker, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw "ACL_VALIDATOR_FUNCTION_NOT_FOUND" }

# Load only the production function; dot-sourcing the installer itself would
# install/restart the Windows Service and is intentionally not part of this test.
$definition = $source.Substring($start, $end - $start)
. ([ScriptBlock]::Create($definition))

$root = "C:\ProgramData\WhisperXAtom\Agent"
$dbPath = Join-Path $root "agent.db"
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "ACL_TEST_ROOT_NOT_FOUND: $root" }
if (-not (Test-Path -LiteralPath $dbPath -PathType Leaf)) { throw "ACL_TEST_DB_NOT_FOUND: $dbPath" }

$sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$rootResult = Test-ModifyAccessForSid -Path $root -Sid $sid
$dbResult = Test-ModifyAccessForSid -Path $dbPath -Sid $sid
if (-not $rootResult) { throw "ACL_ROOT_RESULT=false" }
if (-not $dbResult) { throw "ACL_DB_RESULT=false" }

Write-Host "ACL_VALIDATOR_TARGETED_TEST=PASS"
Write-Host "ACL_ROOT_RESULT=true"
Write-Host "ACL_DB_RESULT=true"

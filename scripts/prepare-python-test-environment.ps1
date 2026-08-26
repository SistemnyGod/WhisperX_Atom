[CmdletBinding()]
param(
    [string]$EnvironmentRoot = "artifacts/python-test-env",
    [string]$Wheelhouse = "",
    [switch]$Recreate
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$environment = [IO.Path]::GetFullPath((Join-Path $repo $EnvironmentRoot))
$lock = Join-Path $repo "requirements.test.lock.txt"
if (-not (Test-Path -LiteralPath $lock -PathType Leaf)) { throw "PYTHON_TEST_LOCK_MISSING: $lock" }

if ($Recreate -and (Test-Path -LiteralPath $environment)) {
    if ([IO.Path]::GetDirectoryName($environment) -ne (Join-Path $repo "artifacts")) {
        throw "PYTHON_TEST_ENV_DELETE_REFUSED: $environment"
    }
    Remove-Item -LiteralPath $environment -Recurse -Force
}
if (-not (Test-Path -LiteralPath $environment)) {
    & py -3.12 -m venv $environment
    if ($LASTEXITCODE -ne 0) { throw "PYTHON_TEST_VENV_CREATE_FAILED" }
}

$python = Join-Path $environment "Scripts\python.exe"
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw "PYTHON_TEST_INTERPRETER_MISSING" }
$install = @('-m','pip','install','--disable-pip-version-check','--require-hashes','-r',$lock)
if (-not [string]::IsNullOrWhiteSpace($Wheelhouse)) {
    $resolvedWheelhouse = (Resolve-Path -LiteralPath $Wheelhouse).Path
    $install += @('--no-index','--find-links',$resolvedWheelhouse)
}
& $python @install
if ($LASTEXITCODE -ne 0) { throw "PYTHON_TEST_ENV_INSTALL_FAILED" }
Write-Host "PYTHON_TEST_ENV_READY=$environment"

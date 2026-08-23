[CmdletBinding()]
param(
    [string]$EnvironmentRoot = "artifacts/python-test-env",
    [string[]]$PytestArguments = @('-q'),
    [switch]$Prepare
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$environment = [IO.Path]::GetFullPath((Join-Path $repo $EnvironmentRoot))
if ($Prepare -or -not (Test-Path -LiteralPath (Join-Path $environment 'Scripts\python.exe') -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'prepare-python-test-environment.ps1') -EnvironmentRoot $EnvironmentRoot
    if ($LASTEXITCODE -ne 0) { throw "PYTHON_TEST_ENV_PREPARE_FAILED" }
}
$tempRoot = Join-Path $repo ("artifacts\test-temp\python\" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
try {
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    & (Join-Path $environment 'Scripts\python.exe') -m pytest -p no:cacheprovider @PytestArguments
    if ($LASTEXITCODE -ne 0) { throw "PYTHON_TESTS_FAILED: exit=$LASTEXITCODE" }
} finally {
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "PYTHON_TESTS_OK"

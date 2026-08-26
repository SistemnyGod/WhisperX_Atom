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
$savedPythonPath = $env:PYTHONPATH
try {
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    # The venv's pytest.exe does not always add the repository root to
    # sys.path (notably on Windows).  Keep imports deterministic for the
    # in-tree workers/ and whisperx_atom/ packages.
    $env:PYTHONPATH = if ([string]::IsNullOrWhiteSpace($savedPythonPath)) { $repo } else { "$repo;$savedPythonPath" }
    & (Join-Path $environment 'Scripts\python.exe') -m pytest -p no:cacheprovider @PytestArguments
    if ($LASTEXITCODE -ne 0) { throw "PYTHON_TESTS_FAILED: exit=$LASTEXITCODE" }
} finally {
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
    $env:PYTHONPATH = $savedPythonPath
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "PYTHON_TESTS_OK"

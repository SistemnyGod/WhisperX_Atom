[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
$composeFile = Join-Path $bundle "compose.dev.yml"
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }
if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) { throw "SERVER_COMPOSE_REQUIRED: $composeFile" }

$mode = if ($Apply) { "--apply" } else { "--preview" }
$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',$composeFile)
& docker @compose run --rm --no-deps gpu-worker python -m workers.ml_worker.recovery $mode
if ($LASTEXITCODE -ne 0) { throw "GPU_RUNTIME_RECOVERY_FAILED" }

[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$ConfigRoot = "C:\ProgramData\WhisperXAtom\Server",
    [int]$MaxAgeHours = 24
)

$ErrorActionPreference = "Stop"
if ($MaxAgeHours -lt 24) { throw "HEARTBEAT_RETENTION_TOO_SHORT" }
$bundle = [IO.Path]::GetFullPath($BundleRoot)
$config = [IO.Path]::GetFullPath($ConfigRoot)
$envFile = Join-Path $config ".env.lan"
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw "SERVER_CONFIG_REQUIRED: $envFile" }

function Read-EnvValue([string]$name, [string]$fallback) {
    $line = Get-Content -LiteralPath $envFile -Encoding utf8 | Where-Object { $_ -match "^$name=" } | Select-Object -First 1
    if ($null -eq $line) { return $fallback }
    return ($line -replace "^$name=", '').Trim()
}

$db = Read-EnvValue 'POSTGRES_DB' 'whisperx_atom'
$user = Read-EnvValue 'POSTGRES_USER' 'whisperx'
$compose = @('compose','--project-name','whisperx-atom','--env-file',$envFile,'-f',(Join-Path $bundle 'compose.dev.yml'),'-f',(Join-Path $bundle 'compose.lan.yml'),'-f',(Join-Path $bundle 'compose.release.yml'))
$profiles = @('--profile','core','--profile','gpu','--profile','lan','--profile','llm')
$sql = @"
WITH active AS (
    SELECT worker_name, instance_id
    FROM worker_instances
    WHERE last_seen_at >= now() - interval '10 minutes'
)
DELETE FROM worker_instances w
WHERE w.last_seen_at < now() - interval '$MaxAgeHours hours'
  AND NOT EXISTS (
      SELECT 1 FROM active a
      WHERE a.worker_name = w.worker_name AND a.instance_id = w.instance_id
  )
RETURNING worker_name || ':' || instance_id;
"@
$rows = @(& docker @compose @profiles exec -T postgres psql -U $user -d $db -At -c $sql)
if ($LASTEXITCODE -ne 0) { throw "HEARTBEAT_PRUNE_FAILED" }
Write-Host "STALE_HEARTBEATS_REMOVED=$($rows.Count)"

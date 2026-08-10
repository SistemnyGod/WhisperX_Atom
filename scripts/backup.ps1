[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [switch]$IncludeMedia
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

function Get-EnvValue([string]$Name, [string]$Default) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { return $Default }
    return $value
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-EnvValue "WHISPERX_BACKUP_HOST" "C:\WhisperXAtom\Backups") $stamp
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stage = Join-Path $OutputDirectory "staging"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$postgresDb = Get-EnvValue "POSTGRES_DB" "whisperx_atom"
$postgresUser = Get-EnvValue "POSTGRES_USER" "whisperx"
$postgresPassword = Get-EnvValue "POSTGRES_PASSWORD" ""
$env:PGPASSWORD = $postgresPassword
if ([string]::IsNullOrWhiteSpace($postgresPassword)) { throw "Set POSTGRES_PASSWORD before running backup.ps1" }

Write-Host "Exporting PostgreSQL..."
$dumpPath = Join-Path $stage "postgres.dump"
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = "docker.exe"
$psi.Arguments = 'compose -f compose.dev.yml exec -T postgres pg_dump -Fc -U "' + $postgresUser + '" -d "' + $postgresDb + '"'
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi
if (-not $process.Start()) { throw "failed to start docker for pg_dump" }
$out = [IO.File]::Open($dumpPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $process.StandardOutput.BaseStream.CopyTo($out) } finally { $out.Dispose() }
$stderr = $process.StandardError.ReadToEnd()
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "pg_dump failed: $stderr" }

Copy-Item -LiteralPath "compose.dev.yml" -Destination (Join-Path $stage "compose.dev.yml")
Copy-Item -LiteralPath ".env.example" -Destination (Join-Path $stage ".env.example")
if (Test-Path -LiteralPath "docs/runbook_windows_wsl2.md") {
    Copy-Item -LiteralPath "docs/runbook_windows_wsl2.md" -Destination (Join-Path $stage "runbook_windows_wsl2.md")
}

$dataRoot = Get-EnvValue "WHISPERX_DATA_HOST" "C:\WhisperXAtom\Data"
$archiveRoot = Get-EnvValue "WHISPERX_ARCHIVE_HOST" "C:\WhisperXAtom\Archive"
if ($IncludeMedia) {
    if (Test-Path -LiteralPath $dataRoot) {
        Copy-Item -LiteralPath $dataRoot -Destination (Join-Path $stage "Data") -Recurse
    }
    if (Test-Path -LiteralPath $archiveRoot) {
        Copy-Item -LiteralPath $archiveRoot -Destination (Join-Path $stage "Archive") -Recurse
    }
}

$files = Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($stage.Length + 1)
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest = [ordered]@{
    created_at = (Get-Date).ToUniversalTime().ToString("o")
    includes_media = [bool]$IncludeMedia
    database = $postgresDb
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage "manifest.json") -Encoding UTF8

$archive = Join-Path $OutputDirectory "whisperx-atom-backup-$stamp.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -CompressionLevel Optimal -Force
if (-not (Test-Path -LiteralPath $archive)) { throw "backup archive was not created: $archive" }
Remove-Item -LiteralPath $stage -Recurse -Force
Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
Write-Host "Backup created: $archive" -ForegroundColor Green

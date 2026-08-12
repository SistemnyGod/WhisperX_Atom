[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$BackupArchive,
    [string]$TargetRoot = "C:\WhisperXAtom\Restore",
    [switch]$Apply,
    [switch]$Force,
    [switch]$IncludeMedia,
    [string]$ComposeFile = "compose.dev.yml",
    [string]$PostgresService = "postgres",
    [string]$PostgresContainer = "",
    [string]$Database = "",
    [string]$User = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot; Set-Location $repo
function Get-EnvValue([string]$Name, [string]$Default) { $value = [Environment]::GetEnvironmentVariable($Name); if ([string]::IsNullOrWhiteSpace($value)) { return $Default }; return $value }
function Get-SchemaVersion { $file = Get-ChildItem -LiteralPath (Join-Path $repo "apps\server\WhisperX.Atom.Api\Migrations") -Filter "*.sql" -File | Sort-Object Name | Select-Object -Last 1; if ($null -eq $file) { throw "SCHEMA_MIGRATION_NOT_FOUND" }; return [IO.Path]::GetFileNameWithoutExtension($file.Name) }
function Assert-ContainedPath([string]$Root, [string]$RelativePath) { if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) { throw "BACKUP_UNSAFE_PATH" }; $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar; $candidate = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath)); if (-not $candidate.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "BACKUP_PATH_ESCAPE" }; return $candidate }
function Invoke-PsqlScalar([string]$Sql, [string]$DbUser, [string]$DbName) { if ($PostgresContainer) { & docker exec $PostgresContainer psql -U $DbUser -d $DbName -tAc $Sql } else { & docker compose -f $ComposeFile exec -T $PostgresService psql -U $DbUser -d $DbName -tAc $Sql } }
function Invoke-PgRestore([string]$DumpPath, [string]$DbUser, [string]$DbName) { if ($PostgresContainer) { $inside = "/tmp/whisperx-restore-" + [Guid]::NewGuid().ToString("N") + ".dump"; try { & docker cp $DumpPath "${PostgresContainer}:$inside"; if ($LASTEXITCODE -ne 0) { throw "PG_RESTORE_STAGE_FAILED" }; $output = & docker exec $PostgresContainer pg_restore --clean --if-exists --no-owner -U $DbUser -d $DbName $inside 2>&1; if ($LASTEXITCODE -ne 0) { throw ("PG_RESTORE_FAILED: " + ($output -join " ")) } } finally { & docker exec $PostgresContainer rm -f $inside 2>$null | Out-Null }; return }; $psi = [Diagnostics.ProcessStartInfo]::new(); $psi.FileName = "docker.exe"; $arguments = @("compose", "-f", $ComposeFile, "exec", "-T", $PostgresService, "pg_restore", "--clean", "--if-exists", "--no-owner", "-U", $DbUser, "-d", $DbName); $psi.Arguments = ($arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' '; $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardError = $true; $process = [Diagnostics.Process]::new(); $process.StartInfo = $psi; if (-not $process.Start()) { throw "PG_RESTORE_START_FAILED" }; $input = [IO.File]::OpenRead($DumpPath); try { $input.CopyTo($process.StandardInput.BaseStream) } finally { $input.Dispose(); $process.StandardInput.Close() }; $stderr = $process.StandardError.ReadToEnd(); $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw ("PG_RESTORE_FAILED: " + $stderr.Trim()) } }

$archive = [IO.Path]::GetFullPath($BackupArchive); if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "BACKUP_ARCHIVE_NOT_FOUND" }
$target = [IO.Path]::GetFullPath($TargetRoot); if ($target -eq [IO.Path]::GetPathRoot($target) -or $target.Length -lt 8) { throw "RESTORE_UNSAFE_TARGET" }
$extractRoot = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-restore-" + [Guid]::NewGuid().ToString("N")); New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extractRoot)
    $manifestPath = Join-Path $extractRoot "backup-manifest.json"; if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "BACKUP_MANIFEST_MISSING" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.secretsIncluded -ne $false -or -not $manifest.schemaVersion -or -not $manifest.files) { throw "BACKUP_MANIFEST_INVALID" }
    if ([string]$manifest.schemaVersion -gt (Get-SchemaVersion)) { throw "BACKUP_SCHEMA_NEWER_THAN_RUNTIME" }
    foreach ($entry in $manifest.files) { $file = Assert-ContainedPath $extractRoot ([string]$entry.path); if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "BACKUP_FILE_MISSING" }; $info = Get-Item -LiteralPath $file; if ([long]$entry.bytes -ne $info.Length) { throw "BACKUP_SIZE_MISMATCH" }; if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "BACKUP_HASH_MISMATCH" } }
    $dump = Join-Path $extractRoot "postgres.dump"; if (-not (Test-Path -LiteralPath $dump -PathType Leaf)) { throw "BACKUP_POSTGRES_DUMP_MISSING" }
    if (-not $Apply) { Write-Output ([ordered]@{ verified = $true; applied = $false; schemaVersion = $manifest.schemaVersion; audioPolicy = $manifest.audioPolicy } | ConvertTo-Json -Compress); return }
    $postgresDb = if ($Database) { $Database } else { Get-EnvValue "POSTGRES_DB" "whisperx_atom" }; $postgresUser = if ($User) { $User } else { Get-EnvValue "POSTGRES_USER" "whisperx" }
    $nonEmpty = (Invoke-PsqlScalar "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema='public')" $postgresUser $postgresDb).Trim() -eq "t"
    if (($nonEmpty -or (Test-Path -LiteralPath $target -PathType Container)) -and -not $Force) { throw "RESTORE_TARGET_NOT_EMPTY_FORCE_REQUIRED" }
    if (-not $PSCmdlet.ShouldProcess($target, "restore isolated PostgreSQL backup")) { return }
    New-Item -ItemType Directory -Force -Path $target | Out-Null; $started = [DateTimeOffset]::UtcNow
    Invoke-PgRestore $dump $postgresUser $postgresDb
    if ($IncludeMedia -and [string]$manifest.audioPolicy -eq "archive-included") { foreach ($name in @("Data", "Archive")) { $source = Join-Path $extractRoot $name; if (Test-Path -LiteralPath $source -PathType Container) { Copy-Item -LiteralPath $source -Destination (Join-Path $target $name) -Recurse -Force } } }
    $report = [ordered]@{ restoredAt = [DateTimeOffset]::UtcNow.ToString("o"); durationMs = ([DateTimeOffset]::UtcNow - $started).TotalMilliseconds; database = $postgresDb; schemaVersion = $manifest.schemaVersion; audioPolicy = $manifest.audioPolicy; secretsIncluded = $false; applied = $true }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target "restore-report.json") -Encoding utf8
    Write-Output (Join-Path $target "restore-report.json")
}
finally { if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force } }

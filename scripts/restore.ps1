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
    [string]$User = "",
    [string]$MigrationRoot = "",
    [string]$ArchiveSha256Path = "",
    [switch]$RequireArchiveSha256
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $repo 'compose.dev.yml') -PathType Leaf) -and
    -not (Test-Path -LiteralPath (Join-Path $repo '.env.example') -PathType Leaf)) {
    $repo = Split-Path -Parent $PSScriptRoot
}
Set-Location $repo
function Get-EnvValue([string]$Name, [string]$Default) { $value = [Environment]::GetEnvironmentVariable($Name); if ([string]::IsNullOrWhiteSpace($value)) { return $Default }; return $value }
function Resolve-RepoPath([string]$Path) { if ([string]::IsNullOrWhiteSpace($Path)) { return $null }; if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }; return [IO.Path]::GetFullPath((Join-Path $repo $Path)) }
function Get-SchemaVersion { $root = if ($MigrationRoot) { Resolve-RepoPath $MigrationRoot } else { Join-Path $repo "apps\server\WhisperX.Atom.Api\Migrations" }; $file = Get-ChildItem -LiteralPath $root -Filter "*.sql" -File | Sort-Object Name | Select-Object -Last 1; if ($null -eq $file) { throw "SCHEMA_MIGRATION_NOT_FOUND" }; return [IO.Path]::GetFileNameWithoutExtension($file.Name) }
function Assert-ContainedPath([string]$Root, [string]$RelativePath) { if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) { throw "BACKUP_UNSAFE_PATH" }; $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar; $candidate = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath)); if (-not $candidate.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "BACKUP_PATH_ESCAPE" }; return $candidate }
function Expand-ZipArchiveSafe([string]$ArchivePath, [string]$DestinationRoot) {
    $rootFull = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $zip.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.FullName) -or $entry.FullName.IndexOf([char]0) -ge 0) { throw "BACKUP_ARCHIVE_ENTRY_INVALID" }
            $relative = $entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $candidate = [IO.Path]::GetFullPath((Join-Path $DestinationRoot $relative))
            $rootCanonical = $rootFull.TrimEnd([IO.Path]::DirectorySeparatorChar)
            if (-not $candidate.Equals($rootCanonical, [StringComparison]::OrdinalIgnoreCase) -and
                -not $candidate.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "BACKUP_ARCHIVE_PATH_ESCAPE" }
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Force -Path $candidate | Out-Null; continue }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $candidate) | Out-Null
            $input = $entry.Open(); $output = [IO.File]::Open($candidate, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $zip.Dispose() }
}
function Invoke-PsqlScalar([string]$Sql, [string]$DbUser, [string]$DbName) { if ($PostgresContainer) { & docker exec $PostgresContainer psql -U $DbUser -d $DbName -tAc $Sql } else { & docker compose -f $ComposeFile exec -T $PostgresService psql -U $DbUser -d $DbName -tAc $Sql } }
function Invoke-PgRestore([string]$DumpPath, [string]$DbUser, [string]$DbName) { if ($PostgresContainer) { $inside = "/tmp/whisperx-restore-" + [Guid]::NewGuid().ToString("N") + ".dump"; try { & docker cp $DumpPath "${PostgresContainer}:$inside"; if ($LASTEXITCODE -ne 0) { throw "PG_RESTORE_STAGE_FAILED" }; $output = & docker exec $PostgresContainer pg_restore --clean --if-exists --no-owner -U $DbUser -d $DbName $inside 2>&1; if ($LASTEXITCODE -ne 0) { throw ("PG_RESTORE_FAILED: " + ($output -join " ")) } } finally { & docker exec $PostgresContainer rm -f $inside 2>$null | Out-Null }; return }; $psi = [Diagnostics.ProcessStartInfo]::new(); $psi.FileName = "docker.exe"; $arguments = @("compose", "-f", $ComposeFile, "exec", "-T", $PostgresService, "pg_restore", "--clean", "--if-exists", "--no-owner", "-U", $DbUser, "-d", $DbName); $psi.Arguments = ($arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' '; $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardError = $true; $process = [Diagnostics.Process]::new(); $process.StartInfo = $psi; if (-not $process.Start()) { throw "PG_RESTORE_START_FAILED" }; $input = [IO.File]::OpenRead($DumpPath); try { $input.CopyTo($process.StandardInput.BaseStream) } finally { $input.Dispose(); $process.StandardInput.Close() }; $stderr = $process.StandardError.ReadToEnd(); $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw ("PG_RESTORE_FAILED: " + $stderr.Trim()) } }

$ComposeFile = Resolve-RepoPath $ComposeFile

$archive = [IO.Path]::GetFullPath($BackupArchive); if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "BACKUP_ARCHIVE_NOT_FOUND" }
$hashPath = if ([string]::IsNullOrWhiteSpace($ArchiveSha256Path)) { "$archive.sha256" } else { [IO.Path]::GetFullPath($ArchiveSha256Path) }
if (Test-Path -LiteralPath $hashPath -PathType Leaf) {
    try { $hashRecord = Get-Content -LiteralPath $hashPath -Raw | ConvertFrom-Json } catch { throw "BACKUP_ARCHIVE_HASH_INVALID" }
    if ([string]::IsNullOrWhiteSpace([string]$hashRecord.sha256)) { throw "BACKUP_ARCHIVE_HASH_INVALID" }
    $actualArchiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualArchiveHash -ne ([string]$hashRecord.sha256).ToLowerInvariant()) { throw "BACKUP_ARCHIVE_HASH_MISMATCH" }
} elseif ($RequireArchiveSha256) {
    throw "BACKUP_ARCHIVE_HASH_MISSING"
}
$target = [IO.Path]::GetFullPath($TargetRoot); if ($target -eq [IO.Path]::GetPathRoot($target) -or $target.Length -lt 8) { throw "RESTORE_UNSAFE_TARGET" }
$extractRoot = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-restore-" + [Guid]::NewGuid().ToString("N")); New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
try {
    Expand-ZipArchiveSafe $archive $extractRoot
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

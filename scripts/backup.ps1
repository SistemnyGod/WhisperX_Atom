[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [switch]$IncludeMedia,
    [string]$ComposeFile = "compose.dev.yml",
    [string]$PostgresService = "postgres",
    [string]$PostgresContainer = "",
    [string]$Database = "",
    [string]$User = "",
    [string]$RuntimeManifestPath = "artifacts\release\runtime-manifest.json",
    [string]$MigrationRoot = "",
    [string]$ArchiveSha256Path = ""
)

$ErrorActionPreference = "Stop"
# The source script lives under <repo>\scripts, whereas the Server Bundle
# intentionally places it directly beside compose files and .env.example.
# Resolve the bundle layout first and keep the source-tree parent fallback.
$repo = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $repo '.env.example') -PathType Leaf)) {
    $repo = Split-Path -Parent $PSScriptRoot
}
Set-Location $repo

function Get-EnvValue([string]$Name, [string]$Default) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { return $Default }
    return $value
}
function Resolve-RepoPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $Path))
}
function Get-SchemaVersion {
    $root = if ($MigrationRoot) { Resolve-RepoPath $MigrationRoot } else { Join-Path $repo "apps\server\WhisperX.Atom.Api\Migrations" }
    $migration = Get-ChildItem -LiteralPath $root -Filter "*.sql" -File |
        Sort-Object Name | Select-Object -Last 1
    if ($null -eq $migration) { throw "SCHEMA_MIGRATION_NOT_FOUND" }
    return [IO.Path]::GetFileNameWithoutExtension($migration.Name)
}
function Invoke-PgDump([string]$Destination, [string]$DbUser, [string]$DbName) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "docker.exe"
    $arguments = if ($PostgresContainer) { @("exec", "-i", $PostgresContainer, "pg_dump", "-Fc", "-U", $DbUser, "-d", $DbName) } else { @("compose", "-f", $ComposeFile, "exec", "-T", $PostgresService, "pg_dump", "-Fc", "-U", $DbUser, "-d", $DbName) }
    $psi.Arguments = ($arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' '
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $psi
    if (-not $process.Start()) { throw "PG_DUMP_START_FAILED" }
    $out = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $process.StandardOutput.BaseStream.CopyTo($out) } finally { $out.Dispose() }
    $stderr = $process.StandardError.ReadToEnd(); $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "PG_DUMP_FAILED" }
}

$ComposeFile = Resolve-RepoPath $ComposeFile
$RuntimeManifestPath = Resolve-RepoPath $RuntimeManifestPath
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path (Get-EnvValue "WHISPERX_BACKUP_HOST" "C:\WhisperXAtom\Backups") $stamp }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stage = Join-Path $OutputDirectory "staging"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

try {
    $postgresDb = if ($Database) { $Database } else { Get-EnvValue "POSTGRES_DB" "whisperx_atom" }
    $postgresUser = if ($User) { $User } else { Get-EnvValue "POSTGRES_USER" "whisperx" }
    $dumpPath = Join-Path $stage "postgres.dump"
    Invoke-PgDump $dumpPath $postgresUser $postgresDb

    Copy-Item -LiteralPath $ComposeFile -Destination (Join-Path $stage "compose.backup.yml")
    Copy-Item -LiteralPath ".env.example" -Destination (Join-Path $stage ".env.example")
    $safeConfig = [ordered]@{
        runtimeProfile = Get-EnvValue "ASPNETCORE_ENVIRONMENT" "Development"
        retentionPolicy = [ordered]@{
            transportGraceHours = Get-EnvValue "WHISPERX_RETENTION_TRANSPORT_GRACE_HOURS" "24"
            rawGraceHours = Get-EnvValue "WHISPERX_RETENTION_RAW_GRACE_HOURS" "24"
            localMasterDays = Get-EnvValue "WHISPERX_RETENTION_LOCAL_MASTER_DAYS" "0"
            serverArchiveDays = Get-EnvValue "WHISPERX_RETENTION_SERVER_ARCHIVE_DAYS" "0"
        }
        agentIdentity = "stored in PostgreSQL as hashed enrollment identity; plaintext tokens are excluded"
        excluded = @("POSTGRES_PASSWORD", "JWT_SECRET", "TUS_HOOK_SECRET", "AGENT tokens", "cookies", "HF_TOKEN", "VOICE_HOST_TOKEN")
    }
    $safeConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage "runtime-config.json") -Encoding utf8
    $runtimeManifest = $RuntimeManifestPath
    if (Test-Path -LiteralPath $runtimeManifest -PathType Leaf) { Copy-Item -LiteralPath $runtimeManifest -Destination (Join-Path $stage "runtime-manifest.json") }

    $dataRoot = Get-EnvValue "WHISPERX_DATA_HOST" "C:\WhisperXAtom\Data"
    $archiveRoot = Get-EnvValue "WHISPERX_ARCHIVE_HOST" "C:\WhisperXAtom\Archive"
    $audioPolicy = if ($IncludeMedia) { "archive-included" } else { "metadata-only" }
    if ($IncludeMedia) {
        if (Test-Path -LiteralPath $dataRoot) { Copy-Item -LiteralPath $dataRoot -Destination (Join-Path $stage "Data") -Recurse }
        if (Test-Path -LiteralPath $archiveRoot) { Copy-Item -LiteralPath $archiveRoot -Destination (Join-Path $stage "Archive") -Recurse }
    }
    $files = Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($stage.Length + 1); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }
    $manifest = [ordered]@{
        createdAt = [DateTimeOffset]::UtcNow.ToString("o")
        release = Get-EnvValue "WHISPERX_RELEASE_VERSION" "unknown"
        gitCommit = (& git -C $repo rev-parse HEAD 2>$null)
        database = $postgresDb
        schemaVersion = Get-SchemaVersion
        audioPolicy = $audioPolicy
        secretsIncluded = $false
        files = @($files)
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage "backup-manifest.json") -Encoding utf8
    $archive = Join-Path $OutputDirectory "whisperx-atom-backup-$stamp.zip"
    # Windows PowerShell's Compress-Archive is limited by the classic 2 GB
    # ZIP path.  Prefer the inbox bsdtar Zip64 writer for media-inclusive
    # backups, while retaining a compatibility fallback on older hosts.
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($tar) {
        & $tar.Source -a -c -f $archive -C $stage .
        if ($LASTEXITCODE -ne 0) { throw "BACKUP_ARCHIVE_CREATE_FAILED" }
    } else {
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -CompressionLevel Optimal -Force
    }
    if (-not (Test-Path -LiteralPath $archive)) { throw "BACKUP_ARCHIVE_NOT_CREATED" }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashPath = if ([string]::IsNullOrWhiteSpace($ArchiveSha256Path)) { "$archive.sha256" } else { [IO.Path]::GetFullPath($ArchiveSha256Path) }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $hashPath) | Out-Null
    $hashRecord = [ordered]@{
        algorithm = "SHA256"
        path = [IO.Path]::GetFileName($archive)
        sha256 = $archiveHash
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    }
    $hashRecord | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $hashPath -Encoding utf8
    Write-Output $archive
    Write-Output $hashPath
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupArchive,
    [string]$TargetRoot = "C:\WhisperXAtom\Restore",
    [switch]$Apply,
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

function Assert-ContainedPath([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Backup manifest contains an unsafe path: $RelativePath"
    }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if (-not $candidate.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup manifest escapes extraction root: $RelativePath"
    }
    return $candidate
}

function Invoke-PgRestore([string]$DumpPath, [string]$User, [string]$Database) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "docker.exe"
    foreach ($argument in @("compose", "-f", "compose.dev.yml", "exec", "-T", "postgres", "pg_restore", "--clean", "--if-exists", "--no-owner", "-U", $User, "-d", $Database)) {
        $psi.ArgumentList.Add($argument)
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "failed to start docker for pg_restore" }
    $input = [IO.File]::OpenRead($DumpPath)
    try { $input.CopyTo($process.StandardInput.BaseStream) }
    finally { $input.Dispose(); $process.StandardInput.Close() }
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "pg_restore failed: $stderr" }
}

$archive = [IO.Path]::GetFullPath($BackupArchive)
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Backup archive not found: $archive" }
$target = [IO.Path]::GetFullPath($TargetRoot)
if ($target -eq [IO.Path]::GetPathRoot($target) -or $target.Length -lt 8) { throw "Refusing unsafe restore target: $target" }
$extractRoot = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-restore-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extractRoot)
    $manifestPath = Join-Path $extractRoot "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Backup manifest is missing" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (-not $manifest.files) { throw "Backup manifest has no file entries" }

    foreach ($entry in $manifest.files) {
        $file = Assert-ContainedPath $extractRoot ([string]$entry.path)
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Backup file is missing: $($entry.path)" }
        $info = Get-Item -LiteralPath $file
        if ([long]$entry.bytes -ne $info.Length) { throw "Backup size mismatch: $($entry.path)" }
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Backup hash mismatch: $($entry.path)" }
    }
    $dump = Join-Path $extractRoot "postgres.dump"
    if (-not (Test-Path -LiteralPath $dump -PathType Leaf)) { throw "PostgreSQL dump is missing" }

    if (-not $Apply) {
        Write-Host "Backup verified successfully. No data was changed." -ForegroundColor Green
        return
    }
    if (-not $PSCmdlet.ShouldProcess($target, "restore PostgreSQL and media backup")) { return }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $postgresDb = Get-EnvValue "POSTGRES_DB" "whisperx_atom"
    $postgresUser = Get-EnvValue "POSTGRES_USER" "whisperx"
    Invoke-PgRestore $dump $postgresUser $postgresDb

    if ($IncludeMedia -and [bool]$manifest.includes_media) {
        foreach ($mediaName in @("Data", "Archive")) {
            $source = Join-Path $extractRoot $mediaName
            if (Test-Path -LiteralPath $source -PathType Container) {
                $destination = Join-Path $target $mediaName
                New-Item -ItemType Directory -Force -Path $destination | Out-Null
                Copy-Item -Path (Join-Path $source "*") -Destination $destination -Recurse -Force
            }
        }
    }
    Write-Host "Backup restored to $target. Database was restored through Compose postgres." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force }
}

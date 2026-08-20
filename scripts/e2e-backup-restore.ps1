[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Verify', 'Apply')][string]$Mode = 'Plan',
    [string]$BackupArchive = '',
    [string]$OutputRoot = 'artifacts\acceptance\backup-restore',
    [string]$TestRoot = '',
    [string]$PostgresContainer = '',
    [string]$ComposeFile = 'compose.dev.yml',
    [switch]$IncludeMedia,
    [switch]$AllowTestRestore,
    [switch]$RequireArchiveSha256
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$restoreScript = Join-Path $PSScriptRoot 'restore.ps1'
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Write-Evidence($Evidence) {
    $path = Join-Path $output ("backup-restore-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $Evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
    $Evidence | ConvertTo-Json -Depth 12
}

function Assert-IsolatedTestRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'RESTORE_TEST_ROOT_REQUIRED' }
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($resolved.Length -lt 12 -or $resolved -eq [IO.Path]::GetPathRoot($resolved)) { throw 'RESTORE_TEST_ROOT_NOT_ISOLATED' }
    $blocked = @(
        [IO.Path]::GetFullPath((Join-Path $repo '')),
        [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'WhisperXAtom')),
        'C:\WhisperXAtom',
        'C:\ProgramData\WhisperXAtom'
    )
    foreach ($root in $blocked) {
        $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($resolved.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'RESTORE_TEST_ROOT_NOT_ISOLATED'
        }
    }
    return $resolved
}

if ($Mode -eq 'Plan') {
    Write-Evidence ([ordered]@{
        status = 'PLAN_ONLY'
        backupVerified = $false
        cleanRestore = $false
        contentChecksPassed = $false
        destructive = $false
        productionVolumesTouched = $false
        restoreCommand = 'restore.ps1 -Apply -Force -RequireArchiveSha256'
        requirements = @('BackupArchive', 'TestRoot outside production roots', 'AllowTestRestore', 'test-only PostgreSQL container named whisperx-atom-drill-*')
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    })
    exit 0
}

if ([string]::IsNullOrWhiteSpace($BackupArchive)) { throw 'BACKUP_ARCHIVE_REQUIRED' }
if (-not (Test-Path -LiteralPath $BackupArchive -PathType Leaf)) { throw 'BACKUP_ARCHIVE_NOT_FOUND' }
$archive = [IO.Path]::GetFullPath($BackupArchive)
$hashRequired = $RequireArchiveSha256 -or $Mode -eq 'Apply'

if ($Mode -eq 'Verify') {
    $verification = @(& $restoreScript -BackupArchive $archive -ComposeFile $ComposeFile -RequireArchiveSha256:$hashRequired)
    if ($LASTEXITCODE -ne 0) { throw 'BACKUP_VERIFY_FAILED' }
    $verified = $verification | Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
    $payload = if ($verified) { $verified | ConvertFrom-Json } else { $null }
    Write-Evidence ([ordered]@{
        status = 'VERIFIED'
        backupVerified = ($null -ne $payload -and $payload.verified -eq $true)
        cleanRestore = $false
        contentChecksPassed = $false
        destructive = $false
        productionVolumesTouched = $false
        archive = $archive
        restoreVerification = $payload
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    })
    exit 0
}

if (-not $AllowTestRestore) { throw 'RESTORE_EXPLICIT_CONFIRMATION_REQUIRED' }
$isolatedRoot = Assert-IsolatedTestRoot $TestRoot
if ([string]::IsNullOrWhiteSpace($PostgresContainer) -or $PostgresContainer -notmatch '^whisperx-atom-drill-[a-z0-9][a-z0-9-]*$') {
    throw 'RESTORE_TEST_CONTAINER_REQUIRED'
}
$restoreTarget = Join-Path $isolatedRoot 'restored-data'
New-Item -ItemType Directory -Force -Path $isolatedRoot | Out-Null
$restoreArgs = @('-BackupArchive', $archive, '-TargetRoot', $restoreTarget, '-PostgresContainer', $PostgresContainer, '-ComposeFile', $ComposeFile, '-Apply', '-Force', '-RequireArchiveSha256')
if ($IncludeMedia) { $restoreArgs += '-IncludeMedia' }
& $restoreScript @restoreArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'BACKUP_RESTORE_APPLY_FAILED' }

Write-Evidence ([ordered]@{
    status = 'RESTORE_APPLIED_CONTENT_CHECK_REQUIRED'
    backupVerified = $true
    cleanRestore = $true
    contentChecksPassed = $false
    destructive = $true
    productionVolumesTouched = $false
    archive = $archive
    testRoot = $isolatedRoot
    postgresContainer = $PostgresContainer
    note = 'Database restore completed only against the explicitly named drill container. Verify meetings, V1/V2, summaries and audio playback separately.'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
})

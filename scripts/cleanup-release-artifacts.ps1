[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..'),
    [string]$KeepIdentity,
    [switch]$IncludeArchives,
    [switch]$IncludeNodeModules,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($RepoRoot)
if (-not (Test-Path -LiteralPath $repo -PathType Container)) { throw "REPO_ROOT_MISSING: $repo" }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))

function Assert-Under([string]$path, [string]$root) {
    $resolvedPath = [IO.Path]::GetFullPath($path).TrimEnd('\') + '\'
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "CLEANUP_PATH_OUTSIDE_SCOPE: $path"
    }
}

function Get-SafeSizeBytes([string]$path) {
    try {
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($item.PSIsContainer) {
            $sum = Get-ChildItem -LiteralPath $path -File -Recurse -Force -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum
            if ($null -eq $sum.Sum) { return [int64]0 }
            return [int64]$sum.Sum
        }
        return [int64]$item.Length
    } catch {
        return $null
    }
}

$protectedNames = @(
    'docker-images.tar', 'release-manifest.json', 'model-manifest.json',
    'WhisperXAtom-Setup.exe'
)
$candidates = [System.Collections.Generic.List[object]]::new()

function Add-CleanupCandidate([string]$Path, [string]$Reason) {
    Assert-Under $Path $repo
    # The root release directory contains backups, inventory and the offline
    # package cache. It is never a generic build-output candidate.
    if ([string]::Equals([IO.Path]::GetFullPath($Path), $artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "CLEANUP_ROOT_ARTIFACTS_FORBIDDEN: $Path"
    }
    $candidates.Add([pscustomobject]@{ Path=$Path; Reason=$Reason })
}

function Get-TopLevelCandidates {
    $unique = @($candidates | Sort-Object Path -Unique)
    return @($unique | Where-Object {
        $candidatePath = [IO.Path]::GetFullPath($_.Path).TrimEnd('\\') + '\\'
        -not @($unique | Where-Object {
            $parentPath = [IO.Path]::GetFullPath($_.Path).TrimEnd('\\') + '\\'
            $parentPath.Length -lt $candidatePath.Length -and
                $candidatePath.StartsWith($parentPath, [StringComparison]::OrdinalIgnoreCase)
        }).Count
    })
}

# Build/test outputs are ignored and scoped to known source roots only.
foreach ($root in @('apps','tests')) {
    $rootPath = Join-Path $repo $root
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $rootPath -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('bin','obj') } |
        ForEach-Object {
            Add-CleanupCandidate $_.FullName 'local build output'
        }
}

# Only nested staging artifacts are disposable. The repository-level
# artifacts directory is intentionally excluded above.
foreach ($root in @('apps','scripts','tests')) {
    $rootPath = Join-Path $repo $root
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $rootPath -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('artifacts','TestResults','__pycache__') } |
        ForEach-Object { Add-CleanupCandidate $_.FullName 'nested build or test output' }
}

if ($IncludeNodeModules) {
    $webRoot = Join-Path $repo 'apps\web'
    $webLock = @('package-lock.json','npm-shrinkwrap.json','pnpm-lock.yaml','yarn.lock') |
        ForEach-Object { Join-Path $webRoot $_ } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    $webModules = Join-Path $webRoot 'node_modules'
    if ($null -eq $webLock) { throw 'CLEANUP_WEB_LOCKFILE_REQUIRED' }
    if (Test-Path -LiteralPath $webModules -PathType Container) {
        Add-CleanupCandidate $webModules 'web dependencies rebuildable from lockfile'
    }
}

# Test scratch directories are explicitly named and never selected from a
# broad workspace root. Permission failures are reported, not force-fixed.
Get-ChildItem -LiteralPath $repo -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(\.pytest|\.media-test|\.codex-test|\.review-test|\.technical-test|tmp)' } |
    ForEach-Object {
        Add-CleanupCandidate $_.FullName 'test scratch directory'
    }

# Old server archives are removable only when the caller explicitly opts in
# and names a rollback identity to keep. The active bundle directory is never
# selected by this script.
$archiveRoot = Join-Path $artifactRoot ''
if ($IncludeArchives -and -not [string]::IsNullOrWhiteSpace($KeepIdentity) -and (Test-Path -LiteralPath $archiveRoot -PathType Container)) {
    Get-ChildItem -LiteralPath $archiveRoot -File -Filter 'WhisperXAtom-Server-*.zip' -Force -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ([string]::IsNullOrWhiteSpace($KeepIdentity) -or $_.Name -notmatch [regex]::Escape($KeepIdentity)) {
                Add-CleanupCandidate $_.FullName 'superseded server archive'
            }
        }
    Get-ChildItem -LiteralPath $archiveRoot -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^server-bundle-[0-9a-fA-F]+$' } |
        ForEach-Object {
            if ($_.Name -notmatch [regex]::Escape($KeepIdentity)) {
                Add-CleanupCandidate $_.FullName 'superseded extracted server bundle'
            }
        }
}
$archivesSkipped = -not $IncludeArchives -or [string]::IsNullOrWhiteSpace($KeepIdentity)
$topLevelCandidates = Get-TopLevelCandidates

$report = [ordered]@{
    mode = if ($Apply) { 'apply' } else { 'preview' }
    repoRoot = $repo
    keepIdentity = $KeepIdentity
    includeArchives = [bool]$IncludeArchives
    includeNodeModules = [bool]$IncludeNodeModules
    archivesSkipped = $archivesSkipped
    protectedRoots = @('C:\WhisperXAtom','Docker volumes','Patrol360',$artifactRoot,'.env','.env.lan','vendor','user recordings')
    candidates = @($topLevelCandidates | ForEach-Object {
        [ordered]@{ path=$_.Path; reason=$_.Reason; sizeBytes=(Get-SafeSizeBytes $_.Path) }
    })
}
$report | ConvertTo-Json -Depth 8

if (-not $Apply) { exit 0 }
foreach ($item in @($topLevelCandidates)) {
    Assert-Under $item.Path $repo
    if ($protectedNames -contains ([IO.Path]::GetFileName($item.Path))) { throw "CLEANUP_PROTECTED_ARTIFACT: $($item.Path)" }
    if ($PSCmdlet.ShouldProcess($item.Path, "Remove $($item.Reason)")) {
        try {
            Remove-Item -LiteralPath $item.Path -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Warning "CLEANUP_SKIPPED path=$($item.Path) reason=$($_.Exception.GetType().Name)"
        }
    }
}

[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..'),
    [string]$KeepIdentity,
    [switch]$IncludeArchives,
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

# Build/test outputs are ignored and scoped to known source roots only.
foreach ($root in @('apps','tests')) {
    $rootPath = Join-Path $repo $root
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $rootPath -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('bin','obj') } |
        ForEach-Object {
            Assert-Under $_.FullName $repo
            $candidates.Add([pscustomobject]@{ Path=$_.FullName; Reason='local build output' })
        }
}

# Test scratch directories are explicitly named and never selected from a
# broad workspace root. Permission failures are reported, not force-fixed.
Get-ChildItem -LiteralPath $repo -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(\.pytest|\.media-test|\.codex-test|\.review-test|\.technical-test|tmp)' } |
    ForEach-Object {
        Assert-Under $_.FullName $repo
        $candidates.Add([pscustomobject]@{ Path=$_.FullName; Reason='test scratch directory' })
    }

# Old server archives are removable only when the caller explicitly opts in
# and names a rollback identity to keep. The active bundle directory is never
# selected by this script.
$archiveRoot = Join-Path $artifactRoot ''
if ($IncludeArchives -and -not [string]::IsNullOrWhiteSpace($KeepIdentity) -and (Test-Path -LiteralPath $archiveRoot -PathType Container)) {
    Get-ChildItem -LiteralPath $archiveRoot -File -Filter 'WhisperXAtom-Server-*.zip' -Force -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ([string]::IsNullOrWhiteSpace($KeepIdentity) -or $_.Name -notmatch [regex]::Escape($KeepIdentity)) {
                Assert-Under $_.FullName $artifactRoot
                $candidates.Add([pscustomobject]@{ Path=$_.FullName; Reason='superseded server archive' })
            }
        }
}
$archivesSkipped = -not $IncludeArchives -or [string]::IsNullOrWhiteSpace($KeepIdentity)

$report = [ordered]@{
    mode = if ($Apply) { 'apply' } else { 'preview' }
    repoRoot = $repo
    keepIdentity = $KeepIdentity
    includeArchives = [bool]$IncludeArchives
    archivesSkipped = $archivesSkipped
    protectedRoots = @('C:\WhisperXAtom','Docker volumes','Patrol360')
    candidates = @($candidates | Sort-Object Path -Unique | ForEach-Object {
        [ordered]@{ path=$_.Path; reason=$_.Reason; sizeBytes=(Get-SafeSizeBytes $_.Path) }
    })
}
$report | ConvertTo-Json -Depth 8

if (-not $Apply) { exit 0 }
foreach ($item in @($candidates | Sort-Object Path -Unique)) {
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

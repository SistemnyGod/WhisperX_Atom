[CmdletBinding()]
param(
    [string]$Destination = "artifacts/nuget-packages",
    [string]$Source = "",
    [switch]$AllCached
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$destinationPath = if ([IO.Path]::IsPathRooted($Destination)) {
    [IO.Path]::GetFullPath($Destination)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $Destination))
}
if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}
$sourcePath = [IO.Path]::GetFullPath($Source)
if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) { throw "NUGET_SEED_SOURCE_MISSING: $sourcePath" }
New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null

if ($AllCached) {
    # Intended for an offline workstation whose trusted user cache was
    # populated by earlier successful restores.  Copy package-id directories
    # individually so the destination can be resumed safely after interruption.
    $copiedRoots = 0
    foreach ($packageRoot in @(Get-ChildItem -LiteralPath $sourcePath -Directory)) {
        $targetRoot = Join-Path $destinationPath $packageRoot.Name
        if (Test-Path -LiteralPath $targetRoot -PathType Container) {
            foreach ($versionEntry in @(Get-ChildItem -LiteralPath $packageRoot.FullName -Force)) {
                Copy-Item -LiteralPath $versionEntry.FullName -Destination $targetRoot -Recurse -Force
            }
        } else {
            Copy-Item -LiteralPath $packageRoot.FullName -Destination $targetRoot -Recurse
        }
        $copiedRoots++
    }
    Write-Host "NUGET_CACHE_READY=$destinationPath"
    Write-Host "NUGET_CACHE_ROOTS=$copiedRoots"
    exit 0
}

$assetsFiles = @(Get-ChildItem -LiteralPath (Join-Path $repo 'apps'), (Join-Path $repo 'tests') -Recurse -Filter 'project.assets.json' -File -ErrorAction SilentlyContinue)
if ($assetsFiles.Count -eq 0) { throw "NUGET_ASSETS_MISSING: run one trusted restore before creating the offline cache" }
$packages = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($assets in $assetsFiles) {
    $json = Get-Content -LiteralPath $assets.FullName -Raw | ConvertFrom-Json
    foreach ($library in $json.libraries.PSObject.Properties) {
        if ([string]$library.Value.type -ne 'package') { continue }
        [void]$packages.Add([string]$library.Name)
    }
}
# A failed/offline restore can rewrite project.assets.json before reporting the
# missing root package.  Always merge exact PackageReference roots from the
# projects so a partially written assets file cannot poison the seed cache.
$projectFiles = @(Get-ChildItem -LiteralPath (Join-Path $repo 'apps'), (Join-Path $repo 'tests') -Recurse -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
foreach ($project in $projectFiles) {
    [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($reference in @($xml.Project.ItemGroup.PackageReference)) {
        $id = [string]$reference.Include
        $version = [string]$reference.Version
        if (-not [string]::IsNullOrWhiteSpace($id) -and -not [string]::IsNullOrWhiteSpace($version) -and $version -notmatch '[\[\(,]') {
            [void]$packages.Add("$id/$version")
        }
    }
}

$copied = 0
$missing = [System.Collections.Generic.List[string]]::new()
foreach ($package in @($packages | Sort-Object)) {
    $parts = $package.Split('/', 2)
    if ($parts.Count -ne 2) { $missing.Add($package); continue }
    $id = $parts[0].ToLowerInvariant()
    $version = $parts[1].ToLowerInvariant()
    $sourcePackage = Join-Path (Join-Path $sourcePath $id) $version
    $destinationPackage = Join-Path (Join-Path $destinationPath $id) $version
    if (Test-Path -LiteralPath $destinationPackage -PathType Container) { continue }
    if (-not (Test-Path -LiteralPath $sourcePackage -PathType Container)) { $missing.Add($package); continue }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPackage) | Out-Null
    Copy-Item -LiteralPath $sourcePackage -Destination $destinationPackage -Recurse
    $copied++
}
if ($missing.Count -gt 0) { throw "NUGET_SEED_PACKAGES_MISSING: $($missing -join ', ')" }
Write-Host "NUGET_CACHE_READY=$destinationPath"
Write-Host "NUGET_CACHE_PACKAGES=$($packages.Count)"
Write-Host "NUGET_CACHE_COPIED=$copied"

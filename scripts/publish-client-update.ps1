[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$InstallerPath,
    [Parameter(Mandatory = $true)] [string]$ReleaseManifestPath,
    [string]$UpdatesRoot = 'C:\ProgramData\WhisperXAtom\Server\Updates',
    [ValidateSet('stable', 'pilot')] [string]$Channel = 'stable',
    [switch]$Mandatory,
    [string[]]$ReleaseNotes = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail([string]$Message) { throw "CLIENT_UPDATE_PUBLISH_FAILED: $Message" }

$installer = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path
$releaseManifest = (Resolve-Path -LiteralPath $ReleaseManifestPath -ErrorAction Stop).Path
$manifest = Get-Content -LiteralPath $releaseManifest -Raw | ConvertFrom-Json

$version = [string]$manifest.version
$buildIdentity = [string]$manifest.buildIdentity
$commit = [string]$manifest.commit
if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($buildIdentity)) { Fail 'version и buildIdentity обязательны' }
if ($buildIdentity -match '(?i)dev|dirty' -or $buildIdentity -notmatch '\+[0-9a-f]{40}$') { Fail "buildIdentity недопустим: $buildIdentity" }
if ($commit -notmatch '^[0-9a-fA-F]{40}$') { Fail 'commit должен быть полным SHA-1' }
if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+].*)?$') { Fail "недопустимая semantic version: $version" }

$sha = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $installer).Length
$signature = Get-AuthenticodeSignature -FilePath $installer
if ($Channel -eq 'stable' -and $signature.Status -ne 'Valid') { Fail "stable требует валидную Authenticode-подпись, статус: $($signature.Status)" }
if ($Channel -eq 'pilot' -and $signature.Status -notin @('Valid', 'NotSigned')) { Fail "pilot-пакет имеет повреждённую подпись: $($signature.Status)" }

$root = [IO.Path]::GetFullPath($UpdatesRoot)
$packages = Join-Path $root 'packages'
New-Item -ItemType Directory -Force -Path $packages | Out-Null
$part = Join-Path $packages "$sha.exe.part"
$target = Join-Path $packages "$sha.exe"
Copy-Item -LiteralPath $installer -Destination $part -Force
if ((Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash -ne $sha) { Remove-Item -LiteralPath $part -Force; Fail 'checksum изменился во время публикации' }
Move-Item -LiteralPath $part -Destination $target -Force

$publishedAt = [DateTimeOffset]::UtcNow
$releaseNotesValue = if ($ReleaseNotes.Count -gt 0) { @($ReleaseNotes) } elseif ($manifest.releaseNotes) { @($manifest.releaseNotes) } else { @() }
$published = [ordered]@{
    schemaVersion = 1
    product = 'WhisperX Atom'
    channel = $Channel
    version = $version
    buildIdentity = $buildIdentity
    commit = $commit.ToLowerInvariant()
    publishedAtUtc = $publishedAt.ToString('O')
    mandatory = [bool]$Mandatory
    apiVersion = 1
    minServerVersion = [string]($manifest.minServerVersion ?? '1.0.1')
    releaseNotes = $releaseNotesValue
    package = [ordered]@{
        packageId = $sha
        fileName = [IO.Path]::GetFileName($installer)
        sizeBytes = $size
        sha256 = $sha
        authenticodeRequired = ($Channel -eq 'stable')
        downloadUrl = "/api/client-updates/packages/$sha"
    }
    signatureStatus = [string]$signature.Status
}

$manifestPart = Join-Path $root 'client-update.json.part'
$manifestPath = Join-Path $root 'client-update.json'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$json = $published | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPart, $json, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $manifestPart -Destination $manifestPath -Force

Write-Host "Опубликовано: $version ($buildIdentity) [$Channel]" -ForegroundColor Green
Write-Host "Manifest: $manifestPath"
Write-Host "Package:  $target"

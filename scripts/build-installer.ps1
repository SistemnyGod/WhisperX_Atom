param(
    [switch]$SkipPublish,
    [switch]$NoRestore,
    [switch]$RequireVoiceRefinerAssets,
    [string]$ServerOrigin = "http://192.168.2.194:8080",
    [string]$TtsWheelhouse = '',
    [string]$ServerBundleRoot = 'artifacts/server-bundle',
    [string]$ServerArchive = 'artifacts/WhisperXAtom-Server.zip'
)
$ErrorActionPreference = "Stop"
$originUri = $null
$validOrigin = (
    [Uri]::TryCreate($ServerOrigin.TrimEnd('/'), [UriKind]::Absolute, [ref]$originUri) -and
    $originUri.Scheme -in @("http", "https") -and
    -not ($originUri.IsLoopback -and $originUri.Port -eq 0)
)
if (-not $validOrigin) { throw "INSTALLER_SERVER_ORIGIN_INVALID: $ServerOrigin" }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$assetVerifier = Join-Path $PSScriptRoot "verify-voice-refiner-assets.ps1"
if ($RequireVoiceRefinerAssets) {
    & $assetVerifier
    if ($LASTEXITCODE -ne 0) { throw "VOICE_REFINER_ASSET_GATE_FAILED" }
}
$publish = Join-Path $repoRoot "scripts\publish-desktop.ps1"
$iss = Join-Path $repoRoot "apps\desktop\Installer\WhisperXAtom.iss"
$artifact = Join-Path $repoRoot "artifacts\desktop\Desktop\WhisperX.Atom.Desktop.exe"
if (-not $SkipPublish -or -not (Test-Path -LiteralPath $artifact)) {
    # Invoke named parameters explicitly.  Passing an argument array through
    # the call operator is not reliable across Windows PowerShell versions:
    # it may bind "-TtsWheelhouse" as OutputRoot, leaving the real desktop
    # payload stale while the installer is being created.
    if ($TtsWheelhouse) {
        if ($NoRestore) {
            & $publish -NoRestore -TtsWheelhouse $TtsWheelhouse -RequireVoiceRefinerAssets:$RequireVoiceRefinerAssets
        } else {
            & $publish -TtsWheelhouse $TtsWheelhouse -RequireVoiceRefinerAssets:$RequireVoiceRefinerAssets
        }
    } elseif ($NoRestore) {
        & $publish -NoRestore -RequireVoiceRefinerAssets:$RequireVoiceRefinerAssets
    } else {
        & $publish -RequireVoiceRefinerAssets:$RequireVoiceRefinerAssets
    }
}
$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if ($null -eq $iscc) {
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $candidates = @(
        (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    $candidates = @($candidates)
    if ($candidates.Count -gt 0) { $iscc = Get-Item -LiteralPath $candidates[0] }
}
if ($null -eq $iscc) {
    throw "Inno Setup 6 (ISCC.exe) is not installed. Install it, then rerun scripts\\build-installer.ps1. The published Desktop package is available under artifacts\\desktop."
}
$isccPath = if ($iscc -is [System.IO.FileInfo]) { $iscc.FullName } elseif ($iscc.Source) { $iscc.Source } else { $iscc.Path }
$setup = Join-Path $repoRoot "artifacts\\installer\\WhisperXAtom-Setup.exe"
$previousSetup = $null
if (Test-Path -LiteralPath $setup -PathType Leaf) {
    $previousSetup = "$setup.previous.$([Guid]::NewGuid().ToString('N'))"
    Move-Item -LiteralPath $setup -Destination $previousSetup
}
try {
    & $isccPath "/DServerOrigin=$($originUri.ToString().TrimEnd('/'))" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
} catch {
    if (-not (Test-Path -LiteralPath $setup) -and $previousSetup -and (Test-Path -LiteralPath $previousSetup)) {
        Move-Item -LiteralPath $previousSetup -Destination $setup
    }
    throw
}
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "INSTALLER_ARTIFACT_MISSING" }
if ($previousSetup -and (Test-Path -LiteralPath $previousSetup)) { Remove-Item -LiteralPath $previousSetup -Force }
$identityPath = Join-Path $repoRoot "artifacts\\desktop\\build-identity.json"
if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) { throw "DESKTOP_IDENTITY_MANIFEST_MISSING" }
$identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
$buildIdentity = [string]$identity.buildIdentity
if ([string]::IsNullOrWhiteSpace($buildIdentity) -or $buildIdentity -match '(?i)dev|dirty' -or $buildIdentity -notmatch '\+[0-9a-fA-F]{40}$' -or [bool]$identity.dirty) {
    throw "INSTALLER_RELEASE_IDENTITY_INVALID: $buildIdentity"
}
$runtimeGate = Join-Path $repoRoot "scripts\verify-clean-runtime.ps1"
if (-not (Test-Path -LiteralPath $runtimeGate -PathType Leaf)) { throw "CLEAN_RUNTIME_GATE_MISSING: $runtimeGate" }
& $runtimeGate -ArtifactsRoot (Join-Path $repoRoot "artifacts\desktop") -OutputPath (Join-Path $repoRoot "artifacts\acceptance\clean-runtime\runtime-identity.json")
if ($LASTEXITCODE -ne 0) { throw "CLEAN_RUNTIME_GATE_FAILED" }
$signature = Get-AuthenticodeSignature -LiteralPath $setup
$signatureStatus = [string]$signature.Status
$releaseStatus = if ($signatureStatus -eq "Valid") { "SIGNED_RELEASE_CANDIDATE" } else { "UNSIGNED_PILOT_BUILD" }
$releaseDir = Join-Path $repoRoot "artifacts\\release"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$serverArchivePath = if ([IO.Path]::IsPathRooted($ServerArchive)) { [IO.Path]::GetFullPath($ServerArchive) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $ServerArchive)) }
$serverBundlePath = if ([IO.Path]::IsPathRooted($ServerBundleRoot)) { [IO.Path]::GetFullPath($ServerBundleRoot) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $ServerBundleRoot)) }
$serverArchive = $serverArchivePath
$serverBundleManifestPath = Join-Path $serverBundlePath 'release-manifest.json'
$serverBundle = if (Test-Path -LiteralPath $serverBundleManifestPath -PathType Leaf) {
    $serverManifest = Get-Content -LiteralPath $serverBundleManifestPath -Raw | ConvertFrom-Json
    $serverIdentity = [string]$serverManifest.buildIdentity
    if (-not [string]::IsNullOrWhiteSpace($serverIdentity) -and $serverIdentity -ne $buildIdentity) {
        throw "INSTALLER_RUNTIME_IDENTITY_MISMATCH: desktop=$buildIdentity server=$serverIdentity"
    }
    [ordered]@{
        directory = $ServerBundleRoot
        archive = if (Test-Path -LiteralPath $serverArchive -PathType Leaf) { [ordered]@{ path = $ServerArchive; sha256 = (Get-FileHash -LiteralPath $serverArchive -Algorithm SHA256).Hash.ToLowerInvariant() } } else { $null }
        buildIdentity = [string]$serverManifest.buildIdentity
        dockerImages = $serverManifest.dockerImages
    }
} else { $null }
[ordered]@{
    schemaVersion = 1
    product = "WhisperX Atom"
    version = "1.0.1"
    buildIdentity = $buildIdentity
    commit = [string]$identity.commit
    status = $releaseStatus
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    installer = [ordered]@{ path = "artifacts/installer/WhisperXAtom-Setup.exe"; sha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant(); signature = $signatureStatus }
    desktopPackage = "artifacts/desktop"
    serverBundle = $serverBundle
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $releaseDir "release-manifest.json") -Encoding utf8
Write-Host "Installer created under artifacts\\installer ($releaseStatus)"

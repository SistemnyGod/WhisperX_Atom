param(
    [Parameter(Mandatory = $true)][string]$VoskZipPath,
    [string]$VendorRoot = (Join-Path $PSScriptRoot "..\vendor\voice-models")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$VendorRoot = [IO.Path]::GetFullPath($VendorRoot)
$VoskZipPath = [IO.Path]::GetFullPath($VoskZipPath)
if (-not $VendorRoot.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "VendorRoot must stay inside the repository." }
if (-not (Test-Path -LiteralPath $VoskZipPath -PathType Leaf)) { throw "Vosk ZIP not found: $VoskZipPath" }
if ([IO.Path]::GetFileName($VoskZipPath) -ne "vosk-model-small-ru-0.22.zip") { throw "Expected official file vosk-model-small-ru-0.22.zip" }

$archiveHash = (Get-FileHash -LiteralPath $VoskZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$existingLock = Join-Path $VendorRoot "voice-models.lock.json"
if (Test-Path -LiteralPath $existingLock) {
    $locked = Get-Content -LiteralPath $existingLock -Raw | ConvertFrom-Json
    if ($locked.archiveSha256 -ne $archiveHash) { throw "Vosk archive SHA-256 differs from the pinned lock. Expected $($locked.archiveSha256), actual $archiveHash" }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("whisperx-atom-vosk-" + [Guid]::NewGuid().ToString("N"))
$staging = Join-Path $temp "extract"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    $archive = [IO.Compression.ZipFile]::OpenRead($VoskZipPath)
    try {
        $stagingFull = [IO.Path]::GetFullPath($staging) + [IO.Path]::DirectorySeparatorChar
        foreach ($entry in $archive.Entries) {
            $destination = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
            if (-not $destination.StartsWith($stagingFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe ZIP entry: $($entry.FullName)" }
        }
    } finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($VoskZipPath, $staging)
    $model = Join-Path $staging "vosk-model-small-ru-0.22"
    foreach ($required in @("am", "conf", "graph")) { if (-not (Test-Path -LiteralPath (Join-Path $model $required) -PathType Container)) { throw "Invalid Vosk model: missing $required" } }

    $project = Join-Path $repoRoot "apps\voice-host\WhisperX.Atom.Voice.Host\WhisperX.Atom.Voice.Host.csproj"
    dotnet run --project $project -- --model-smoke $model
    if ($LASTEXITCODE -ne 0) { throw "Vosk native model smoke failed." }

    New-Item -ItemType Directory -Force -Path $VendorRoot | Out-Null
    $target = Join-Path $VendorRoot "vosk-model-small-ru-0.22"
    $backup = Join-Path $temp "previous-model"
    if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup }
    try { Move-Item -LiteralPath $model -Destination $target }
    catch { if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $target }; throw }

    $files = Get-ChildItem -LiteralPath $target -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($VendorRoot.Length + 1).Replace('\','/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $lock = [ordered]@{
        product = "WhisperX Atom Voice Commands"
        model = "vosk-model-small-ru-0.22"
        source = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip"
        archiveSha256 = $archiveHash
        files = @($files)
        offlineInstaller = $true
    }
    $lock | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $existingLock -Encoding UTF8
    Write-Host "Vosk model staged in $VendorRoot"
    Write-Host "Pinned archive SHA-256: $archiveHash"
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
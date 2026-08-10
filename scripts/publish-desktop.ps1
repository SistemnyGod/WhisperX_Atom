param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\artifacts\desktop")
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$output = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { [System.IO.Path]::GetFullPath($OutputRoot) } else { [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputRoot)) }
if ([string]::IsNullOrWhiteSpace($output) -or $output -eq $repoRoot -or $output.Length -lt ($repoRoot.Length + 8)) {
    throw "Refusing unsafe output path: $output"
}

$bundledModels = Join-Path $repoRoot "vendor\voice-models"
$bundledVosk = Join-Path $bundledModels "vosk-model-small-ru-0.22"
$bundledLock = Join-Path $bundledModels "voice-models.lock.json"
$responseSource = Join-Path $repoRoot "apps\voice-host\Assets\VoiceResponses"
if (-not (Test-Path -LiteralPath $bundledVosk -PathType Container)) { throw "Vosk model is not staged. Run scripts\prepare-voice-models.ps1 -VoskZipPath <official zip>." }
if (-not (Test-Path -LiteralPath $bundledLock -PathType Leaf)) { throw "Vosk lock file is missing: $bundledLock" }
if (-not (Test-Path -LiteralPath $responseSource -PathType Container) -or (Get-ChildItem -LiteralPath $responseSource -Filter *.wav -File).Count -lt 11) { throw "Offline WAV responses are missing. Run scripts\prepare-voice-responses.ps1." }
$lock = Get-Content -LiteralPath $bundledLock -Raw | ConvertFrom-Json
if ($lock.model -ne "vosk-model-small-ru-0.22" -or $lock.archiveSha256 -notmatch "^[a-f0-9]{64}$") { throw "Vosk lock metadata is invalid." }
$modelRoot = [IO.Path]::GetFullPath($bundledModels) + [IO.Path]::DirectorySeparatorChar
foreach ($entry in $lock.files) {
    $asset = [IO.Path]::GetFullPath((Join-Path $bundledModels ([string]$entry.path)))
    if (-not $asset.StartsWith($modelRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe model lock path: $($entry.path)" }
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Locked Vosk asset is missing: $($entry.path)" }
    $file = Get-Item -LiteralPath $asset
    if ($file.Length -ne [long]$entry.size) { throw "Locked Vosk asset size mismatch: $($entry.path)" }
    $actualHash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne [string]$entry.sha256) { throw "Locked Vosk asset hash mismatch: $($entry.path)" }
}
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

$desktopProject = Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\WhisperX.Atom.Desktop.csproj"
$serviceProject = Join-Path $repoRoot "apps\recorder-agent\WhisperX.Atom.Recorder.Service.csproj"
$voiceProject = Join-Path $repoRoot "apps\voice-host\WhisperX.Atom.Voice.Host\WhisperX.Atom.Voice.Host.csproj"
$desktopOut = Join-Path $output "Desktop"
$serviceOut = Join-Path $output "Service"
$voiceOut = Join-Path $output "VoiceHost"

dotnet publish $desktopProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -o $desktopOut
dotnet publish $serviceProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -o $serviceOut
dotnet publish $voiceProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -o $voiceOut

Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Install-Service.ps1") $output
Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Uninstall-Service.ps1") $output
Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Configure-VoiceUser.ps1") $output
$voiceModels = Join-Path $output "VoiceHost\Models\Voice"

New-Item -ItemType Directory -Force -Path $voiceModels | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "apps\voice-host\Models\Voice\voice-models.manifest.json") -Destination $voiceModels
Copy-Item -LiteralPath $bundledLock -Destination $voiceModels
Copy-Item -LiteralPath $bundledVosk -Destination $voiceModels -Recurse
$publishedResponses = Join-Path $voiceOut "Assets\VoiceResponses"
if (-not (Test-Path -LiteralPath $publishedResponses) -or (Get-ChildItem -LiteralPath $publishedResponses -Filter *.wav -File).Count -lt 11) { throw "Published VoiceHost does not contain all WAV responses." }
$voiceExe = Join-Path $voiceOut "WhisperX.Atom.Voice.Host.exe"
& $voiceExe --model-smoke $bundledVosk
if ($LASTEXITCODE -ne 0) { throw "Published VoiceHost cannot load Vosk/native runtime." }
Write-Host "Desktop package published to $output"
Write-Host "Next: compile apps\desktop\Installer\WhisperXAtom.iss with Inno Setup."

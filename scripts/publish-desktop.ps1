param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\artifacts\desktop")
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$output = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { [System.IO.Path]::GetFullPath($OutputRoot) } else { [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputRoot)) }
if ([string]::IsNullOrWhiteSpace($output) -or $output -eq $repoRoot -or $output.Length -lt ($repoRoot.Length + 8)) {
    throw "Refusing unsafe output path: $output"
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

$desktopProject = Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\WhisperX.Atom.Desktop.csproj"
$serviceProject = Join-Path $repoRoot "apps\recorder-agent\WhisperX.Atom.Recorder.Service.csproj"
$desktopOut = Join-Path $output "Desktop"
$serviceOut = Join-Path $output "Service"

dotnet publish $desktopProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $desktopOut
dotnet publish $serviceProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $serviceOut

Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Install-Service.ps1") $output
Copy-Item (Join-Path $repoRoot "apps\desktop\Installer\Uninstall-Service.ps1") $output
Write-Host "Desktop package published to $output"
Write-Host "Next: compile apps\desktop\Installer\WhisperXAtom.iss with Inno Setup."

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = $(if ($env:WHISPERX_DESKTOP_CONFIGURATION) { $env:WHISPERX_DESKTOP_CONFIGURATION } else { "Debug" })
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\WhisperX.Atom.Desktop.csproj"
$override = [Environment]::GetEnvironmentVariable("WHISPERX_DESKTOP_EXE")

$candidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($override)) {
    $candidates.Add([IO.Path]::GetFullPath($override))
}
$candidates.Add((Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\bin\$Configuration\net10.0-windows\WhisperX.Atom.Desktop.exe"))
if ($Configuration -ne "Debug") {
    $candidates.Add((Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\bin\Debug\net10.0-windows\WhisperX.Atom.Desktop.exe"))
}
$candidates.Add((Join-Path $repoRoot "artifacts\desktop\Desktop\WhisperX.Atom.Desktop.exe"))

$desktopExe = $candidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($desktopExe)) {
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Desktop project not found: $project"
    }

    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw "dotnet.exe was not found. Install the .NET 10 SDK or set WHISPERX_DESKTOP_EXE to a published Desktop executable."
    }

    Push-Location $repoRoot
    try {
        & $dotnet.Source build $project --configuration $Configuration --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Desktop build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    $desktopExe = Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\bin\$Configuration\net10.0-windows\WhisperX.Atom.Desktop.exe"
    if (-not (Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
        throw "Desktop executable was not created: $desktopExe"
    }
}

$desktopExe = (Resolve-Path -LiteralPath $desktopExe).Path
$workingDirectory = Split-Path -Parent $desktopExe
$process = Start-Process -FilePath $desktopExe -WorkingDirectory $workingDirectory -PassThru
Write-Host "WhisperX Atom Desktop started. PID=$($process.Id)"
Write-Host "Executable: $desktopExe"

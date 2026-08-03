param(
    [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publish = Join-Path $repoRoot "scripts\publish-desktop.ps1"
$iss = Join-Path $repoRoot "apps\desktop\Installer\WhisperXAtom.iss"
$artifact = Join-Path $repoRoot "artifacts\desktop\Desktop\WhisperX.Atom.Desktop.exe"
if (-not $SkipPublish -or -not (Test-Path -LiteralPath $artifact)) {
    & $publish
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
& $isccPath $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
Write-Host "Installer created under artifacts\\installer"

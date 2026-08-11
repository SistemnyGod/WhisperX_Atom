[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = $(if ($env:WHISPERX_DESKTOP_CONFIGURATION) { $env:WHISPERX_DESKTOP_CONFIGURATION } else { "Debug" }),
    [switch]$ForcePublish,
    [switch]$SkipRecorder
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
if (-not $SkipRecorder) {
    $recorderService = Get-Service -Name "WhisperXAtomRecorder" -ErrorAction SilentlyContinue
    if ($null -eq $recorderService) {
        try { & (Join-Path $PSScriptRoot "start-recorder-host.ps1") }
        catch { Write-Warning ("RECORDER_UNAVAILABLE: {0}. Desktop will still open for diagnostics." -f $_.Exception.Message) }
    } elseif ($recorderService.Status -ne "Running") {
        Start-Service -Name "WhisperXAtomRecorder"
    }
}
$project = Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop\WhisperX.Atom.Desktop.csproj"
$override = [Environment]::GetEnvironmentVariable("WHISPERX_DESKTOP_EXE")
$publishedDesktop = Join-Path $repoRoot "artifacts\desktop\Desktop"
$publishedExe = Join-Path $publishedDesktop "WhisperX.Atom.Desktop.exe"

function Get-RunningDesktopProcess {
    param([string]$ProcessName)

    $processes = @()
    try {
        $processes = @(Get-CimInstance Win32_Process -Filter "Name='$ProcessName'" -ErrorAction Stop |
            Select-Object ProcessId, ExecutablePath, CommandLine)
    }
    catch {
        # A non-elevated shell may not be allowed to query Win32_Process.
        # Fall back to the local process table; Path can be unavailable there too.
        $processes = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ProcessName)) -ErrorAction SilentlyContinue |
            ForEach-Object {
                $path = $null
                try { $path = $_.Path } catch { }
                [pscustomobject]@{
                    ProcessId = $_.Id
                    ExecutablePath = $path
                    CommandLine = $null
                }
            })
    }
    return $processes
}

$desktopName = Split-Path -Leaf $publishedExe
$runningBeforePublish = @(Get-RunningDesktopProcess -ProcessName $desktopName)
if ($runningBeforePublish.Count -gt 0) {
    $knownPaths = @($runningBeforePublish |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) } |
        ForEach-Object { [IO.Path]::GetFullPath($_.ExecutablePath) } |
        Select-Object -Unique)
    $expectedDesktopPath = if (-not [string]::IsNullOrWhiteSpace($override)) {
        [IO.Path]::GetFullPath($override)
    }
    else {
        [IO.Path]::GetFullPath($publishedExe)
    }
    if ($knownPaths -contains $expectedDesktopPath) {
        Write-Host "WhisperX Atom Desktop is already running from the current published build."
        return
    }
    if ($knownPaths.Count -gt 0) {
        throw ("DESKTOP_ALREADY_RUNNING_DIFFERENT_BUILD: {0}. Close the old Desktop instance before rebuilding." -f ($knownPaths -join "; "))
    }
    Write-Warning "DESKTOP_PROCESS_PATH_UNAVAILABLE: an existing Desktop process was found, but its path could not be inspected. Rebuilding is skipped."
    return
}

$candidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($override)) {
    $overridePath = [IO.Path]::GetFullPath($override)
    if (-not (Test-Path -LiteralPath $overridePath -PathType Leaf)) {
        throw "WHISPERX_DESKTOP_EXE does not exist: $overridePath"
    }
    $candidates.Add($overridePath)
}
else {
    $publishRequired = $ForcePublish -or -not (Test-Path -LiteralPath $publishedExe -PathType Leaf)
    if (-not $publishRequired) {
        $sourceRoots = @(
            (Join-Path $repoRoot "apps\desktop\WhisperX.Atom.Desktop"),
            (Join-Path $repoRoot "apps\recorder-agent")
        )
        $latestSource = Get-ChildItem -LiteralPath $sourceRoots -Recurse -File |
            Where-Object {
                $_.FullName -notmatch "\\(bin|obj)\\" -and
                $_.Extension -in @(".cs", ".xaml", ".csproj", ".props", ".targets", ".json")
            } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $latestSource -and $latestSource.LastWriteTimeUtc -gt (Get-Item -LiteralPath $publishedExe).LastWriteTimeUtc) {
            $publishRequired = $true
            Write-Host "Published Desktop is stale; rebuilding from current sources."
        }
    }
    if (-not $publishRequired) {
        $candidates.Add($publishedExe)
    }
}

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
        $publishArgs = @(
            "publish", $project, "--configuration", $Configuration, "--runtime", "win-x64",
            "--self-contained", "true", "-p:WindowsPackageType=None",
            "-p:WindowsAppSDKSelfContained=true", "-p:PublishSingleFile=false",
            "-p:NuGetAudit=false", "--output", $publishedDesktop, "--no-restore"
        )
        & $dotnet.Source @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Desktop publish failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    $desktopExe = $publishedExe
    if (-not (Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
        throw "Desktop executable was not created: $desktopExe"
    }
}

$desktopExe = (Resolve-Path -LiteralPath $desktopExe).Path
$workingDirectory = Split-Path -Parent $desktopExe
$existingDesktop = @(Get-RunningDesktopProcess -ProcessName $desktopName |
    Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($desktopExe)) })
if ($existingDesktop.Count -gt 0) {
    Write-Host "WhisperX Atom Desktop is already running."
    return
}
$process = Start-Process -FilePath $desktopExe -WorkingDirectory $workingDirectory -PassThru
Write-Host "WhisperX Atom Desktop started. PID=$($process.Id)"
Write-Host "Executable: $desktopExe"

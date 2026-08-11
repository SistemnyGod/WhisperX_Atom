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

    # Use the local process table so the launcher can inspect MainWindowHandle.
    # A process can remain alive after WinUI failed to create a visible window;
    # treating that process as a successful launch makes subsequent .bat runs
    # silently do nothing.
    return @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ProcessName)) -ErrorAction SilentlyContinue |
        ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { }
            [pscustomobject]@{
                ProcessId = $_.Id
                ExecutablePath = $path
                CommandLine = $null
                MainWindowHandle = $_.MainWindowHandle
                Responding = $_.Responding
            }
        })
}

function Test-DesktopWindowReady {
    param([object]$Process)

    try {
        return ([IntPtr]$Process.MainWindowHandle -ne [IntPtr]::Zero -and $Process.Responding)
    }
    catch {
        return $false
    }
}

function Stop-StaleDesktopProcess {
    param([object]$Process)

    Write-Warning ("DESKTOP_STALE_PROCESS: PID {0} has no visible window; restarting it." -f $Process.ProcessId)
    Stop-Process -Id ([int]$Process.ProcessId) -Force -ErrorAction Stop
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $stillRunning = Get-Process -Id ([int]$Process.ProcessId) -ErrorAction SilentlyContinue
    } while ($null -ne $stillRunning -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -ne $stillRunning) {
        throw "DESKTOP_STALE_PROCESS_STOP_FAILED: PID $($Process.ProcessId) did not stop."
    }
}

function Wait-ForDesktopWindow {
    param(
        [int]$ProcessId,
        [int]$TimeoutSeconds = 30,
        [int]$StableSeconds = 5
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds))
    $readySince = $null
    do {
        $candidate = Get-RunningDesktopProcess $desktopName |
            Where-Object { $_.ProcessId -eq $ProcessId } |
            Select-Object -First 1
        if ($null -eq $candidate) {
            throw "DESKTOP_EXITED_BEFORE_WINDOW: PID $ProcessId exited before creating a window."
        }
        if (Test-DesktopWindowReady $candidate) {
            if ($null -eq $readySince) {
                $readySince = [DateTimeOffset]::UtcNow
            }
            if ([DateTimeOffset]::UtcNow -ge $readySince.AddSeconds([Math]::Max(1, $StableSeconds))) {
                return $candidate
            }
        }
        else {
            $readySince = $null
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "DESKTOP_WINDOW_NOT_READY: PID $ProcessId is running but has no visible responsive window."
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
        $matching = @($runningBeforePublish | Where-Object {
            $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $expectedDesktopPath
        })
        $ready = @($matching | Where-Object { Test-DesktopWindowReady $_ })
        if ($ready.Count -gt 0) {
            Write-Host "WhisperX Atom Desktop is already running from the current published build."
            return
        }
        foreach ($stale in $matching) { Stop-StaleDesktopProcess $stale }
    }
    elseif ($knownPaths.Count -gt 0) {
        throw ("DESKTOP_ALREADY_RUNNING_DIFFERENT_BUILD: {0}. Close the old Desktop instance before rebuilding." -f ($knownPaths -join "; "))
    }
    else {
        $unknownPath = @($runningBeforePublish | Where-Object { [string]::IsNullOrWhiteSpace($_.ExecutablePath) })
        $readyUnknown = @($unknownPath | Where-Object { Test-DesktopWindowReady $_ })
        if ($readyUnknown.Count -gt 0) {
            Write-Warning "DESKTOP_PROCESS_PATH_UNAVAILABLE: a visible Desktop process is already running; launch was not duplicated."
            return
        }
        foreach ($stale in $unknownPath) { Stop-StaleDesktopProcess $stale }
    }
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
    $ready = @($existingDesktop | Where-Object { Test-DesktopWindowReady $_ })
    if ($ready.Count -gt 0) {
        Write-Host "WhisperX Atom Desktop is already running."
        return
    }
    foreach ($stale in $existingDesktop) { Stop-StaleDesktopProcess $stale }
}
$process = Start-Process -FilePath $desktopExe -WorkingDirectory $workingDirectory -WindowStyle Normal -PassThru
Wait-ForDesktopWindow -ProcessId $process.Id | Out-Null
Write-Host "WhisperX Atom Desktop started. PID=$($process.Id)"
Write-Host "Executable: $desktopExe"

[CmdletBinding()]
param(
    [string]$ExecutablePath = "",
    [switch]$Rebuild,
    [int]$ReadyTimeoutSeconds = 20,
    [string]$DataRoot = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
. (Join-Path $PSScriptRoot "Resolve-RecorderRuntime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo
$runtimeRoot = Get-WhisperXRuntimeRoot -RepoPath $repo
$pidPath = Join-Path $runtimeRoot "recorder-host.pid"
$stdoutPath = Join-Path $runtimeRoot "recorder-host.stdout.log"
$stderrPath = Join-Path $runtimeRoot "recorder-host.stderr.log"
$engine = (Resolve-RecorderRuntime).CaptureEngine
$audioGraph = [string]::Equals($engine, "AUDIOGRAPH", [StringComparison]::OrdinalIgnoreCase)
$publishedRoot = if ($audioGraph) { Join-Path $repo "artifacts\recorder-host-current" } else { Join-Path $repo "artifacts\recorder-current" }
$defaultExe = if ($audioGraph) { Join-Path $publishedRoot "WhisperX.Atom.Recorder.Host.exe" } else { Join-Path $publishedRoot "WhisperX.Atom.Recorder.Service.exe" }
$project = if ($audioGraph) { Join-Path $repo "apps\recorder-host\WhisperX.Atom.Recorder.Host.csproj" } else { Join-Path $repo "apps\recorder-agent\WhisperX.Atom.Recorder.Service.csproj" }
$processName = if ($audioGraph) { "WhisperX.Atom.Recorder.Host" } else { "WhisperX.Atom.Recorder.Service" }
$pipeName = if ($audioGraph) { "WhisperXAtomRecorderHost" } else { "WhisperXAtomAgent" }

function Get-RecorderProcess([string]$path) {
    $fullPath = [IO.Path]::GetFullPath($path)
    @(Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq $fullPath } catch { $false }
    }) | Select-Object -First 1
}

function Test-RecorderPipe {
    $pipe = $null
    $reader = $null
    $writer = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect(1000)
        if (-not $pipe.IsConnected) { return $false }

        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 4096, $true)
        $writer.AutoFlush = $true
        $protocolVersion = if ($audioGraph) { 6 } else { 5 }
        $request = [ordered]@{
            command = "HEALTH"
            protocolVersion = $protocolVersion
            payload = @{}
        }
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 8))
        $readTask = $reader.ReadLineAsync()
        if (-not $readTask.Wait(1000)) { return $false }
        $line = $readTask.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($line)) { return $false }
        $response = $line | ConvertFrom-Json
        if ($null -eq $response) { return $false }

        $minimum = if ($null -ne $response.minimumSupportedProtocolVersion) { [int]$response.minimumSupportedProtocolVersion } else { 0 }
        $current = if ($null -ne $response.currentProtocolVersion) { [int]$response.currentProtocolVersion } else { [int]$response.protocolVersion }
        if ($minimum -gt $protocolVersion -or $current -lt $protocolVersion) { return $false }
        if ([string]$response.error -eq "IPC_VERSION_INCOMPATIBLE") { return $false }
        return $true
    }
    catch { return $false }
    finally {
        if ($null -ne $writer) { try { $writer.Dispose() } catch { } }
        if ($null -ne $reader) { try { $reader.Dispose() } catch { } }
        if ($null -ne $pipe) { try { $pipe.Dispose() } catch { } }
    }
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { $ExecutablePath = $defaultExe }
$publishRequired = $Rebuild -or -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)
if (-not $publishRequired) {
    $binaryStamp = (Get-Item -LiteralPath $ExecutablePath).LastWriteTimeUtc
    $sourceRoot = Split-Path -Parent $project
    $latestSource = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
        Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" -and $_.Extension -in @(".cs", ".csproj", ".props", ".targets", ".json") } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    $publishRequired = $null -ne $latestSource -and $latestSource.LastWriteTimeUtc -gt $binaryStamp
}
if ($publishRequired) {
    New-Item -ItemType Directory -Force -Path $publishedRoot | Out-Null
    $publishArgs = @(
        $project, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:NuGetAudit=false", "-o", $publishedRoot, "--no-restore"
    )
    & dotnet publish @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "RECORDER_HOST_BUILD_FAILED" }
}
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path

$anyHost = @(Get-Process -Name $processName -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($anyHost.Count -gt 0 -and (Test-RecorderPipe)) {
    # A live Host owns the canonical user spool regardless of which artifact
    # directory launched it. Starting a second binary would compete for raw
    # recovery and make the shared named pipe route commands nondeterministically.
    $anyHost[0].Id | Set-Content -LiteralPath $pidPath -Encoding ascii
    Write-Host "Recorder host is already running. PID=$($anyHost[0].Id)"
    return
}

$existing = Get-RecorderProcess $ExecutablePath
if ($null -ne $existing -and (Test-RecorderPipe)) {
    $existing.Id | Set-Content -LiteralPath $pidPath -Encoding ascii
    Write-Host "Recorder host is already running. PID=$($existing.Id)"
    return
}
if ($null -ne $existing) { Stop-Process -Id $existing.Id -Force -ErrorAction SilentlyContinue }

$dataRoot = if ([string]::IsNullOrWhiteSpace($DataRoot)) { "C:\ProgramData\WhisperXAtom\Agent" } else { [IO.Path]::GetFullPath($DataRoot) }
$dataRoot = New-Item -ItemType Directory -Force -Path $dataRoot | Select-Object -ExpandProperty FullName
$machineConfigPath = Join-Path $dataRoot "..\client-config.json"
$machineConfig = if (Test-Path -LiteralPath $machineConfigPath -PathType Leaf) { try { Get-Content -LiteralPath $machineConfigPath -Raw | ConvertFrom-Json } catch { $null } } else { $null }
$configPath = if ($audioGraph) {
    Join-Path (Join-Path $env:LOCALAPPDATA "WhisperXAtom") "Agent\agent-config.json"
} else {
    Join-Path $dataRoot "agent-config.json"
}
if (-not $audioGraph -and -not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "RECORDER_CONFIG_NOT_FOUND: $configPath" }
$configDirectory = Split-Path -Parent $configPath
New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
$sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$machineInstallationId = $null
if ($null -ne $machineConfig) {
    $installationProperty = $machineConfig.PSObject.Properties["installationId"]
    if ($null -ne $installationProperty) { $machineInstallationId = [string]$installationProperty.Value }
    if ([string]::IsNullOrWhiteSpace($machineInstallationId)) {
        $installationProperty = $machineConfig.PSObject.Properties["InstallationId"]
        if ($null -ne $installationProperty) { $machineInstallationId = [string]$installationProperty.Value }
    }
}
if ([string]::IsNullOrWhiteSpace($machineInstallationId) -and $audioGraph -and (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    try {
        $hostConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $hostInstallation = $hostConfig.PSObject.Properties["InstallationId"]
        if ($null -eq $hostInstallation) { $hostInstallation = $hostConfig.PSObject.Properties["installationId"] }
        if ($null -ne $hostInstallation) { $machineInstallationId = [string]$hostInstallation.Value }
    }
    catch { }
}
$env:AUDIO_CAPTURE_ENGINE = if ($audioGraph) { "AUDIOGRAPH" } else { "LEGACY_WASAPI" }
$env:ATOM_AGENT_CONFIG_PATH = $configPath
$env:ATOM_AGENT_DATA_ROOT = $dataRoot
$env:ATOM_AGENT_INSTALLATION_ID = $machineInstallationId
$env:ATOM_AGENT_ALLOWED_SID = $sid
$env:ATOM_AGENT_DPAPI_SCOPE = if ($audioGraph) { "CURRENT_USER" } else { "LOCAL_MACHINE" }
$toolDirectory = if (-not [string]::IsNullOrWhiteSpace($env:WHISPERX_FFMPEG_DIR)) { $env:WHISPERX_FFMPEG_DIR } else { Join-Path $repo "vendor\ffmpeg\win-x64" }
$bundledFfmpeg = Join-Path (Split-Path -Parent $ExecutablePath) "ffmpeg.exe"
$bundledFfprobe = Join-Path (Split-Path -Parent $ExecutablePath) "ffprobe.exe"
function Sync-PinnedTool([string]$source, [string]$destination) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { return }
    $sourceInfo = Get-Item -LiteralPath $source
    $destinationInfo = Get-Item -LiteralPath $destination -ErrorAction SilentlyContinue
    if ($null -eq $destinationInfo -or $destinationInfo.Length -ne $sourceInfo.Length) {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}
$sourceFfmpeg = Join-Path $toolDirectory "ffmpeg.exe"
$sourceFfprobe = Join-Path $toolDirectory "ffprobe.exe"
if ((Test-Path -LiteralPath $sourceFfmpeg -PathType Leaf) -and (Test-Path -LiteralPath $sourceFfprobe -PathType Leaf)) {
    Sync-PinnedTool $sourceFfmpeg $bundledFfmpeg
    Sync-PinnedTool $sourceFfprobe $bundledFfprobe
}
if (-not (Test-Path -LiteralPath $bundledFfmpeg -PathType Leaf) -or -not (Test-Path -LiteralPath $bundledFfprobe -PathType Leaf)) {
    throw "FFMPEG_UNAVAILABLE: bundled ffmpeg.exe and ffprobe.exe are required beside the Recorder executable."
}
$env:ATOM_AGENT_FFMPEG_PATH = $bundledFfmpeg
$env:ATOM_AGENT_FFPROBE_PATH = $bundledFfprobe

$process = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path -Parent $ExecutablePath) -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
$process.Id | Set-Content -LiteralPath $pidPath -Encoding ascii
$deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max(1, $ReadyTimeoutSeconds))
do {
    Start-Sleep -Milliseconds 500
    $alive = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($null -eq $alive) {
        $stderrTail = if (Test-Path -LiteralPath $stderrPath) { (Get-Content -LiteralPath $stderrPath -Tail 20 -ErrorAction SilentlyContinue) -join "`n" } else { "" }
        $stdoutTail = if (Test-Path -LiteralPath $stdoutPath) { (Get-Content -LiteralPath $stdoutPath -Tail 20 -ErrorAction SilentlyContinue) -join "`n" } else { "" }
        $tail = (@($stderrTail, $stdoutTail) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
        throw "RECORDER_HOST_EXITED: $tail"
    }
    if (Test-RecorderPipe) {
        Write-Host "Recorder host is ready. PID=$($process.Id)"
        return
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw "RECORDER_PIPE_NOT_READY"

[CmdletBinding()]
param(
    [string]$ExecutablePath = "",
    [switch]$Rebuild,
    [int]$ReadyTimeoutSeconds = 20
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "WhisperX.Runtime.ps1")
Set-WhisperXRuntimeEnvironment -RepoPath $repo
$runtimeRoot = Get-WhisperXRuntimeRoot -RepoPath $repo
$pidPath = Join-Path $runtimeRoot "recorder-host.pid"
$stdoutPath = Join-Path $runtimeRoot "recorder-host.stdout.log"
$stderrPath = Join-Path $runtimeRoot "recorder-host.stderr.log"
$publishedRoot = Join-Path $repo "artifacts\recorder-current"
$defaultExe = Join-Path $publishedRoot "WhisperX.Atom.Recorder.Service.exe"
$project = Join-Path $repo "apps\recorder-agent\WhisperX.Atom.Recorder.Service.csproj"

function Get-RecorderProcess([string]$path) {
    $fullPath = [IO.Path]::GetFullPath($path)
    @(Get-Process -Name "WhisperX.Atom.Recorder.Service" -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq $fullPath } catch { $false }
    }) | Select-Object -First 1
}

function Test-RecorderPipe {
    $pipe = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", "WhisperXAtomAgent", [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect(1000)
        return $pipe.IsConnected
    }
    catch { return $false }
    finally { if ($null -ne $pipe) { $pipe.Dispose() } }
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { $ExecutablePath = $defaultExe }
$publishRequired = $Rebuild -or -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)
if (-not $publishRequired) {
    $binaryStamp = (Get-Item -LiteralPath $ExecutablePath).LastWriteTimeUtc
    $latestSource = Get-ChildItem -LiteralPath (Join-Path $repo "apps\recorder-agent") -Recurse -File |
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

$existing = Get-RecorderProcess $ExecutablePath
if ($null -ne $existing -and (Test-RecorderPipe)) {
    $existing.Id | Set-Content -LiteralPath $pidPath -Encoding ascii
    Write-Host "Recorder host is already running. PID=$($existing.Id)"
    return
}
if ($null -ne $existing) { Stop-Process -Id $existing.Id -Force -ErrorAction SilentlyContinue }

$dataRoot = "C:\ProgramData\WhisperXAtom\Agent"
$configPath = Join-Path $dataRoot "agent-config.json"
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "RECORDER_CONFIG_NOT_FOUND: $configPath" }
$sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$env:ATOM_AGENT_CONFIG_PATH = $configPath
$env:ATOM_AGENT_DATA_ROOT = $dataRoot
$env:ATOM_AGENT_ALLOWED_SID = $sid
$ffmpeg = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
if ($null -eq $ffmpeg) { throw "FFMPEG_UNAVAILABLE" }
$env:ATOM_AGENT_FFMPEG_PATH = $ffmpeg.Source

$process = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path -Parent $ExecutablePath) -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
$process.Id | Set-Content -LiteralPath $pidPath -Encoding ascii
$deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max(1, $ReadyTimeoutSeconds))
do {
    Start-Sleep -Milliseconds 500
    $alive = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($null -eq $alive) {
        $tail = if (Test-Path -LiteralPath $stderrPath) { (Get-Content -LiteralPath $stderrPath -Tail 20 -ErrorAction SilentlyContinue) -join "`n" } else { "" }
        throw "RECORDER_HOST_EXITED: $tail"
    }
    if (Test-RecorderPipe) {
        Write-Host "Recorder host is ready. PID=$($process.Id)"
        return
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw "RECORDER_PIPE_NOT_READY"

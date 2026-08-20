param(
    [Parameter(Mandatory)]
    [string]$InstalledRoot,
    [int]$TimeoutSeconds = 10
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath($InstalledRoot).TrimEnd('\')
$recorderName = 'WhisperX.Atom.Recorder.Host'
$voiceName = 'WhisperX.Atom.Voice.Host'
$desktopName = 'WhisperX.Atom.Desktop'

function Get-ProcessImagePathSafe([Diagnostics.Process]$Process) {
    # Process.Path is the least-privileged query and works for a same-user
    # unpackaged Host even when MainModule is blocked by cross-bitness or
    # protected-process rules. Fall back to MainModule only when Path is not
    # available; never continue with an unverified process.
    try {
        $path = [string]$Process.Path
        if (-not [string]::IsNullOrWhiteSpace($path)) { return [IO.Path]::GetFullPath($path) }
    } catch { }
    try {
        $path = [string]$Process.MainModule.FileName
        if (-not [string]::IsNullOrWhiteSpace($path)) { return [IO.Path]::GetFullPath($path) }
    } catch { }
    throw "INSTALL_BLOCKED_UNINSPECTABLE_PROCESS: PID=$($Process.Id) Name=$($Process.ProcessName)"
}

function Test-InstalledProcess([Diagnostics.Process]$Process) {
    $path = Get-ProcessImagePathSafe $Process
    return $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CurrentUserProcess([Diagnostics.Process]$Process) {
    try {
        $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)" -ErrorAction Stop
        if ($null -eq $cim) { throw 'PROCESS_NOT_FOUND' }
        $owner = Invoke-CimMethod -InputObject $cim -MethodName GetOwnerSid -ErrorAction Stop
        $sid = [string]$owner.Sid
        $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if ([string]::IsNullOrWhiteSpace($sid)) { throw 'OWNER_SID_UNAVAILABLE' }
        if (-not [string]::Equals($sid, $currentSid, [StringComparison]::OrdinalIgnoreCase)) {
            throw "INSTALL_BLOCKED_OWNER_MISMATCH: PID=$($Process.Id)"
        }
    }
    catch {
        if ($_.Exception.Message -like 'INSTALL_BLOCKED_OWNER_MISMATCH*') { throw }
        throw "INSTALL_BLOCKED_UNINSPECTABLE_PROCESS: PID=$($Process.Id) Name=$($Process.ProcessName)"
    }
}

function Invoke-JsonPipe([string]$PipeName, [string]$Command, [int]$TimeoutMilliseconds = 2500) {
    $pipe = $null; $reader = $null; $writer = $null
    try {
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect($TimeoutMilliseconds)
        if (-not $pipe.IsConnected) { return $null }
        $reader = [IO.StreamReader]::new($pipe)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine(([ordered]@{ command = $Command; protocolVersion = 6; payload = @{} } | ConvertTo-Json -Compress -Depth 8))
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($TimeoutMilliseconds)) { return $null }
        $line = $task.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($line)) { return $null }
        return $line | ConvertFrom-Json
    }
    catch { return $null }
    finally {
        if ($null -ne $writer) { try { $writer.Dispose() } catch {} }
        if ($null -ne $reader) { try { $reader.Dispose() } catch {} }
        if ($null -ne $pipe) { try { $pipe.Dispose() } catch {} }
    }
}

function Wait-ProcessExit([Diagnostics.Process[]]$Processes, [DateTime]$Deadline) {
    do {
        $alive = @($Processes | Where-Object { -not $_.HasExited })
        if ($alive.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $Deadline)
    throw "INSTALL_PROCESS_DID_NOT_EXIT: $($alive.Id -join ',')"
}

function Get-RecorderStateDatabasePath {
    $commonData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    if ([string]::IsNullOrWhiteSpace($commonData)) { return $null }
    return Join-Path $commonData 'WhisperXAtom\Agent\agent.db'
}

# An existing durable spool means that the Recorder owns user data even when
# its process is currently down.  In that situation a missing Host/IPC is not
# evidence of idleness: it can be a crash, a pending finalization, or an
# active capture whose process has not been observed yet.  Do not let Setup
# copy new Recorder binaries until the canonical Host can answer HEALTH.
$stateDb = Get-RecorderStateDatabasePath
$recorderStateExists = $null -ne $stateDb -and (Test-Path -LiteralPath $stateDb -PathType Leaf)

# Fail closed if Windows cannot inspect the target process. Never terminate a
# process merely because it has a familiar name; path ownership is the gate.
$processes = @(
    Get-Process -Name $recorderName,$voiceName,$desktopName -ErrorAction SilentlyContinue |
        Where-Object { Test-InstalledProcess $_ }
)
foreach ($process in $processes) { Assert-CurrentUserProcess $process }

$drive = [IO.Path]::GetPathRoot($root)
$disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($drive.TrimEnd('\'))'" -ErrorAction SilentlyContinue
if ($null -ne $disk -and [int64]$disk.FreeSpace -lt 2GB) {
    throw "INSTALL_INSUFFICIENT_DISK_SPACE: $($disk.FreeSpace) bytes free"
}

$recorder = @($processes | Where-Object ProcessName -eq $recorderName)
if ($recorder.Count -eq 0 -and $recorderStateExists) {
    throw 'INSTALL_BLOCKED_RECORDER_STATE_UNKNOWN: durable Recorder database exists but the canonical Host/IPC is unavailable.'
}
if ($recorder.Count -gt 0) {
    $health = Invoke-JsonPipe 'WhisperXAtomRecorderHost' 'HEALTH'
    if ($null -eq $health) { throw 'INSTALL_BLOCKED_UNRESPONSIVE_RECORDER_HOST' }
    $activeSession = [string]$health.health.activeSessionId
    if (-not [string]::IsNullOrWhiteSpace($activeSession)) {
        throw "INSTALL_BLOCKED_ACTIVE_RECORDING: $activeSession"
    }
    $shutdown = Invoke-JsonPipe 'WhisperXAtomRecorderHost' 'SHUTDOWN'
    if ($null -eq $shutdown -or $shutdown.ok -ne $true) { throw 'INSTALL_RECORDER_SHUTDOWN_REJECTED' }
}

$voice = @($processes | Where-Object ProcessName -eq $voiceName)
if ($voice.Count -gt 0) {
    $shutdown = Invoke-JsonPipe 'WhisperXAtomVoiceHost' 'SHUTDOWN'
    if ($null -eq $shutdown -or $shutdown.ok -ne $true) { throw 'INSTALL_VOICE_HOST_SHUTDOWN_REJECTED' }
}

$desktop = @($processes | Where-Object ProcessName -eq $desktopName)
if ($desktop.Count -gt 0) { throw 'INSTALL_REQUIRES_DESKTOP_EXIT' }
Wait-ProcessExit ($recorder + $voice) ([DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds)))
Write-Host 'INSTALL_PREFLIGHT_OK=true'

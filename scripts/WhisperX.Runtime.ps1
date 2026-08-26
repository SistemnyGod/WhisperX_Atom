Set-StrictMode -Version Latest

function Import-WhisperXDotEnv {
    param([Parameter(Mandatory=$true)][string]$RepoPath)

    $envFile = Join-Path $RepoPath ".env"
    if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
        throw "ENV_FILE_NOT_FOUND: $envFile"
    }

    foreach ($line in Get-Content -LiteralPath $envFile) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) { continue }
        if ($trimmed -notmatch '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') { continue }
        $key = $Matches[1]
        $value = $Matches[2].Trim()
        if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        [Environment]::SetEnvironmentVariable($key, $value, "Process")
    }
}

function Set-WhisperXRuntimeEnvironment {
    param([Parameter(Mandatory=$true)][string]$RepoPath)

    Import-WhisperXDotEnv -RepoPath $RepoPath
    $env:PYTHONPATH = $RepoPath
    $postgresPort = if ($env:POSTGRES_HOST_PORT) { $env:POSTGRES_HOST_PORT } else { "55432" }
    $natsPort = if ($env:NATS_HOST_PORT) { $env:NATS_HOST_PORT } else { "4222" }
    $database = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { "whisperx_atom" }
    $databaseUser = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { "whisperx" }
    if ([string]::IsNullOrWhiteSpace($env:POSTGRES_PASSWORD)) { throw "POSTGRES_PASSWORD is missing from .env." }
    $env:DATABASE_URL = "host=127.0.0.1 port=$postgresPort dbname=$database user=$databaseUser password=$($env:POSTGRES_PASSWORD)"
    $env:NATS_URL = "nats://127.0.0.1:$natsPort"
    $env:WHISPERX_DATA_HOST = if ($env:WHISPERX_DATA_HOST) { $env:WHISPERX_DATA_HOST } else { "C:\WhisperXAtom\Data" }
    $env:GPU_WORKER_RUNTIME = "host"
    $env:GPU_WORKER_HEARTBEAT_STALE_SECONDS = if ($env:GPU_WORKER_HEARTBEAT_STALE_SECONDS) { $env:GPU_WORKER_HEARTBEAT_STALE_SECONDS } else { "75" }
    $env:DEVICE = "cuda"
    $env:REQUIRE_CUDA = "true"
    $env:TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD = "1"
    $env:LLM_HEALTH_HOST = "127.0.0.1"
    if (-not $env:LLM_HEALTH_PORT) { $env:LLM_HEALTH_PORT = "18080" }

    # PowerShell hosts can expose both PATH and Path as separate keys even
    # though Windows treats them case-insensitively. Start-Process rejects
    # that duplicate environment when spawning a worker. Keep one canonical
    # Path entry for every runtime child process.
    $pathValue = $env:Path
    try { Remove-Item Env:\PATH -ErrorAction SilentlyContinue } catch { }
    if (-not [string]::IsNullOrWhiteSpace($pathValue)) { $env:Path = $pathValue }
}

function Get-WhisperXRuntimeRoot {
    param([Parameter(Mandatory=$true)][string]$RepoPath)
    $path = Join-Path $RepoPath "artifacts\runtime"
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Write-WhisperXRuntimeState {
    param(
        [Parameter(Mandatory=$true)][string]$RepoPath,
        [Parameter(Mandatory=$true)]$State
    )
    $path = Join-Path (Get-WhisperXRuntimeRoot -RepoPath $RepoPath) "state.json"
    $merged = [ordered]@{}
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        try {
            $previous = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            foreach ($property in $previous.PSObject.Properties) { $merged[$property.Name] = $property.Value }
        } catch { }
    }
    if ($State -is [System.Collections.IDictionary]) {
        foreach ($key in $State.Keys) { $merged[[string]$key] = $State[$key] }
    } else {
        foreach ($property in $State.PSObject.Properties) { $merged[$property.Name] = $property.Value }
    }
    foreach ($key in @("Count", "IsReadOnly", "Keys", "Values", "IsFixedSize", "SyncRoot", "IsSynchronized")) {
        $merged.Remove($key)
    }
    $merged.generatedAtUtc = [DateTimeOffset]::UtcNow
    $merged | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
}

function Get-WhisperXProcessCommandLine {
    param([Parameter(Mandatory=$true)][int]$ProcessId)
    try {
        $row = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
        return [string]$row.CommandLine
    } catch { return "" }
}

function Get-WhisperXHostWorkerCandidates {
    param([Parameter(Mandatory=$true)][string]$PythonPath)
    $expected = ""
    try { $expected = [IO.Path]::GetFullPath($PythonPath).ToLowerInvariant() } catch { }
    $rows = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue)
    foreach ($row in $rows) {
        $commandLine = [string]$row.CommandLine
        if ($commandLine -notmatch 'workers\.ml_worker\.worker') { continue }
        $executable = [string]$row.ExecutablePath
        $isExpected = $false
        try { $isExpected = $expected -and ([IO.Path]::GetFullPath($executable).ToLowerInvariant() -eq $expected) } catch { }
        [pscustomobject]@{
            pid = [int]$row.ProcessId
            parentPid = [int]$row.ParentProcessId
            executablePath = $executable
            commandLine = $commandLine
            matchesPython = $isExpected
        }
    }
}

function Stop-WhisperXProcessTree {
    param([Parameter(Mandatory=$true)][int]$ProcessId)
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        & taskkill.exe /PID $ProcessId /T /F | Out-Null
    }
}

function Get-WhisperXHostWorkerStatus {
    param(
        [Parameter(Mandatory=$true)][string]$RepoPath,
        [string]$PythonPath = $env:WHISPERX_HOST_PYTHON
    )
    $pidPath = Join-Path (Get-WhisperXRuntimeRoot -RepoPath $RepoPath) "host-gpu-worker.pid"
    $workerId = $null
    $process = $null
    $commandLine = ""
    $executablePath = ""
    if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
        try { $workerId = [int](Get-Content -LiteralPath $pidPath -Raw).Trim() } catch { $workerId = $null }
        if ($workerId) {
            $process = Get-Process -Id $workerId -ErrorAction SilentlyContinue
            if ($process) {
                $commandLine = Get-WhisperXProcessCommandLine -ProcessId $workerId
                try { $executablePath = [string]$process.Path } catch { $executablePath = "" }
            }
        }
    }
    $probe = Join-Path $RepoPath "scripts\probe_host_gpu_worker.py"
    $heartbeat = $null
    $probeExit = 1
    if ($PythonPath -and (Test-Path -LiteralPath $PythonPath)) {
        $json = & $PythonPath $probe --json 2>$null
        $probeExit = $LASTEXITCODE
    # probe_host_gpu_worker emits a JSON heartbeat for a live STARTING worker
    # and exits non-zero until the model is ready.  Parse it regardless of the
    # exit code so callers can distinguish cold-start liveness from staleness.
    if ($json) {
        try { $heartbeat = ($json -join "`n") | ConvertFrom-Json } catch { $heartbeat = $null }
    }
    }
    $expectedPython = ""
    try { $expectedPython = [IO.Path]::GetFullPath($PythonPath).ToLowerInvariant() } catch { }
    $actualExecutable = ""
    try { $actualExecutable = [IO.Path]::GetFullPath($executablePath).ToLowerInvariant() } catch { }
    $commandMatchesPython = $false
    if ($PythonPath -and $commandLine -and $commandLine.ToLowerInvariant().Contains((Split-Path $PythonPath -Leaf).ToLowerInvariant())) {
        $commandMatchesPython = $true
    } elseif ($expectedPython -and $actualExecutable -and $expectedPython -eq $actualExecutable) {
        # Win32_Process.CommandLine can be unavailable for a service-owned
        # process even when its executable path is readable. The exact Python
        # executable path is a safe fallback identity check.
        $commandMatchesPython = $true
    }
    [pscustomobject]@{
        pid = $workerId
        processAlive = $null -ne $process
        commandLine = $commandLine
        executablePath = $executablePath
        commandMatchesPython = $commandMatchesPython
        heartbeatLive = $null -ne $heartbeat
        heartbeatReady = $null -ne $heartbeat -and [string]$heartbeat.status -in @("READY", "BUSY")
        heartbeat = $heartbeat
        pidPath = $pidPath
    }
}

function Test-WhisperXDesktopRunning {
    param([Parameter(Mandatory=$true)][string]$RepoPath)
    $published = Join-Path $RepoPath "artifacts\desktop\Desktop\WhisperX.Atom.Desktop.exe"
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='WhisperX.Atom.Desktop.exe'" -ErrorAction SilentlyContinue)
    foreach ($row in $processes) {
        if ($row.ExecutablePath -and ((Resolve-Path -LiteralPath $row.ExecutablePath -ErrorAction SilentlyContinue).Path -eq ((Resolve-Path -LiteralPath $published -ErrorAction SilentlyContinue).Path))) { return $true }
    }
    return $false
}
